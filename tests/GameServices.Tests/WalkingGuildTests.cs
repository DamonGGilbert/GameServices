using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Guilds;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// WALKING GUILDS: the weekly Guild Journey (shared step target and fair, capped rewards) and the weekend
/// guild-vs-guild step race (pairing, scoring the top walkers, and the winner's rating and war wins).
/// </summary>
public class WalkingGuildTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public WalkingGuildTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(string game = GameIds.Walking)
    {
        HttpClient client = factory.CreateClient();
        string name = "g" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, game);
        return new Player(client, auth.User.Id, name);
    }

    // A guild of the given players (the first one creates it, the rest join).
    private static async Task<Guid> MakeGuild(params Player[] players)
    {
        HttpResponseMessage made = await players[0].Http.PostAsJsonAsync("/api/guilds",
            new CreateGuildRequest("Walk " + Guid.NewGuid().ToString("N")[..8], "WLK", "for tests", true));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        Guid id = (await made.Content.ReadFromJsonAsync<GuildSummary>())!.Id;

        foreach (Player p in players.Skip(1))
        {
            Assert.True((await p.Http.PostAsync($"/api/guilds/{id}/join", null)).IsSuccessStatusCode);
        }

        return id;
    }

    // Writes step days straight into the database (as if the reports had arrived on those days).
    private async Task SetSteps(Player p, DateTime day, int steps)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        string key = WalkingGuilds.DayKey(day);
        WalkingDay? row = await db.WalkingDays.FirstOrDefaultAsync(d => d.UserId == p.Id && d.Day == key);

        if (row == null)
        {
            db.WalkingDays.Add(new WalkingDay { UserId = p.Id, Day = key, Steps = steps });
        }
        else
        {
            row.Steps = steps;
        }

        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------ the rules

    [Fact]
    public void The_journey_target_grows_with_the_guild()
    {
        Assert.Equal(35_000, WalkingGuilds.TargetFor(1));
        Assert.Equal(140_000, WalkingGuilds.TargetFor(4));
        Assert.Equal(35_000, WalkingGuilds.TargetFor(0)); // never zero
    }

    [Fact]
    public void Journey_rewards_are_fair_and_capped()
    {
        const int target = 70_000; // a guild of 2: a fair share is 35,000 each

        Assert.Equal(0, WalkingGuilds.JourneyReward(50_000, target, 0, 2));                       // didn't walk: nothing
        Assert.Equal(350, WalkingGuilds.JourneyReward(70_000, target, 35_000, 2));                // done, full share: max + bonus
        Assert.Equal(350, WalkingGuilds.JourneyReward(200_000, target, 190_000, 2));              // walking even more: still capped
        Assert.Equal(125, WalkingGuilds.JourneyReward(35_000, target, 35_000, 2));                // guild halfway: half the max, no bonus
        Assert.Equal(100 + 125, WalkingGuilds.JourneyReward(70_000, target, 17_500, 2));          // done, half your share: half the max + bonus
    }

    [Fact]
    public void The_week_starts_on_monday_and_only_the_top_five_walkers_count()
    {
        Assert.Equal(new DateTime(2026, 10, 12), WalkingGuilds.WeekStart(new DateTime(2026, 10, 18, 23, 0, 0))); // Sunday
        Assert.Equal(new DateTime(2026, 10, 12), WalkingGuilds.WeekStart(new DateTime(2026, 10, 12, 0, 1, 0)));  // Monday
        Assert.Equal(15, WalkingGuilds.RaceScore(new[] { 1, 2, 3, 4, 5 }));
        Assert.Equal(20, WalkingGuilds.RaceScore(new[] { 6, 1, 2, 3, 4, 5 })); // the 1 doesn't count
    }

    // ------------------------------------------------------------------ the Journey on a real server

    [Fact]
    public async Task You_need_a_walking_guild_for_the_journey()
    {
        Player loner = await NewPlayer();
        Assert.Equal(HttpStatusCode.NotFound, (await loner.Http.GetAsync("/api/walking/guild/journey")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await loner.Http.GetAsync("/api/walking/guild/race")).StatusCode);
    }

    [Fact]
    public async Task The_journey_adds_up_everyones_steps_this_week()
    {
        Player a = await NewPlayer();
        Player b = await NewPlayer();
        await MakeGuild(a, b);

        DateTime monday = WalkingGuilds.WeekStart(DateTime.UtcNow);
        await SetSteps(a, monday, 4000);
        await SetSteps(b, monday, 2500);
        await SetSteps(b, monday.AddDays(-1), 9999); // last week: not this Journey

        JourneyDto journey = (await a.Http.GetFromJsonAsync<JourneyDto>("/api/walking/guild/journey"))!;

        Assert.Equal(70_000, journey.Target);
        Assert.Equal(6500, journey.Total);
        Assert.Equal(4000, journey.MySteps);
        Assert.Equal(2, journey.Members.Count);
        Assert.Equal(a.Name, journey.Members[0].Username); // best walker first
    }

    [Fact]
    public async Task Last_weeks_reward_can_be_collected_once()
    {
        Player a = await NewPlayer();
        Player b = await NewPlayer();
        await MakeGuild(a, b);

        // Last week the pair walked 70,000 between them: the Journey was completed.
        DateTime lastMonday = WalkingGuilds.WeekStart(DateTime.UtcNow).AddDays(-7);
        await SetSteps(a, lastMonday, 40_000);
        await SetSteps(b, lastMonday.AddDays(1), 30_000);

        JourneyDto journey = (await a.Http.GetFromJsonAsync<JourneyDto>("/api/walking/guild/journey"))!;
        Assert.True(journey.HasLastWeek);
        Assert.False(journey.LastWeekClaimed);
        Assert.Equal(350, journey.LastWeekGlimmer);

        HttpResponseMessage claim = await a.Http.PostAsync("/api/walking/guild/journey/claim", null);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Equal(350, (await claim.Content.ReadFromJsonAsync<JourneyClaimResult>())!.Glimmer);

        Assert.Equal(HttpStatusCode.Conflict, (await a.Http.PostAsync("/api/walking/guild/journey/claim", null)).StatusCode);

        journey = (await a.Http.GetFromJsonAsync<JourneyDto>("/api/walking/guild/journey"))!;
        Assert.True(journey.LastWeekClaimed);

        // b walked a bit less than a fair share, so earns a bit less.
        HttpResponseMessage bClaim = await b.Http.PostAsync("/api/walking/guild/journey/claim", null);
        Assert.Equal(100 + (int)Math.Round(250 * (30_000 / 35_000.0)), (await bClaim.Content.ReadFromJsonAsync<JourneyClaimResult>())!.Glimmer);
    }

    [Fact]
    public async Task Nothing_to_collect_if_you_didnt_walk_last_week()
    {
        Player a = await NewPlayer();
        await MakeGuild(a);

        Assert.Equal(HttpStatusCode.BadRequest, (await a.Http.PostAsync("/api/walking/guild/journey/claim", null)).StatusCode);
    }

    // ------------------------------------------------------------------ the weekend step race

    [Fact]
    public async Task On_saturday_walking_guilds_are_paired_but_card_game_guilds_are_not()
    {
        Guid walkers = await MakeGuild(await NewPlayer(), await NewPlayer());
        Guid others = await MakeGuild(await NewPlayer(), await NewPlayer());
        Guid cards = await MakeGuild(await NewPlayer(GameIds.Yugioh), await NewPlayer(GameIds.Yugioh));
        Guid alone = await MakeGuild(await NewPlayer());

        DateTime saturday = new(2030, 1, 5, 0, 30, 0, DateTimeKind.Utc);
        Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);

        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await WalkingGuilds.RunOnce(db, saturday);

        List<GuildStepRace> races = await db.GuildStepRaces.AsNoTracking().Where(r => r.StartsUtc == saturday.Date).ToListAsync();
        bool InARace(Guid id) => races.Any(r => r.GuildAId == id || r.GuildBId == id);

        Assert.True(InARace(walkers) || InARace(others)); // with an odd number of guilds one may wait, but not both of ours
        Assert.False(InARace(cards));
        Assert.False(InARace(alone)); // needs at least 2 members

        // Running again the same weekend doesn't pair anyone twice.
        int before = races.Count;
        await WalkingGuilds.RunOnce(db, saturday.AddMinutes(5));
        Assert.True(await db.GuildStepRaces.CountAsync(r => r.StartsUtc == saturday.Date) <= before + 1);
    }

    [Fact]
    public async Task The_top_walkers_win_the_race_and_the_guild_moves_up()
    {
        Player[] fast = { await NewPlayer(), await NewPlayer() };
        Player[] slow = { await NewPlayer(), await NewPlayer() };
        Guid fastGuild = await MakeGuild(fast);
        Guid slowGuild = await MakeGuild(slow);

        // A race on a weekend long ago, so no other test's race can get mixed up with it.
        DateTime saturday = new(2021, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GuildStepRaces.Add(new GuildStepRace { GuildAId = slowGuild, GuildBId = fastGuild, StartsUtc = saturday, EndsUtc = saturday.AddDays(2) });
            await db.SaveChangesAsync();
        }

        await SetSteps(fast[0], saturday, 12_000);
        await SetSteps(fast[1], saturday.AddDays(1), 8_000);
        await SetSteps(slow[0], saturday, 5_000);
        await SetSteps(slow[1], saturday.AddDays(2), 50_000); // Monday: after the race, doesn't count

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await WalkingGuilds.RunOnce(db, saturday.AddDays(2).AddMinutes(1));

            GuildStepRace race = await db.GuildStepRaces.AsNoTracking().SingleAsync(r => r.GuildAId == slowGuild && r.GuildBId == fastGuild);
            Assert.Equal(RaceStatus.Finished, race.Status);
            Assert.Equal(5_000, race.ScoreA);
            Assert.Equal(20_000, race.ScoreB);
            Assert.Equal(fastGuild, race.WinnerGuildId);

            Guild winner = await db.Guilds.AsNoTracking().SingleAsync(g => g.Id == fastGuild);
            Guild loser = await db.Guilds.AsNoTracking().SingleAsync(g => g.Id == slowGuild);
            Assert.Equal(1, winner.WarWins);
            Assert.Equal(1000 + GuildWars.WinnerRatingBonus, winner.Rating);
            Assert.Equal(1, loser.WarLosses);

            // A few days later the result is still shown...
            GuildStepRaceDto shown = await WalkingGuilds.DescribeRace(db, fastGuild, saturday.AddDays(3), default);
            Assert.Equal("Finished", shown.Status);
            Assert.Equal("Won", shown.Result);
            Assert.Equal(20_000, shown.MyScore);
            Assert.Equal(slow[0].Name, shown.Theirs[0].Username);

            // ...and after a week it makes way for "the next race starts on Saturday".
            Assert.Equal("None", (await WalkingGuilds.DescribeRace(db, fastGuild, saturday.AddDays(9), default)).Status);
        }
    }

    [Fact]
    public async Task The_ranking_lists_walking_guilds_only()
    {
        Player a = await NewPlayer();
        Guid mine = await MakeGuild(a);
        await MakeGuild(await NewPlayer(GameIds.Yugioh));

        WalkingGuildRankingDto ranking = (await a.Http.GetFromJsonAsync<WalkingGuildRankingDto>("/api/walking/guild/ranking"))!;

        Assert.Equal(mine, ranking.MyGuildId);
        Assert.True(ranking.MyRank > 0);
        Assert.True(ranking.Items.Count <= 10);
    }
}
