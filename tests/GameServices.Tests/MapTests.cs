using System.Net;
using GameServices.Api.Map;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameServices.Tests;

/// <summary>A pretend Overpass: counts how often it's asked, and can be told to fail or to be slow.</summary>
public class FakeUpstream : IMapUpstream
{
    public int Calls;
    public bool Fail;
    public TimeSpan Delay = TimeSpan.Zero;

    public async Task<UpstreamResult> FetchAsync(int x, int y, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        return Fail
            ? UpstreamResult.Fail("pretend outage")
            : UpstreamResult.Ok($"{{\"version\":0.6,\"elements\":[{{\"type\":\"way\",\"id\":{x}{y},\"tags\":{{\"highway\":\"footway\"}}}}]}}");
    }
}

/// <summary>The real server, with the pretend Overpass and its own empty map cache folder.</summary>
public class MapApiFactory : ApiFactory
{
    public readonly FakeUpstream Upstream = new();
    private readonly string cacheFolder = Path.Combine(Path.GetTempPath(), "gs-map-test-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Map:CacheFolder", cacheFolder);
        builder.UseSetting("Map:GapBetweenRequestsMs", "0");
        builder.UseSetting("RateLimit:MapRequestsPerMinute", "1000");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMapUpstream>();
            services.AddSingleton<IMapUpstream>(Upstream);
        });
    }

    /// <summary>Makes every saved square look a year old.</summary>
    public void AgeCache()
    {
        foreach (string file in Directory.GetFiles(cacheFolder, "*.gz", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-365));
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(cacheFolder, true); } catch (IOException) { }
    }
}

/// <summary>
/// THE MAP RELAY: each map square is fetched from OpenStreetMap once and shared, old copies stand in when Overpass is
/// down, and many players asking at once cause just one fetch.
/// </summary>
public class MapTests : IClassFixture<MapApiFactory>
{
    private readonly MapApiFactory factory;

    public MapTests(MapApiFactory factory)
    {
        this.factory = factory;
        factory.Upstream.Fail = false;
        factory.Upstream.Delay = TimeSpan.Zero;
    }

    [Fact]
    public async Task A_square_is_fetched_once_then_served_from_the_cache()
    {
        HttpClient client = factory.CreateClient();
        int before = factory.Upstream.Calls;

        HttpResponseMessage first = await client.GetAsync("/api/map/15/16106/10610");
        HttpResponseMessage second = await client.GetAsync("/api/map/15/16106/10610");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("\"elements\"", await second.Content.ReadAsStringAsync());
        Assert.Equal(before + 1, factory.Upstream.Calls);
    }

    [Fact]
    public async Task No_account_is_needed()
    {
        HttpResponseMessage response = await factory.CreateClient().GetAsync("/api/map/15/16000/10000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/map/14/16106/10610")]   // wrong zoom
    [InlineData("/api/map/15/-1/10610")]
    [InlineData("/api/map/15/32768/10610")]   // off the edge of the world
    public async Task Squares_that_do_not_exist_are_refused(string url)
    {
        HttpResponseMessage response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task With_Overpass_down_and_no_copy_the_player_is_told_to_try_again()
    {
        factory.Upstream.Fail = true;
        HttpResponseMessage response = await factory.CreateClient().GetAsync("/api/map/15/16200/10700");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task With_Overpass_down_an_old_copy_is_sent_and_marked_stale()
    {
        HttpClient client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/map/15/16300/10800")).StatusCode);

        factory.AgeCache();
        factory.Upstream.Fail = true;
        HttpResponseMessage response = await client.GetAsync("/api/map/15/16300/10800");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Map-Stale"));
        Assert.Contains("\"elements\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Many_players_asking_at_once_cause_one_fetch()
    {
        factory.Upstream.Delay = TimeSpan.FromMilliseconds(300);
        int before = factory.Upstream.Calls;
        HttpClient client = factory.CreateClient();

        HttpResponseMessage[] responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => client.GetAsync("/api/map/15/16400/10900")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(before + 1, factory.Upstream.Calls);
    }

    [Fact]
    public async Task Squares_are_sent_compressed_to_apps_that_accept_it()
    {
        // A client that does NOT unpack automatically, so we can see what was actually sent.
        HttpClient client = factory.CreateDefaultClient();
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");

        HttpResponseMessage response = await client.GetAsync("/api/map/15/16500/11000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
        string json = System.Text.Encoding.UTF8.GetString(MapTileStore.Gunzip(await response.Content.ReadAsByteArrayAsync()));
        Assert.Contains("\"elements\"", json);
    }
}
