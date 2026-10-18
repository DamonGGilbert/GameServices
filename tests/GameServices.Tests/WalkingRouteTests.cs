using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Map;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Routes;
using GameServices.Api.Walking.Wallet;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// The real server with a made-up neighbourhood already in its map cache: a grid of footpaths every 100 m, 4 km
/// across, with a busy main road (no pavement) running through the middle. Checkpoint radius is wide so the tests can
/// stand exactly on a checkpoint.
/// </summary>
public class RouteApiFactory : ApiFactory
{
    public const double StartLat = 51.5000, StartLon = -0.1000;
    public readonly string CacheFolder = Path.Combine(Path.GetTempPath(), "gs-route-test-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Map:CacheFolder", CacheFolder);
        WriteNeighbourhood();
    }

    // The same Overpass answer saved for every square around the start (the graph ignores repeated ways).
    private void WriteNeighbourhood()
    {
        string folder = Path.Combine(CacheFolder, "v" + MapQuery.Version);
        Directory.CreateDirectory(folder);
        byte[] gz = Gzip(BuildGrid());

        double n = 1 << MapQuery.Zoom;
        int cx = (int)Math.Floor((StartLon + 180.0) / 360.0 * n);
        double latRad = StartLat * Math.PI / 180.0;
        int cy = (int)Math.Floor((1 - Math.Log(Math.Tan(latRad) + 1 / Math.Cos(latRad)) / Math.PI) / 2 * n);
        for (int y = cy - 4; y <= cy + 4; y++)
            for (int x = cx - 4; x <= cx + 4; x++)
                File.WriteAllBytes(Path.Combine(folder, $"{MapQuery.Zoom}_{x}_{y}.json.gz"), gz);
    }

    public static string BuildGrid()
    {
        const int size = 41;          // 41 x 41 points, 100 m apart = 4 km square
        const double step = 100;
        double dLat = step / 110540.0, dLon = step / (111320.0 * Math.Cos(StartLat * Math.PI / 180.0));
        long Node(int r, int c) => 1_000_000 + r * size + c;
        (double, double) Pos(int r, int c) => (StartLat + (r - size / 2) * dLat, StartLon + (c - size / 2) * dLon);

        var ways = new List<object>();
        long wayId = 1;
        for (int r = 0; r < size; r++)
        {
            // Row 22 (just north of the start) is a busy main road without a pavement: it must never be walked along.
            bool mainRoad = r == size / 2 + 2;
            var nodes = new List<long>();
            var geom = new List<object>();
            for (int c = 0; c < size; c++) { nodes.Add(Node(r, c)); var p = Pos(r, c); geom.Add(new { lat = p.Item1, lon = p.Item2 }); }
            ways.Add(new { type = "way", id = wayId++, tags = mainRoad ? new Dictionary<string, string> { ["highway"] = "primary" } : new Dictionary<string, string> { ["highway"] = "footway" }, nodes, geometry = geom });
        }
        for (int c = 0; c < size; c++)
        {
            var nodes = new List<long>();
            var geom = new List<object>();
            for (int r = 0; r < size; r++) { nodes.Add(Node(r, c)); var p = Pos(r, c); geom.Add(new { lat = p.Item1, lon = p.Item2 }); }
            ways.Add(new { type = "way", id = wayId++, tags = new Dictionary<string, string> { ["highway"] = "footway" }, nodes, geometry = geom });
        }
        return JsonSerializer.Serialize(new { version = 0.6, elements = ways });
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest)) gzip.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(CacheFolder, true); } catch (IOException) { }
    }
}

/// <summary>
/// SAFE ROUTES: loops planned along the safest ways, checkpoints that must really be walked, and the rewards.
/// </summary>
public class WalkingRouteTests : IClassFixture<RouteApiFactory>
{
    private readonly RouteApiFactory factory;

    public WalkingRouteTests(RouteApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "r" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        return new Player(client, auth.User.Id);
    }

    // An offset that makes it about midday for the walker, whenever the tests run.
    private static int MiddayOffset() => Math.Clamp((int)Math.Round((720 - DateTime.UtcNow.TimeOfDay.TotalMinutes) / 15.0) * 15, -720, 720);

    private async Task<RouteDto> Plan(Player p, string kind, int? offset = null)
    {
        HttpResponseMessage response = await p.Http.PostAsJsonAsync("/api/walking/routes",
            new RoutePlanRequest(kind, RouteApiFactory.StartLat, RouteApiFactory.StartLon, offset ?? MiddayOffset(), "park"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RouteDto>())!;
    }

    private async Task WithDb(Func<AppDbContext, Task> change)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    // Pretends the walker set off a while ago and has walked plenty of steps.
    private Task WalkedAWhile(Player p, Guid routeId, long steps = 0) => WithDb(async db =>
    {
        WalkingRoute r = await db.WalkingRoutes.FirstAsync(x => x.Id == routeId);
        r.LastCheckpointUtc = DateTime.UtcNow.AddHours(-1);
        if (steps > 0)
        {
            WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, p.Id, CancellationToken.None);
            profile.LifetimeSteps = r.StepsAtStart + steps;
        }
    });

    private async Task<CheckpointResult> WalkWholeRoute(Player p, RouteDto route)
    {
        CheckpointResult? last = null;
        for (int i = 0; i < route.Checkpoints.Count; i++)
        {
            await WalkedAWhile(p, route.Id, steps: route.Metres);
            double[] c = route.Checkpoints[i];
            HttpResponseMessage response = await p.Http.PostAsJsonAsync($"/api/walking/routes/{route.Id}/checkpoint", new CheckpointRequest(i, c[0], c[1]));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            last = await response.Content.ReadFromJsonAsync<CheckpointResult>();
        }
        return last!;
    }

    // ------------------------------------------------------------------ planning

    [Fact]
    public void Main_roads_without_a_pavement_are_never_part_of_the_walking_network()
    {
        using JsonDocument noPavement = JsonDocument.Parse("{\"highway\":\"primary\"}");
        using JsonDocument withPavement = JsonDocument.Parse("{\"highway\":\"primary\",\"sidewalk\":\"both\"}");
        using JsonDocument privatePath = JsonDocument.Parse("{\"highway\":\"footway\",\"access\":\"private\"}");
        using JsonDocument park = JsonDocument.Parse("{\"highway\":\"footway\"}");

        Assert.Null(RouteGraph.SafetyFactor(noPavement.RootElement, out _));
        Assert.Null(RouteGraph.SafetyFactor(privatePath.RootElement, out _));
        Assert.Equal(5.0, RouteGraph.SafetyFactor(withPavement.RootElement, out bool busy));
        Assert.True(busy);
        Assert.Equal(1.0, RouteGraph.SafetyFactor(park.RootElement, out _));
    }

    [Theory]
    [InlineData("short", 1000)]
    [InlineData("medium", 3000)]
    [InlineData("long", 5000)]
    public async Task A_loop_of_about_the_right_length_starts_and_ends_where_you_are(string kind, int target)
    {
        RouteDto route = await Plan(await NewPlayer(), kind);

        Assert.InRange(route.Metres, target * 0.7, target * 1.35);
        Assert.InRange(route.Checkpoints.Count, 4, 10);
        Assert.Equal(1.0, route.QuietShare, 2);   // all footpaths: the main road was never used
        Assert.Equal(0, route.BusyStretches);
        double[] start = route.Path[0], finish = route.Checkpoints[^1];
        Assert.True(RouteGraph.Distance(start[0], start[1], finish[0], finish[1]) < 1);
        Assert.True(RouteGraph.Distance(start[0], start[1], RouteApiFactory.StartLat, RouteApiFactory.StartLon) < 150);
    }

    [Fact]
    public async Task Where_the_map_is_not_ready_yet_the_walker_is_asked_to_try_again()
    {
        Player p = await NewPlayer();
        // Somewhere far from the made-up neighbourhood: nothing in the cache (and no Overpass in tests).
        HttpResponseMessage response = await p.Http.PostAsJsonAsync("/api/walking/routes", new RoutePlanRequest("short", 10.0, 10.0, 0));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ------------------------------------------------------------------ walking it

    [Fact]
    public async Task Walking_every_checkpoint_completes_the_route_and_pays_Glimmer()
    {
        Player p = await NewPlayer();
        RouteDto route = await Plan(p, "short");

        CheckpointResult done = await WalkWholeRoute(p, route);

        Assert.True(done.Completed);
        Assert.Equal(40, done.RewardGlimmer);
        Assert.Equal(40, (await p.Http.GetFromJsonAsync<WalletDto>("/api/walking/wallet"))!.Glimmer);
        Assert.Equal(HttpStatusCode.NoContent, (await p.Http.GetAsync("/api/walking/routes/active")).StatusCode);
    }

    [Fact]
    public async Task Checkpoints_must_be_reached_in_order_close_by_and_at_walking_pace()
    {
        Player p = await NewPlayer();
        RouteDto route = await Plan(p, "short");
        double[] first = route.Checkpoints[0], second = route.Checkpoints[1];

        await WalkedAWhile(p, route.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Http.PostAsJsonAsync($"/api/walking/routes/{route.Id}/checkpoint", new CheckpointRequest(1, second[0], second[1]))).StatusCode);   // skipped one
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Http.PostAsJsonAsync($"/api/walking/routes/{route.Id}/checkpoint", new CheckpointRequest(0, first[0] + 0.01, first[1]))).StatusCode);   // 1 km away
        Assert.Equal(HttpStatusCode.OK, (await p.Http.PostAsJsonAsync($"/api/walking/routes/{route.Id}/checkpoint", new CheckpointRequest(0, first[0], first[1]))).StatusCode);

        // Straight on to the next one, seconds later: faster than anyone walks.
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Http.PostAsJsonAsync($"/api/walking/routes/{route.Id}/checkpoint", new CheckpointRequest(1, second[0], second[1]))).StatusCode);
    }

    [Fact]
    public async Task The_finish_waits_until_the_steps_add_up()
    {
        Player p = await NewPlayer();
        RouteDto route = await Plan(p, "short");

        for (int i = 0; i < route.Checkpoints.Count - 1; i++)
        {
            await WalkedAWhile(p, route.Id);
            double[] c = route.Checkpoints[i];
            await p.Http.PostAsJsonAsync($"/api/walking/routes/{route.Id}/checkpoint", new CheckpointRequest(i, c[0], c[1]));
        }

        await WalkedAWhile(p, route.Id);   // ...but no steps were walked
        double[] finish = route.Checkpoints[^1];
        HttpResponseMessage response = await p.Http.PostAsJsonAsync($"/api/walking/routes/{route.Id}/checkpoint", new CheckpointRequest(route.Checkpoints.Count - 1, finish[0], finish[1]));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ------------------------------------------------------------------ rewards and limits

    [Fact]
    public async Task The_weekly_challenge_guarantees_a_rare_monster_and_happens_in_daylight_only()
    {
        Player p = await NewPlayer();
        int night = MiddayOffset() + 720 > 720 ? MiddayOffset() - 720 : MiddayOffset() + 720;   // twelve hours from midday
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Http.PostAsJsonAsync("/api/walking/routes",
            new RoutePlanRequest("challenge", RouteApiFactory.StartLat, RouteApiFactory.StartLon, night))).StatusCode);

        RouteDto route = await Plan(p, "challenge");
        CheckpointResult done = await WalkWholeRoute(p, route);

        Assert.True(done.Monster);
        WalkingMonsterDto prize = (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))!.Single(m => m.Source == "route");
        Assert.True(prize.RarityTier >= 2);
        Assert.False(prize.Seen);

        Assert.Equal(HttpStatusCode.BadRequest, (await p.Http.PostAsJsonAsync("/api/walking/routes",
            new RoutePlanRequest("challenge", RouteApiFactory.StartLat, RouteApiFactory.StartLon, MiddayOffset()))).StatusCode);   // once a week
    }

    [Fact]
    public async Task Two_rewarded_routes_a_day()
    {
        Player p = await NewPlayer();
        await WalkWholeRoute(p, await Plan(p, "short"));
        await WalkWholeRoute(p, await Plan(p, "short"));

        HttpResponseMessage third = await p.Http.PostAsJsonAsync("/api/walking/routes", new RoutePlanRequest("short", RouteApiFactory.StartLat, RouteApiFactory.StartLon, MiddayOffset()));
        Assert.Equal(HttpStatusCode.BadRequest, third.StatusCode);

        List<RouteOptionDto> options = (await p.Http.GetFromJsonAsync<List<RouteOptionDto>>($"/api/walking/routes/options?utcOffsetMinutes={MiddayOffset()}"))!;
        Assert.False(options.Single(o => o.Kind == "short").Available);
        Assert.True(options.Single(o => o.Kind == "challenge").Available);
    }
}
