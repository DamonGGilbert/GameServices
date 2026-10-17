using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Social;
using GameServices.Api.Walking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// THE WALKING GAME on a real server: reporting steps (and the server refusing unbelievable ones), comparing steps
/// with friends, cheers and step races.
/// </summary>
public class WalkingTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public WalkingTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "w" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        return new Player(client, auth.User.Id, name);
    }

    private static async Task MakeFriends(Player a, Player b)
    {
        await a.Http.PostAsJsonAsync("/api/friends/requests", new UsernameRequest(b.Name));
        Assert.Equal(HttpStatusCode.NoContent, (await b.Http.PostAsync($"/api/friends/requests/{a.Id}/accept", null)).StatusCode);
    }

    /// <summary>
    /// A time zone where it's about midday right now, so a test never runs just after "midnight" (when the cadence
    /// rule would rightly refuse thousands of steps).
    /// </summary>
    private static (string Day, int Offset) Midday()
    {
        DateTime now = DateTime.UtcNow;
        int offset = (int)Math.Round((720 - now.TimeOfDay.TotalMinutes) / 15.0) * 15;
        offset = Math.Clamp(offset, -720, 720);
        return (now.AddMinutes(offset).ToString("yyyy-MM-dd"), offset);
    }

    private static async Task<HttpResponseMessage> Report(Player p, int steps)
    {
        (string day, int offset) = Midday();
        return await p.Http.PostAsJsonAsync("/api/walking/steps", new StepReportRequest(day, steps, offset));
    }

    /// <summary>Pretends the last report was a while ago (so the 15-second report limit doesn't slow the tests down).</summary>
    private async Task RewindLastReport(Player p, TimeSpan by)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id);
        profile.LastReportUtc = profile.LastReportUtc!.Value - by;
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ steps

    [Fact]
    public async Task Reported_steps_count_for_today_and_for_life()
    {
        Player walker = await NewPlayer();

        HttpResponseMessage response = await Report(walker, 3200);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        StepReportResult result = (await response.Content.ReadFromJsonAsync<StepReportResult>())!;
        Assert.Equal(3200, result.AcceptedToday);
        Assert.Equal(3200, result.LifetimeSteps);

        WalkingMeDto me = (await walker.Http.GetFromJsonAsync<WalkingMeDto>("/api/walking/me"))!;
        Assert.Equal(3200, me.Today);
        Assert.Equal(3200, me.Week);
        Assert.Equal(3200, me.Lifetime);
    }

    [Fact]
    public async Task Reporting_again_straight_away_is_refused()
    {
        Player walker = await NewPlayer();
        await Report(walker, 1000);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Report(walker, 1100)).StatusCode);
    }

    [Fact]
    public async Task An_impossible_jump_is_clipped_and_flagged_by_the_server()
    {
        Player cheat = await NewPlayer();
        await Report(cheat, 1000);
        await RewindLastReport(cheat, TimeSpan.FromMinutes(1));

        // One minute later, 30,000 more steps. Nobody walks that fast.
        StepReportResult result = (await (await Report(cheat, 31000)).Content.ReadFromJsonAsync<StepReportResult>())!;

        Assert.True(result.Rejected > 29000);
        Assert.True(result.Flagged);
        Assert.True(result.AcceptedToday < 1500);
    }

    [Fact]
    public async Task Steps_for_another_day_are_refused()
    {
        Player walker = await NewPlayer();
        string lastWeek = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");

        HttpResponseMessage response = await walker.Http.PostAsJsonAsync("/api/walking/steps", new StepReportRequest(lastWeek, 9000, 0));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------ comparing with friends

    [Fact]
    public async Task Friends_can_compare_steps_unless_they_choose_not_to_share()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);
        await Report(bob, 4800);

        FriendStepsDto seen = (await alice.Http.GetFromJsonAsync<List<FriendStepsDto>>("/api/walking/friends"))!.Single(f => f.UserId == bob.Id);
        Assert.True(seen.Shared);
        Assert.Equal(4800, seen.Today);

        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.PutAsJsonAsync("/api/walking/privacy", new PrivacyRequest(false))).StatusCode);

        FriendStepsDto hidden = (await alice.Http.GetFromJsonAsync<List<FriendStepsDto>>("/api/walking/friends"))!.Single(f => f.UserId == bob.Id);
        Assert.False(hidden.Shared);
        Assert.Equal(0, hidden.Today);
    }

    [Fact]
    public async Task Strangers_steps_are_never_listed()
    {
        Player alice = await NewPlayer();
        Player stranger = await NewPlayer();
        await Report(stranger, 2000);

        List<FriendStepsDto> list = (await alice.Http.GetFromJsonAsync<List<FriendStepsDto>>("/api/walking/friends"))!;

        Assert.DoesNotContain(list, f => f.UserId == stranger.Id);
    }

    // ------------------------------------------------------------------ cheers

    [Fact]
    public async Task A_cheer_arrives_and_can_be_marked_as_seen()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);

        Assert.Equal(HttpStatusCode.OK, (await alice.Http.PostAsJsonAsync($"/api/walking/cheers/{bob.Id}", new CheerRequest(0))).StatusCode);

        List<CheerDto> cheers = (await bob.Http.GetFromJsonAsync<List<CheerDto>>("/api/walking/cheers"))!;
        CheerDto cheer = Assert.Single(cheers);
        Assert.Equal(alice.Name, cheer.FromUsername);
        Assert.Equal(WalkingEndpoints.CheerPhrases[0], cheer.Text);

        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.PostAsJsonAsync("/api/walking/cheers/seen", new SeenRequest(cheer.Id))).StatusCode);
        Assert.Empty((await bob.Http.GetFromJsonAsync<List<CheerDto>>("/api/walking/cheers"))!);
    }

    [Fact]
    public async Task Cheers_are_for_friends_only_and_limited_per_day()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        Player stranger = await NewPlayer();
        await MakeFriends(alice, bob);

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.Http.PostAsJsonAsync($"/api/walking/cheers/{stranger.Id}", new CheerRequest(0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PostAsJsonAsync($"/api/walking/cheers/{bob.Id}", new CheerRequest(99))).StatusCode);

        for (int i = 0; i < WalkingEndpoints.MaxCheersPerFriendPerDay; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await alice.Http.PostAsJsonAsync($"/api/walking/cheers/{bob.Id}", new CheerRequest(1))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await alice.Http.PostAsJsonAsync($"/api/walking/cheers/{bob.Id}", new CheerRequest(1))).StatusCode);
    }

    // ------------------------------------------------------------------ step races

    private static async Task<List<RaceDto>> Races(Player p) => (await p.Http.GetFromJsonAsync<List<RaceDto>>("/api/walking/races"))!;

    [Fact]
    public async Task A_race_counts_only_steps_walked_after_it_starts_and_the_job_names_the_winner()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        await MakeFriends(alice, bob);

        // Steps before the race don't count.
        await Report(alice, 5000);
        await Report(bob, 1000);

        Assert.Equal(HttpStatusCode.OK, (await alice.Http.PostAsJsonAsync("/api/walking/races", new RaceRequest(bob.Id, 24))).StatusCode);
        RaceDto pending = Assert.Single(await Races(bob));
        Assert.Equal("Pending", pending.Status);
        Assert.False(pending.IChallenged);

        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.PostAsync($"/api/walking/races/{pending.Id}/accept", null)).StatusCode);

        // During the race: bob walks 3,000 more, alice 500 more.
        await RewindLastReport(alice, TimeSpan.FromMinutes(30));
        await RewindLastReport(bob, TimeSpan.FromMinutes(30));
        await Report(alice, 5500);
        await Report(bob, 4000);

        RaceDto live = Assert.Single(await Races(bob));
        Assert.Equal("Active", live.Status);
        Assert.Equal(3000, live.MySteps);
        Assert.Equal(500, live.TheirSteps);

        // A day later the background job finishes it.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await WalkingLifecycleService.RunOnce(db, DateTime.UtcNow.AddHours(25)));
        }

        RaceDto done = Assert.Single(await Races(bob));
        Assert.Equal("Finished", done.Status);
        Assert.Equal("Won", done.Result);
        Assert.Equal("Lost", Assert.Single(await Races(alice)).Result);
        Assert.Equal(1, (await bob.Http.GetFromJsonAsync<WalkingMeDto>("/api/walking/me"))!.RacesWon);
    }

    [Fact]
    public async Task You_can_only_race_friends_one_race_at_a_time_and_lengths_are_fixed()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        Player stranger = await NewPlayer();
        await MakeFriends(alice, bob);

        Assert.Equal(HttpStatusCode.Forbidden, (await alice.Http.PostAsJsonAsync("/api/walking/races", new RaceRequest(stranger.Id, 24))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PostAsJsonAsync("/api/walking/races", new RaceRequest(bob.Id, 5))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await alice.Http.PostAsJsonAsync("/api/walking/races", new RaceRequest(bob.Id, 72))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.Http.PostAsJsonAsync("/api/walking/races", new RaceRequest(alice.Id, 24))).StatusCode);
    }

    [Fact]
    public async Task A_challenge_can_be_declined_or_cancelled_and_old_ones_expire()
    {
        Player alice = await NewPlayer();
        Player bob = await NewPlayer();
        Player carol = await NewPlayer();
        await MakeFriends(alice, bob);
        await MakeFriends(alice, carol);

        await alice.Http.PostAsJsonAsync("/api/walking/races", new RaceRequest(bob.Id, 24));
        Guid toBob = (await Races(bob)).Single().Id;
        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.PostAsync($"/api/walking/races/{toBob}/decline", null)).StatusCode);
        Assert.Empty(await Races(bob));

        await alice.Http.PostAsJsonAsync("/api/walking/races", new RaceRequest(carol.Id, 24));
        Guid toCarol = (await Races(carol)).Single().Id;

        // Carol can't cancel alice's challenge (only decline it); alice can cancel it.
        Assert.Equal(HttpStatusCode.NotFound, (await carol.Http.PostAsync($"/api/walking/races/{toCarol}/cancel", null)).StatusCode);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await WalkingLifecycleService.RunOnce(db, DateTime.UtcNow.AddHours(25));
        }

        // Nobody answered within a day: it expired.
        Assert.Empty(await Races(carol));
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Http.PostAsync($"/api/walking/races/{toCarol}/cancel", null)).StatusCode);
    }
}
