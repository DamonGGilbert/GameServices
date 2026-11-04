using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Progress;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// AN EXPLORER'S PROGRESS IS THE SERVER'S: levels, milestones, quests, achievements, medals, streaks, finds on the map and
/// what's written in the activity log. These try what a hacked phone would try.
/// </summary>
public class ServerProgressTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ServerProgressTests(ApiFactory factory)
    {
        this.factory = factory;
        _ = factory.Services;   // the server has started, so its rules are loaded
    }

    private static MonsterCatalog.ProgressionEntry Rules => MonsterCatalog.Shared.Progression;
    private static MonsterCatalog.DiscoveryEntry Finds => MonsterCatalog.Shared.Discovery;

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(bool started = true)
    {
        HttpClient client = factory.CreateClient();
        string name = "p" + Guid.NewGuid().ToString("N")[..10];
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null, 1996, 3)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        await client.GetAsync("/api/walking/monsters");   // the starter
        Player p = new(client, auth.User.Id, name);
        if (started) await WithDb(async db => (await WalkingEndpoints.GetOrCreateProfile(db, p.Id, CancellationToken.None)).ProgressStarted = true);
        return p;
    }

    private async Task WithDb(Func<AppDbContext, Task> change)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    private async Task<T> FromDb<T>(Func<AppDbContext, Task<T>> read)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<WalkingProfile> Profile(Player p) => FromDb(db => db.WalkingProfiles.AsNoTracking().FirstAsync(x => x.UserId == p.Id));

    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

    // As if the server had accepted this many steps today (offset 0: its own day).
    private Task Walked(Player p, int steps) => WithDb(async db =>
    {
        WalkingDay row = await Explorer.DayAsync(db, p.Id, Today, CancellationToken.None);
        row.Steps = steps;
    });

    private Task<int> Glimmer(Player p) => FromDb(db => db.WalkingProfiles.Where(x => x.UserId == p.Id).Select(x => x.Glimmer).FirstAsync());

    private Task<int> MonstersOf(Player p) => FromDb(db => db.WalkingMonsters.CountAsync(m => m.OwnerId == p.Id));

    private static async Task<ProgressDto> Get(Player p) => (await p.Http.GetFromJsonAsync<ProgressDto>("/api/walking/progress"))!;

    private static Task<HttpResponseMessage> Claim(Player p, string kind, string id) => p.Http.PostAsJsonAsync("/api/walking/progress/claim", new ClaimRequest(kind, id));

    private static Task<HttpResponseMessage> Investigate(Player p) => p.Http.PostAsJsonAsync("/api/walking/investigate", new InvestigateRequest());

    private static async Task<int> GlimmerIn(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<ClaimResultDto>())!.Glimmer;

    // A walker's activity shows up a moment after the answer: it's written once the request has finished.
    private async Task<List<WalkingActivity>> ActivityOf(Player p, string kind)
    {
        for (int i = 0; ; i++)
        {
            List<WalkingActivity> rows = await FromDb(db => db.WalkingActivity.AsNoTracking().Where(a => a.UserId == p.Id && a.Kind == kind).ToListAsync());
            if (rows.Count > 0 || i >= 40) return rows;
            await Task.Delay(50);
        }
    }

    // ------------------------------------------------------------------ the rules match the game's

    [Fact]
    public void The_rules_came_across_from_the_game()
    {
        Assert.Equal(new[] { 100, 255, 441 }, Rules.XpForLevel.Take(3));
        Assert.Equal(Rules.PlayerMaxLevel - 1, Rules.XpForLevel.Count);
        Assert.Equal(new[] { 5, 10, 15, 20, 30, 40 }, Rules.SlotLevels);
        Assert.Equal(new[] { 300, 800, 1500, 3000, 4500, 6000, 10000 }, Finds.Steps);
        Assert.Equal(7, Rules.Milestones.Count);
        Assert.Equal(10, Rules.Medals.Count);
    }

    [Theory]
    [InlineData("steps", 999, 0)]
    [InlineData("steps", 1000, 1)]
    [InlineData("steps", 49999, 2)]
    [InlineData("steps", 5000000, 10)]
    [InlineData("steps", 7500000, 11)]
    [InlineData("distance", 80150, 9)]
    public void Medal_ladders_climb_as_they_do_in_the_game(string track, long value, int expected)
    {
        Assert.Equal(expected, Rules.Medals.First(m => m.Id == track).Reached(value));
    }

    [Theory]
    [InlineData(0, "Bronze")]
    [InlineData(2, "Gold")]
    [InlineData(9, "Legend")]
    [InlineData(10, "Legend II")]
    [InlineData(13, "Legend V")]
    public void Medal_names_go_on_for_ever(int rung, string expected) => Assert.Equal(expected, Explorer.TierName(rung));

    [Theory]
    [InlineData(0, 50)]
    [InlineData(2, 150)]
    [InlineData(20, 500)]
    public void Higher_medals_pay_more_up_to_a_limit(int rung, int expected) => Assert.Equal(expected, Explorer.MedalReward(rung));

    // ------------------------------------------------------------------ levels

    [Fact]
    public async Task Levels_bring_slots_and_a_phone_cant_say_how_many_it_has()
    {
        Player p = await NewPlayer();
        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress", new { slots = 12 });
        Assert.Equal(MonsterSlots.Starting, (await Profile(p)).MonsterSlots);

        int upToFive = Rules.XpForLevel.Take(4).Sum();
        await WithDb(async db =>
        {
            WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id);
            profile.PlayerXp = 0;
            profile.PlayerLevel = 1;
            Explorer.AddXp(db, profile, upToFive + 7, "Test");
        });

        ProgressDto now = await Get(p);
        Assert.Equal(5, now.Level);
        Assert.Equal(7, now.Xp);
        Assert.Equal(MonsterSlots.Starting + 1, now.Slots);
        Assert.NotEmpty(await ActivityOf(p, "level-up"));
    }

    [Fact]
    public async Task Walking_earns_experience_and_a_days_steps_count_up_to_a_limit()
    {
        Player p = await NewPlayer();
        DateTime now = DateTime.UtcNow;
        int offset = Math.Clamp((int)Math.Round((720 - now.TimeOfDay.TotalMinutes) / 15.0) * 15, -720, 720);
        int before = (await Profile(p)).PlayerXp;

        await p.Http.PostAsJsonAsync("/api/walking/steps", new StepReportRequest(now.AddMinutes(offset).ToString("yyyy-MM-dd"), 2525, offset));

        WalkingProfile after = await Profile(p);
        Assert.Equal(offset, after.UtcOffsetMinutes);
        Assert.Equal(before + 50, after.PlayerXp);   // 2,525 steps at 50 a point
    }

    [Fact]
    public async Task Opening_the_game_first_doesnt_cost_any_steps_walked_with_it_closed()
    {
        Player p = await NewPlayer();
        DateTime now = DateTime.UtcNow;
        int offset = Math.Clamp((int)Math.Round((720 - now.TimeOfDay.TotalMinutes) / 15.0) * 15, -720, 720);
        await p.Http.GetAsync("/api/walking/progress?offset=" + offset);   // makes today's row

        HttpResponseMessage response = await p.Http.PostAsJsonAsync("/api/walking/steps", new StepReportRequest(now.AddMinutes(offset).ToString("yyyy-MM-dd"), 6000, offset));

        StepReportResult result = (await response.Content.ReadFromJsonAsync<StepReportResult>())!;
        Assert.Equal(6000, result.AcceptedToday);
        Assert.Equal(0, result.Rejected);
    }

    // ------------------------------------------------------------------ milestones

    [Fact]
    public async Task A_milestone_needs_the_steps_the_server_accepted_and_pays_once()
    {
        Player p = await NewPlayer();
        await Walked(p, 999);
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "milestone", "1000")).StatusCode);

        await Walked(p, 1000);
        HttpResponseMessage first = await Claim(p, "milestone", "1000");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        ClaimResultDto paid = (await first.Content.ReadFromJsonAsync<ClaimResultDto>())!;
        Assert.Equal(25, paid.Glimmer);
        Assert.Equal(15, paid.Xp);
        Assert.Contains(1000, paid.Progress.ClaimedMilestones);
        Assert.Equal(25, await Glimmer(p));

        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "milestone", "1000")).StatusCode);
        Assert.Equal(25, await Glimmer(p));
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "milestone", "1234")).StatusCode);
    }

    [Fact]
    public async Task Tapping_collect_ten_times_at_once_still_pays_once()
    {
        Player p = await NewPlayer();
        await Walked(p, 1000);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Claim(p, "milestone", "1000")));

        Assert.Equal(25, await Glimmer(p));
    }

    [Fact]
    public async Task A_monster_reward_really_stays_on_the_account()
    {
        Player p = await NewPlayer();
        await Walked(p, 2500);
        int before = await MonstersOf(p);

        ClaimResultDto paid = (await (await Claim(p, "milestone", "2500")).Content.ReadFromJsonAsync<ClaimResultDto>())!;

        Assert.Equal(1, paid.Monsters);
        Assert.Equal(before + 1, await MonstersOf(p));
        Assert.Equal(1, await FromDb(db => db.WalkingMonsters.CountAsync(m => m.OwnerId == p.Id && m.Source == "reward")));
    }

    [Fact]
    public async Task A_full_team_gets_Glimmer_instead_of_a_monster()
    {
        Player p = await NewPlayer();
        await Walked(p, 2500);
        await WithDb(async db =>
        {
            for (int i = 0; i < MonsterSlots.Starting; i++)
            {
                await MonsterEndpoints.Add(db, p.Id, "MON_0001", 0, 1000 + i, 10, 4, "walk", null, Today, CancellationToken.None);
            }
        });
        int monsters = await MonstersOf(p);

        ClaimResultDto paid = (await (await Claim(p, "milestone", "2500")).Content.ReadFromJsonAsync<ClaimResultDto>())!;

        Assert.Equal(0, paid.Monsters);
        Assert.Equal(50 + Rules.GlimmerInsteadOfMonster, paid.Glimmer);
        Assert.Equal(monsters, await MonstersOf(p));
    }

    [Fact]
    public async Task Claim_all_collects_everything_ready_and_says_what()
    {
        Player p = await NewPlayer();
        await Walked(p, 6000);

        ClaimResultDto all = (await (await p.Http.PostAsync("/api/walking/progress/claim-all", null)).Content.ReadFromJsonAsync<ClaimResultDto>())!;

        Assert.Equal(5, all.Milestones);   // 1,000, 2,500, 4,000, 5,000 and 6,000
        Assert.True(all.Quests >= 1);      // the 6,000 steps quest is always offered, and any other walking quests
        Assert.Equal(all.Glimmer, await Glimmer(p));
        Assert.Contains(all.Progress.Quests, q => q.Id == "walk_6000" && q.State == 3);

        ClaimResultDto again = (await (await p.Http.PostAsync("/api/walking/progress/claim-all", null)).Content.ReadFromJsonAsync<ClaimResultDto>())!;
        Assert.Equal(0, again.Glimmer);
        Assert.Equal(0, again.Milestones + again.Quests);
    }

    // ------------------------------------------------------------------ quests

    [Fact]
    public async Task Todays_quests_stay_the_same_and_only_pay_when_done()
    {
        Player p = await NewPlayer();
        ProgressDto first = await Get(p);
        Assert.Equal(Rules.DailyQuestCount, first.Quests.Count);
        Assert.Contains(first.Quests, q => q.Id == "walk_6000");
        Assert.Equal(first.Quests.Select(q => q.Id), (await Get(p)).Quests.Select(q => q.Id));

        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "quest", "walk_6000")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "quest", "not_a_quest")).StatusCode);

        await Walked(p, 6000);
        Assert.Equal(2, (await Get(p)).Quests.First(q => q.Id == "walk_6000").State);
        Assert.Equal(80, await GlimmerIn(await Claim(p, "quest", "walk_6000")));
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "quest", "walk_6000")).StatusCode);
        Assert.Equal(80, await Glimmer(p));
    }

    // ------------------------------------------------------------------ achievements and medals

    [Fact]
    public async Task Achievements_unlock_from_the_servers_numbers_and_pay_once()
    {
        Player p = await NewPlayer();
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "achievement", "first_steps")).StatusCode);

        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).LifetimeSteps = 1000);
        ProgressDto now = await Get(p);
        Assert.Contains(now.Achievements, a => a.Id == "first_steps" && a.Unlocked && !a.Claimed);
        Assert.Contains("badge_first_steps", now.Badges);

        Assert.Equal(50, await GlimmerIn(await Claim(p, "achievement", "first_steps")));
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "achievement", "first_steps")).StatusCode);
        Assert.Equal(50, await Glimmer(p));
    }

    [Fact]
    public async Task Medals_pay_for_every_rung_reached_and_titles_have_to_be_earned()
    {
        Player p = await NewPlayer();

        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(null, null, "Gold Walker"));
        Assert.Equal("", (await Profile(p)).Title);

        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).LifetimeSteps = 50000);
        ProgressDto now = await Get(p);
        MedalDto steps = now.Medals.First(m => m.Id == "steps");
        Assert.Equal(3, steps.Reached);
        Assert.Equal(0, steps.Claimed);
        Assert.Equal(3, steps.EarnedUnix.Count);
        Assert.Contains("Gold Walker", now.Titles);

        Assert.Equal(50 + 100 + 150, await GlimmerIn(await Claim(p, "medal", "steps")));
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "medal", "steps")).StatusCode);

        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(null, null, "Gold Walker"));
        Assert.Equal("Gold Walker", (await Profile(p)).Title);

        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(null, null, "Legend Walker"));
        Assert.Equal("Gold Walker", (await Profile(p)).Title);

        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(null, null, ""));
        Assert.Equal("", (await Profile(p)).Title);
    }

    // ------------------------------------------------------------------ streaks

    private static WalkingProfile WithStreak(int current, string last, int rest) => new() { CurrentStreak = current, LongestStreak = current, LastGoalDay = last, RestDays = rest, RestDaysWeek = Explorer.WeekKey(last) };

    [Fact]
    public void The_streak_follows_the_same_forgiving_rules_as_the_game()
    {
        WalkingProfile first = new() { RestDays = 1 };
        Assert.True(Explorer.ApplyStreak(first, "2026-06-03"));
        Assert.Equal(1, first.CurrentStreak);

        WalkingProfile nextDay = WithStreak(4, "2026-06-03", 0);
        Assert.True(Explorer.ApplyStreak(nextDay, "2026-06-04"));
        Assert.Equal(5, nextDay.CurrentStreak);
        Assert.Equal(5, nextDay.LongestStreak);

        WalkingProfile sameDay = WithStreak(4, "2026-06-03", 0);
        Assert.False(Explorer.ApplyStreak(sameDay, "2026-06-03"));
        Assert.Equal(4, sameDay.CurrentStreak);

        WalkingProfile restDay = WithStreak(4, "2026-06-03", 1);
        Assert.True(Explorer.ApplyStreak(restDay, "2026-06-05"));
        Assert.Equal(5, restDay.CurrentStreak);
        Assert.Equal(0, restDay.RestDays);

        WalkingProfile broken = WithStreak(9, "2026-06-03", 0);
        Assert.True(Explorer.ApplyStreak(broken, "2026-06-05"));
        Assert.Equal(1, broken.CurrentStreak);
        Assert.Equal(9, broken.LongestStreak);

        WalkingProfile older = WithStreak(4, "2026-06-03", 1);
        Assert.False(Explorer.ApplyStreak(older, "2026-06-02"));
        Assert.Equal(4, older.CurrentStreak);
    }

    [Fact]
    public void Rest_days_refill_each_week_and_only_so_many_can_be_saved()
    {
        WalkingProfile p = WithStreak(1, "2026-06-05", 2);   // a Friday
        Explorer.ApplyStreak(p, "2026-06-08");               // the Monday after: two missed, two rest days, then a refill first
        Assert.Equal(2, p.CurrentStreak);
        Assert.Equal(0, p.RestDays);
        Assert.Equal(Explorer.WeekKey("2026-06-08"), p.RestDaysWeek);
    }

    [Fact]
    public void Week_keys_match_the_games()
    {
        Assert.Equal("2026-W23", Explorer.WeekKey("2026-06-05"));
        Assert.Equal("2026-W01", Explorer.WeekKey("2026-01-02"));
    }

    // ------------------------------------------------------------------ finds on the map

    [Theory]
    [InlineData(0, 0, 300)]
    [InlineData(299, 0, 300)]
    [InlineData(300, 1, 800)]
    [InlineData(1500, 3, 3000)]
    [InlineData(6000, 6, 10000)]
    [InlineData(10000, 7, 0)]
    [InlineData(60000, 7, 0)]
    public void Walking_brings_three_to_seven_finds_a_day(int steps, int earned, int next)
    {
        Assert.Equal(earned, Finds.Earned(steps));
        Assert.Equal(next, Finds.NextAt(steps));
    }

    [Fact]
    public async Task Nothing_turns_up_until_the_steps_have_earned_it_whatever_a_phone_says()
    {
        Player p = await NewPlayer();
        HttpResponseMessage refused = await Investigate(p);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.NotEmpty(await ActivityOf(p, "find-refused"));

        await Walked(p, 300);
        Assert.Equal(HttpStatusCode.OK, (await Investigate(p)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Investigate(p)).StatusCode);

        DiscoveryDto discovery = (await Get(p)).Discovery;
        Assert.Equal(1, discovery.Found);
        Assert.Equal(1, discovery.Earned);
        Assert.Equal(800, discovery.NextAtSteps);
    }

    [Fact]
    public async Task Ten_at_once_only_get_what_the_steps_have_earned()
    {
        Player p = await NewPlayer();
        await Walked(p, 300);

        HttpResponseMessage[] results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Investigate(p)));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, (await Get(p)).Discovery.Found);
    }

    [Fact]
    public async Task Seven_finds_a_day_at_most_and_the_last_is_the_Golden_Cache()
    {
        Player p = await NewPlayer();
        await Walked(p, 60000);
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).MonsterSlots = MonsterSlots.Most);

        InvestigateResultDto? last = null;
        for (int i = 0; i < 7; i++)
        {
            HttpResponseMessage response = await Investigate(p);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            last = await response.Content.ReadFromJsonAsync<InvestigateResultDto>();
            Assert.Equal(i == 6, last!.Golden);
        }

        Assert.True(last!.Glimmer >= Finds.GoldenGlimmer);
        Assert.True(last.Fragments >= Finds.GoldenFragments);
        Assert.Contains("Golden Cache", last.Message);
        Assert.NotEqual("trainer", last.Kind);

        HttpResponseMessage eighth = await Investigate(p);
        Assert.Equal(HttpStatusCode.Conflict, eighth.StatusCode);
        Assert.Equal(7, (await Get(p)).Discovery.Found);
    }

    [Fact]
    public async Task Tomorrow_brings_new_finds_but_changing_the_time_zone_brings_no_extra_ones()
    {
        Player p = await NewPlayer();
        await Walked(p, 300);
        Assert.Equal(HttpStatusCode.OK, (await Investigate(p)).StatusCode);

        // Same day, another time zone: if that lands on another calendar day, it has no steps there. If it's the same day, nothing's changed.
        await p.Http.GetAsync("/api/walking/progress?offset=720");
        Assert.Equal(HttpStatusCode.Conflict, (await Investigate(p)).StatusCode);
        await p.Http.GetAsync("/api/walking/progress?offset=0");
        Assert.Equal(HttpStatusCode.Conflict, (await Investigate(p)).StatusCode);

        // A silly time zone isn't taken at all.
        await p.Http.GetAsync("/api/walking/progress?offset=99999");
        Assert.Equal(0, (await Profile(p)).UtcOffsetMinutes);
    }

    [Fact]
    public void What_a_find_holds_follows_the_odds()
    {
        int monsters = 0, trainers = 0;
        Random random = new(42);
        for (int i = 0; i < 20000; i++)
        {
            string roll = Discoveries.Roll(Finds, 0, 1f, random.Next);
            if (roll == "Monster") monsters++;
            if (roll == "Trainer") trainers++;
        }

        Assert.InRange(monsters / 200.0, Finds.MonsterPercent - 1.5, Finds.MonsterPercent + 1.5);
        Assert.InRange(trainers / 200.0, 13, 19);   // 6 parts in 33 of what a Stepling leaves
    }

    [Fact]
    public async Task Real_finds_through_the_real_route_come_up_at_the_right_odds()
    {
        Player p = await NewPlayer();
        var kinds = new Dictionary<string, int>();
        const int Rounds = 300;

        for (int i = 0; i < Rounds; i++)
        {
            await WithDb(async db =>
            {
                WalkingDay row = await Explorer.DayAsync(db, p.Id, Today, CancellationToken.None);
                row.Steps = 300;
                row.Discoveries = 0;
                (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).FindsSinceMonster = 0;   // the odds alone: no sure thing after a dry spell
            });

            InvestigateResultDto find = (await (await Investigate(p)).Content.ReadFromJsonAsync<InvestigateResultDto>())!;
            kinds[find.Kind] = kinds.GetValueOrDefault(find.Kind) + 1;
        }

        int steplings = kinds.GetValueOrDefault("monster") + kinds.GetValueOrDefault("fullteam");
        Assert.InRange(steplings * 100 / Rounds, 5, 22);   // 12 in 100, with room for luck (a miss here is about 1 in 100,000)
        Assert.True(kinds.GetValueOrDefault("treasure") > 0);
        Assert.True(kinds.GetValueOrDefault("bonus") > 0);
        Assert.True(kinds.GetValueOrDefault("fragments") > 0);
        Assert.True(kinds.GetValueOrDefault("trainer") > 0);
    }

    [Fact]
    public async Task A_new_walker_has_their_starter_even_if_a_reward_arrives_first()
    {
        HttpClient client = factory.CreateClient();
        string name = "s" + Guid.NewGuid().ToString("N")[..10];
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null, 1996, 3))).Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        Player p = new(client, auth.User.Id, name);
        await p.Http.PostAsJsonAsync("/api/walking/progress/start", FromPhone());

        await Walked(p, 2500);
        await Claim(p, "milestone", "2500");

        List<WalkingMonsterDto> mine = (await client.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))!;
        Assert.Contains(mine, m => m.Source == "starter");
        Assert.Contains(mine, m => m.Source == "reward");
    }

    [Fact]
    public void Its_never_too_long_between_Steplings_and_a_boost_helps()
    {
        Assert.Equal("Monster", Discoveries.Roll(Finds, Finds.PityAfter - 1, 1f, _ => 99));
        Assert.NotEqual("Monster", Discoveries.Roll(Finds, Finds.PityAfter - 2, 1f, _ => 99));
        Assert.Equal(Finds.MonsterPercent * 2, Discoveries.MonsterChance(Finds, 2f));
        Assert.Equal(100, Discoveries.MonsterChance(Finds, 50f));
    }

    [Fact]
    public void Players_can_see_the_odds_and_they_add_up()
    {
        List<OddsDto> odds = Discoveries.Odds(Finds, 1f);
        Assert.Equal(5, odds.Count);
        Assert.Equal(Finds.MonsterPercent, odds[0].Percent);
        Assert.InRange(odds.Sum(o => o.Percent), 99, 101);
    }

    [Fact]
    public async Task A_Stepling_from_a_find_is_kept_and_befriended_once_with_two_points_at_most()
    {
        Player p = await NewPlayer();
        await Walked(p, 300);
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).FindsSinceMonster = Finds.PityAfter);   // sure to be one

        InvestigateResultDto find = (await (await Investigate(p)).Content.ReadFromJsonAsync<InvestigateResultDto>())!;
        Assert.Equal("monster", find.Kind);
        Assert.NotNull(find.MonsterId);
        Assert.NotNull(find.SpeciesId);
        Assert.Equal(0, (await Profile(p)).FindsSinceMonster);

        WalkingMonster monster = await FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == find.MonsterId));
        Assert.Equal(p.Id, monster.OwnerId);
        Assert.Equal("find", monster.Source);

        Assert.Equal(HttpStatusCode.OK, (await p.Http.PostAsJsonAsync($"/api/walking/investigate/{find.MonsterId}/befriend", new BefriendRequest(99))).StatusCode);
        monster = await FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == find.MonsterId));
        Assert.Equal(2, monster.TrainedPower + monster.TrainedGuard + monster.TrainedTricks + monster.TrainedWits);
        Assert.Equal(1, monster.TrainedPower);   // on its lowest stats
        Assert.Equal(1, monster.TrainedGuard);

        Assert.Equal(HttpStatusCode.Conflict, (await p.Http.PostAsJsonAsync($"/api/walking/investigate/{find.MonsterId}/befriend", new BefriendRequest(2))).StatusCode);
    }

    [Fact]
    public async Task Only_a_Stepling_you_found_can_be_befriended()
    {
        Player p = await NewPlayer(), other = await NewPlayer();
        Guid starter = await FromDb(db => db.WalkingMonsters.Where(m => m.OwnerId == p.Id).Select(m => m.Id).FirstAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await p.Http.PostAsJsonAsync($"/api/walking/investigate/{starter}/befriend", new BefriendRequest(2))).StatusCode);

        await Walked(p, 300);
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).FindsSinceMonster = Finds.PityAfter);
        InvestigateResultDto find = (await (await Investigate(p)).Content.ReadFromJsonAsync<InvestigateResultDto>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.PostAsJsonAsync($"/api/walking/investigate/{find.MonsterId}/befriend", new BefriendRequest(2))).StatusCode);
    }

    [Fact]
    public async Task A_full_team_finding_a_Stepling_gets_Glimmer_and_nothing_extra_to_keep()
    {
        Player p = await NewPlayer();
        await Walked(p, 300);
        await WithDb(async db =>
        {
            (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).FindsSinceMonster = Finds.PityAfter;
            for (int i = 0; i < MonsterSlots.Starting; i++) await MonsterEndpoints.Add(db, p.Id, "MON_0001", 0, 2000 + i, 10, 4, "walk", null, Today, CancellationToken.None);
        });
        int before = await MonstersOf(p);

        InvestigateResultDto find = (await (await Investigate(p)).Content.ReadFromJsonAsync<InvestigateResultDto>())!;

        Assert.Equal("fullteam", find.Kind);
        Assert.Equal(Rules.GlimmerInsteadOfMonster, find.Glimmer);
        Assert.Equal(before, await MonstersOf(p));
    }

    [Fact]
    public async Task A_discovery_boost_lasts_and_shows()
    {
        Player p = await NewPlayer();
        await Walked(p, 4000);
        await Claim(p, "milestone", "4000");   // +30 minutes at x1.5

        ProgressDto now = await Get(p);
        Assert.Equal(1.5f, now.BoostMultiplier);
        Assert.True(now.BoostUntilUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 25 * 60);
        Assert.Equal(18, now.Discovery.Odds[0].Percent);
    }

    // ------------------------------------------------------------------ what comes from the phone

    private static StartRequest FromPhone(int level = 1, long steps = 0, int wins = 0, List<MedalClaimDto>? medals = null, List<string>? achievements = null, int fragments = 0, double metres = 0, int streak = 0, List<string>? titles = null) =>
        new(level, 0, steps, metres, 0, streak, streak, null, 1, null, wins, 0, 0, 0, 0, 0, 0, 0, fragments, null, null, titles, medals, achievements, null, null);

    [Fact]
    public async Task A_phone_brings_its_progress_once_and_within_limits()
    {
        Player p = await NewPlayer(started: false);

        ProgressDto notYet = await Get(p);
        Assert.False(notYet.Started);
        Assert.Empty(notYet.Quests);

        await p.Http.PostAsJsonAsync("/api/walking/progress/start", FromPhone(level: 500, steps: 99_000_000, wins: 99999, fragments: 99999, streak: 9999, metres: 1e12));

        WalkingProfile after = await Profile(p);
        Assert.True(after.ProgressStarted);
        Assert.Equal(ProgressEndpoints.MostStartingLevel, after.PlayerLevel);
        Assert.Equal(ProgressEndpoints.MostStartingSteps, after.StartingSteps);
        Assert.Equal(ProgressEndpoints.MostStartingCount, after.BattlesWon);
        Assert.Equal(ProgressEndpoints.MostStartingFragments, after.Fragments);
        Assert.Equal(ProgressEndpoints.MostStartingStreak, after.LongestStreak);
        Assert.True(after.LifetimeMetres <= ProgressEndpoints.MostStartingSteps * Explorer.MaxMetresPerStep);

        // Only the first time.
        await p.Http.PostAsJsonAsync("/api/walking/progress/start", FromPhone(level: 15, wins: 200));
        Assert.Equal(ProgressEndpoints.MostStartingCount, (await Profile(p)).BattlesWon);
    }

    [Fact]
    public async Task What_a_phone_brings_can_never_pay_a_thing()
    {
        Player p = await NewPlayer(started: false);
        int before = await Glimmer(p);

        await p.Http.PostAsJsonAsync("/api/walking/progress/start", FromPhone(level: 15, steps: 200000, wins: 200, streak: 60));

        ProgressDto now = await Get(p);
        Assert.True(now.Medals.First(m => m.Id == "steps").Reached >= 4);
        Assert.All(now.Medals, m => Assert.Equal(m.Reached, m.Claimed));
        Assert.All(now.Achievements.Where(a => a.Unlocked), a => Assert.True(a.Claimed));

        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "medal", "steps")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "achievement", "first_steps")).StatusCode);
        Assert.Equal(before, await Glimmer(p));
    }

    [Fact]
    public async Task Medals_a_phone_already_paid_for_arent_paid_for_again_later()
    {
        Player p = await NewPlayer(started: false);
        await p.Http.PostAsJsonAsync("/api/walking/progress/start", FromPhone(medals: new List<MedalClaimDto> { new("steps", 3), new("nonsense", 9) }, achievements: new List<string> { "first_steps", "nonsense" }));

        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).LifetimeSteps = 100000);
        ProgressDto now = await Get(p);
        Assert.Equal(4, now.Medals.First(m => m.Id == "steps").Reached);
        Assert.DoesNotContain(now.Medals, m => m.Id == "nonsense");

        Assert.Equal(Explorer.MedalReward(3), await GlimmerIn(await Claim(p, "medal", "steps")));   // only the rung it hadn't had yet
        Assert.Equal(HttpStatusCode.Conflict, (await Claim(p, "achievement", "first_steps")).StatusCode);
    }

    [Fact]
    public async Task Only_a_real_gold_title_comes_across()
    {
        Player p = await NewPlayer(started: false);
        await p.Http.PostAsJsonAsync("/api/walking/progress/start", FromPhone(titles: new List<string> { "Gold Collector", "Legend Walker", "Supreme Overlord" }));

        List<string> titles = (await Get(p)).Titles;
        Assert.Contains("Gold Collector", titles);
        Assert.DoesNotContain("Legend Walker", titles);
        Assert.DoesNotContain("Supreme Overlord", titles);
    }

    [Fact]
    public async Task After_the_first_sync_a_phone_cant_add_any_Glimmer()
    {
        Player p = await NewPlayer();
        await p.Http.PostAsJsonAsync("/api/walking/wallet/sync", new WalletSyncRequest(500));
        Assert.Equal(500, await Glimmer(p));

        await p.Http.PostAsJsonAsync("/api/walking/wallet/sync", new { phoneDelta = 99999, phoneBalance = 99999 });
        Assert.Equal(500, await Glimmer(p));
    }

    // ------------------------------------------------------------------ walking and your companion

    [Fact]
    public async Task Distance_and_areas_are_only_believed_as_far_as_the_steps_can_explain_them()
    {
        Player p = await NewPlayer();
        DateTime now = DateTime.UtcNow;
        int offset = Math.Clamp((int)Math.Round((720 - now.TimeOfDay.TotalMinutes) / 15.0) * 15, -720, 720);
        string day = now.AddMinutes(offset).ToString("yyyy-MM-dd");

        await p.Http.PostAsJsonAsync("/api/walking/steps", new StepReportRequest(day, 1000, offset, Metres: 999_999, AreasToday: 9999, NewAreas: 9999));

        WalkingDay row = await FromDb(db => db.WalkingDays.AsNoTracking().FirstAsync(d => d.UserId == p.Id && d.Day == day));
        Assert.Equal(1200, row.Metres);
        Assert.Equal(21, row.Areas);
        WalkingProfile after = await Profile(p);
        Assert.Equal(1200, after.LifetimeMetres);
        Assert.Equal(21, after.AreasVisited);
    }

    [Fact]
    public async Task Companions_train_a_point_a_day_from_the_servers_steps_and_grow_their_bond()
    {
        Player p = await NewPlayer();
        Guid monster = await FromDb(db => db.WalkingMonsters.Where(m => m.OwnerId == p.Id).Select(m => m.Id).FirstAsync());
        var notices = new List<string>();

        async Task Walk(int steps, string day) => await WithDb(async db =>
        {
            WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == monster);
            WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id);
            Companions.OnSteps(db, profile, m, steps, day, notices);
        });

        await Walk(2999, "2026-06-01");
        Assert.Empty(notices);
        await Walk(1, "2026-06-01");
        Assert.Single(notices);
        await Walk(5000, "2026-06-01");
        Assert.Single(notices);   // one point a day

        WalkingMonster after = await FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == monster));
        Assert.Equal(1, after.TrainedPower);
        Assert.Equal(8000, after.BondSteps);

        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).TrainingFocus = 4);
        await Walk(3000, "2026-06-02");
        Assert.Equal(1, (await FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == monster))).TrainedWits);
    }

    [Fact]
    public async Task A_phone_cant_claim_training_or_bond_once_it_has_brought_its_starters()
    {
        Player p = await NewPlayer();
        Guid monster = await FromDb(db => db.WalkingMonsters.Where(m => m.OwnerId == p.Id).Select(m => m.Id).FirstAsync());

        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(null, new List<MonsterProgressDto> { new(monster, 1, 0, 2, 1, 0, 0, 0, 500) }));
        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(null, new List<MonsterProgressDto> { new(monster, 50, 0, 9, 9, 9, 9, 0, 99999) }));

        WalkingMonster after = await FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == monster));
        Assert.Equal(1, after.Level);
        Assert.Equal(3, after.TrainedPower + after.TrainedGuard);
        Assert.Equal(500, after.BondSteps);
    }

    // ------------------------------------------------------------------ the activity log

    [Fact]
    public async Task What_a_walker_does_is_written_down_including_what_was_refused()
    {
        Player p = await NewPlayer();
        await Walked(p, 1000);
        await Claim(p, "milestone", "1000");
        await Claim(p, "milestone", "1000");   // refused

        List<WalkingActivity> requests = await ActivityOf(p, "request");
        for (int i = 0; i < 40 && requests.Count(r => r.Detail.Contains("/progress/claim")) < 2; i++)
        {
            await Task.Delay(50);
            requests = await ActivityOf(p, "request");
        }

        Assert.Contains(requests, r => r.Detail == "POST /api/walking/progress/claim -> 200");
        Assert.Contains(requests, r => r.Detail == "POST /api/walking/progress/claim -> 409");
        Assert.Contains(await ActivityOf(p, "claim"), a => a.Detail.Contains("milestone 1000") && a.Detail.Contains("+25 Glimmer"));
        Assert.NotEmpty(await ActivityOf(p, "account"));
    }

    [Fact]
    public async Task The_log_never_holds_a_password_or_what_was_sent()
    {
        Player p = await NewPlayer();
        await p.Http.PostAsJsonAsync("/api/walking/investigate", new { biome = "SECRET-SENT-WITH-THE-REQUEST", lat = 51.5, lon = -0.1 });
        await ActivityOf(p, "request");

        await p.Http.PostAsJsonAsync("/api/auth/login", new { username = p.Name, password = "CorrectHorse1" });
        await ActivityOf(p, "sign-in");

        List<string> everything = await FromDb(db => db.WalkingActivity.AsNoTracking().Where(a => a.UserId == p.Id).Select(a => a.Detail).ToListAsync());
        Assert.NotEmpty(everything);
        Assert.DoesNotContain(everything, d => d.Contains("CorrectHorse1") || d.Contains("SECRET") || d.Contains("51.5"));
    }

    [Fact]
    public async Task Steps_reports_are_too_many_to_write_down_one_by_one()
    {
        Player p = await NewPlayer();
        DateTime now = DateTime.UtcNow;
        int offset = Math.Clamp((int)Math.Round((720 - now.TimeOfDay.TotalMinutes) / 15.0) * 15, -720, 720);
        await p.Http.PostAsJsonAsync("/api/walking/steps", new StepReportRequest(now.AddMinutes(offset).ToString("yyyy-MM-dd"), 100, offset));
        await p.Http.PostAsJsonAsync("/api/walking/wallet/sync", new WalletSyncRequest(0));   // something that is written, to wait for

        List<WalkingActivity> requests = await ActivityOf(p, "request");
        Assert.DoesNotContain(requests, r => r.Detail.Contains("/steps"));
    }

    [Fact]
    public async Task An_admin_can_read_a_walkers_day_with_their_Glimmer()
    {
        Player p = await NewPlayer();
        await Walked(p, 1000);
        await Claim(p, "milestone", "1000");

        List<string>? lines = await FromDb(db => ActivityLog.TimelineAsync(db, p.Name, 1, DateTime.UtcNow));

        Assert.NotNull(lines);
        Assert.Contains(lines, l => l.Contains("claim") && l.Contains("milestone 1000"));
        Assert.Contains(lines, l => l.Contains("glimmer") && l.Contains("+25 = 25"));
        Assert.Null(await FromDb(db => ActivityLog.TimelineAsync(db, "nobody-of-this-name", 1, DateTime.UtcNow)));
    }

    [Fact]
    public async Task The_log_is_only_kept_for_a_while()
    {
        Player p = await NewPlayer();
        await WithDb(db =>
        {
            db.WalkingActivity.Add(new WalkingActivity { UserId = p.Id, Kind = "old", Detail = "", AtUtc = DateTime.UtcNow.AddDays(-91) });
            db.WalkingActivity.Add(new WalkingActivity { UserId = p.Id, Kind = "recent", Detail = "", AtUtc = DateTime.UtcNow.AddDays(-89) });
            return Task.CompletedTask;
        });

        await WithDb(db => WalkingLifecycleService.RunOnce(db, DateTime.UtcNow));

        Assert.Equal(0, await FromDb(db => db.WalkingActivity.CountAsync(a => a.UserId == p.Id && a.Kind == "old")));
        Assert.Equal(1, await FromDb(db => db.WalkingActivity.CountAsync(a => a.UserId == p.Id && a.Kind == "recent")));
    }

    [Fact]
    public async Task Deleting_an_account_takes_its_progress_and_log_with_it()
    {
        Player p = await NewPlayer();
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).LifetimeSteps = 50000);
        await Get(p);
        Assert.True(await FromDb(db => db.WalkingMedals.AnyAsync(m => m.UserId == p.Id)));

        Assert.Equal(HttpStatusCode.NoContent, (await p.Http.PostAsJsonAsync("/api/auth/delete-account", new DeleteAccountRequest("CorrectHorse1"))).StatusCode);

        // (the last request's own line may arrive after the account has gone: it can't be written, and that's fine)
        await Task.Delay(100);
        Assert.False(await FromDb(db => db.WalkingMedals.AnyAsync(m => m.UserId == p.Id)));
        Assert.False(await FromDb(db => db.WalkingAchievements.AnyAsync(a => a.UserId == p.Id)));
        Assert.False(await FromDb(db => db.WalkingActivity.AnyAsync(a => a.UserId == p.Id)));
    }

    [Fact]
    public async Task The_books_still_add_up_after_all_of_it()
    {
        Player p = await NewPlayer();
        await Walked(p, 60000);
        await p.Http.PostAsync("/api/walking/progress/claim-all", null);
        for (int i = 0; i < 7; i++) await Investigate(p);
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).LifetimeSteps = 50000);
        await Claim(p, "medal", "steps");

        AuditReportCheck report = await FromDb(async db => new AuditReportCheck(await WalletAudit.RunAsync(db, p.Id)));
        Assert.Empty(report.Report.Problems);
    }

    private record AuditReportCheck(AuditReport Report);
}
