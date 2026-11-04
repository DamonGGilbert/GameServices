using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Battles;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// BATTLES AGAINST WILD MONSTERS AND TRAINERS are run by the server: it picks the opponent, decides the fight and pays out,
/// and every Glimmer and every fight can be checked afterwards. The phone can only ask.
/// </summary>
public class WalkingBattleTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public WalkingBattleTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(int glimmer = 0)
    {
        HttpClient client = factory.CreateClient();
        string name = "b" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        await client.GetAsync("/api/walking/monsters");   // the starter
        var player = new Player(client, auth.User.Id, name);
        await WithDb(async db =>
        {
            WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, player.Id, CancellationToken.None);
            profile.WalletStarted = true;
            if (glimmer > 0) WalkingWallet.Change(db, profile, glimmer - profile.Glimmer, "test", "Starting Glimmer");
            else if (profile.Glimmer != 0) WalkingWallet.Change(db, profile, -profile.Glimmer, "test", "Starting Glimmer");
        });
        return player;
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
    private Task<WalkingMonster> Monster(Guid id) => FromDb(db => db.WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == id));
    private static async Task<Guid> StarterOf(Player p) => (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0].Id;

    private static int nextSerial = 500000;

    private async Task<Guid> Extra(Player p, int plus)
    {
        Guid id = Guid.Empty;
        await WithDb(async db =>
        {
            WalkingMonster starter = await db.WalkingMonsters.FirstAsync(m => m.OwnerId == p.Id);
            var m = new WalkingMonster
            {
                OwnerId = p.Id, SpeciesId = starter.SpeciesId, Seed = starter.Seed + plus, Potential = starter.Potential, RulesVersion = starter.RulesVersion,
                RarityTier = starter.RarityTier, Serial = Interlocked.Increment(ref nextSerial), Source = "walk", BiomeId = "", FoundDay = "2020-01-01", FoundUtc = DateTime.UtcNow.AddDays(-30),
                AcquiredUtc = DateTime.UtcNow.AddDays(-30), Seen = true,
            };
            db.WalkingMonsters.Add(m);
            id = m.Id;
        });
        return id;
    }

    private static Task<HttpResponseMessage> Start(Player p, string opponent, params Guid[] monsters) =>
        p.Http.PostAsJsonAsync("/api/walking/battles", new BattleStartRequest(opponent, monsters.ToList()));

    private static async Task<BattleDto> Started(Player p, string opponent, params Guid[] monsters)
    {
        HttpResponseMessage response = await Start(p, opponent, monsters);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BattleDto>())!;
    }

    // Finds a seed that comes out the way the test needs: the fight itself is the server's to decide.
    private async Task Force(Guid battleId, DuelStrategy strategy, bool won, bool tonic = false, bool signature = false)
    {
        await WithDb(async db =>
        {
            WalkingBattle battle = await db.WalkingBattles.FirstAsync(b => b.Id == battleId);
            battle.Tonic = tonic;
            battle.Signature = signature;
            for (long seed = 1; seed < 100000; seed++)
            {
                battle.Seed = seed;
                if (BattleEndpoints.Decide(battle, strategy, out _).PlayerWon == won) break;
            }
            battle.Tonic = false;
            battle.Signature = false;
        });
    }

    private async Task<BattleResultDto> Fight(Player p, Guid battleId, bool won, int stake = 0, bool tonic = false, bool signature = false)
    {
        await Force(battleId, DuelStrategy.Balanced, won, tonic, signature);
        HttpResponseMessage response = await p.Http.PostAsJsonAsync($"/api/walking/battles/{battleId}/fight", new BattleFightRequest("Balanced", tonic, stake, signature));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        BattleResultDto result = (await response.Content.ReadFromJsonAsync<BattleResultDto>())!;
        Assert.Equal(won, result.Won);
        return result;
    }

    private const string Trainer = "npc_odessa";
    private MonsterCatalog Catalog => factory.Services.GetRequiredService<MonsterFinder>().Catalog;

    // ------------------------------------------------------------------ nobody can peek

    [Fact]
    public async Task The_seed_stays_secret_until_the_fight_is_over()
    {
        Player p = await NewPlayer();
        HttpResponseMessage response = await Start(p, "wild", await StarterOf(p));
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(json.RootElement.TryGetProperty("seed", out _), "the seed decides the fight: sent early, a phone could work out the result");

        Guid id = json.RootElement.GetProperty("id").GetGuid();
        HttpResponseMessage fought = await p.Http.PostAsJsonAsync($"/api/walking/battles/{id}/fight", new BattleFightRequest("Balanced"));
        BattleResultDto result = (await fought.Content.ReadFromJsonAsync<BattleResultDto>())!;
        Assert.NotEqual(0, result.Seed);   // ...and the phone gets it afterwards, to replay the fight
    }

    [Fact]
    public async Task Every_new_battle_has_a_new_seed()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);

        BattleDto a = await Started(p, Trainer, monster);
        BattleDto b = await Started(p, Trainer, monster);

        long seedA = await FromDb(db => db.WalkingBattles.Where(x => x.Id == a.Id).Select(x => x.Seed).FirstAsync());
        long seedB = await FromDb(db => db.WalkingBattles.Where(x => x.Id == b.Id).Select(x => x.Seed).FirstAsync());
        Assert.NotEqual(seedA, seedB);
    }

    [Fact]
    public async Task A_battle_can_be_fought_once()
    {
        Player p = await NewPlayer();
        BattleDto battle = await Started(p, Trainer, await StarterOf(p));
        await Fight(p, battle.Id, true);

        HttpResponseMessage again = await p.Http.PostAsJsonAsync($"/api/walking/battles/{battle.Id}/fight", new BattleFightRequest("Balanced"));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(Catalog.Battle.GlimmerPerWin, (await Profile(p)).Glimmer);
    }

    [Fact]
    public async Task Tapping_fight_twice_at_once_only_pays_once()
    {
        Player p = await NewPlayer();
        BattleDto battle = await Started(p, Trainer, await StarterOf(p));
        await Force(battle.Id, DuelStrategy.Balanced, true);

        HttpResponseMessage[] both = await Task.WhenAll(
            p.Http.PostAsJsonAsync($"/api/walking/battles/{battle.Id}/fight", new BattleFightRequest("Balanced")),
            p.Http.PostAsJsonAsync($"/api/walking/battles/{battle.Id}/fight", new BattleFightRequest("Balanced")));

        Assert.Single(both, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(Catalog.Battle.GlimmerPerWin, (await Profile(p)).Glimmer);
    }

    [Fact]
    public async Task Starting_a_new_battle_ends_the_one_you_didnt_fight()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        BattleDto first = await Started(p, Trainer, monster);
        await Started(p, Trainer, monster);

        HttpResponseMessage old = await p.Http.PostAsJsonAsync($"/api/walking/battles/{first.Id}/fight", new BattleFightRequest("Balanced"));

        Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
    }

    [Fact]
    public async Task An_old_battle_cant_be_fought()
    {
        Player p = await NewPlayer();
        BattleDto battle = await Started(p, Trainer, await StarterOf(p));
        await WithDb(async db => (await db.WalkingBattles.FirstAsync(b => b.Id == battle.Id)).ExpiresUtc = DateTime.UtcNow.AddMinutes(-1));

        HttpResponseMessage late = await p.Http.PostAsJsonAsync($"/api/walking/battles/{battle.Id}/fight", new BattleFightRequest("Balanced"));

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }

    [Fact]
    public async Task You_cant_fight_someone_elses_battle_or_with_their_monster()
    {
        Player a = await NewPlayer();
        Player b = await NewPlayer();
        BattleDto battle = await Started(a, Trainer, await StarterOf(a));

        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.PostAsJsonAsync($"/api/walking/battles/{battle.Id}/fight", new BattleFightRequest("Balanced"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Start(b, Trainer, await StarterOf(a))).StatusCode);
    }

    [Fact]
    public async Task Nonsense_is_refused()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);

        Assert.Equal(HttpStatusCode.BadRequest, (await Start(p, "npc_nobody", monster)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Start(p, "wild", monster, await Extra(p, 1), await Extra(p, 2))).StatusCode);   // wild monsters are one at a time
        Assert.Equal(HttpStatusCode.BadRequest, (await Start(p, Trainer, monster, await Extra(p, 3))).StatusCode);                     // one or three

        BattleDto battle = await Started(p, Trainer, monster);
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Http.PostAsJsonAsync($"/api/walking/battles/{battle.Id}/fight", new BattleFightRequest("Sideways"))).StatusCode);
    }

    // ------------------------------------------------------------------ opponents

    [Fact]
    public async Task A_trainers_team_is_always_the_same_monsters_at_a_level_matched_to_yours()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        await WithDb(async db => (await db.WalkingMonsters.FirstAsync(m => m.Id == monster)).Level = 12);

        TrainerTeamDto odessa = (await p.Http.GetFromJsonAsync<TrainerTeamDto>($"/api/walking/battles/trainers/npc_odessa?monsterId={monster}"))!;
        TrainerTeamDto again = (await p.Http.GetFromJsonAsync<TrainerTeamDto>($"/api/walking/battles/trainers/npc_odessa?monsterId={monster}"))!;
        TrainerTeamDto bramble = (await p.Http.GetFromJsonAsync<TrainerTeamDto>($"/api/walking/battles/trainers/npc_bramble?monsterId={monster}"))!;

        Assert.Equal(3, odessa.Team.Count);
        Assert.Equal(odessa.Team.Select(m => (m.SpeciesId, m.Seed)), again.Team.Select(m => (m.SpeciesId, m.Seed)));
        Assert.All(odessa.Team, m => Assert.Equal(12, m.Level));
        Assert.All(bramble.Team, m => Assert.Equal(13, m.Level));   // Bramble is a level above you
    }

    [Fact]
    public async Task A_wildcard_trainer_has_three_wild_monsters()
    {
        Player p = await NewPlayer();
        TrainerTeamDto juno = (await p.Http.GetFromJsonAsync<TrainerTeamDto>($"/api/walking/battles/trainers/npc_juno?monsterId={await StarterOf(p)}"))!;

        Assert.Equal(3, juno.Team.Count);
        Assert.All(juno.Team, m => Assert.True(Catalog.FindSpecies(m.SpeciesId)!.Wild));
    }

    [Fact]
    public async Task You_can_pick_which_of_a_trainers_team_you_play()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        TrainerTeamDto team = (await p.Http.GetFromJsonAsync<TrainerTeamDto>($"/api/walking/battles/trainers/{Trainer}?monsterId={monster}"))!;

        HttpResponseMessage response = await p.Http.PostAsJsonAsync("/api/walking/battles", new BattleStartRequest(Trainer, new List<Guid> { monster }, 2));
        BattleDto battle = (await response.Content.ReadFromJsonAsync<BattleDto>())!;

        Assert.Equal(team.Team[2].SpeciesId, battle.Opponents.Single().SpeciesId);
        Assert.Equal(team.Team[2].Stats, battle.Opponents.Single().Stats);

        // In the battle itself, the clans count.
        WalkingMonster m = await Monster(monster);
        Clan mine = Clans.Of(Catalog, m.SpeciesId, m.Seed), theirs = Clans.Of(Catalog, team.Team[2].SpeciesId, team.Team[2].Seed);
        int[] s = team.Team[2].Stats;
        DuelStats expected = Clans.InBattle(new DuelStats(s[0], s[1], s[2], s[3]), theirs, mine);
        Assert.Equal(new[] { expected.Power, expected.Guard, expected.Trickery, expected.Wits }, battle.OpponentStats);
    }

    [Fact]
    public async Task A_wild_monster_is_close_to_your_monsters_level()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        await WithDb(async db => (await db.WalkingMonsters.FirstAsync(m => m.Id == monster)).Level = 20);

        for (int i = 0; i < 12; i++)
        {
            BattleDto battle = await Started(p, "wild", monster);
            Assert.InRange(battle.Opponents.Single().Level, 19, 21);
        }
    }

    [Fact]
    public async Task The_arena_helps_a_monster_at_home_there()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        WalkingMonster starter = await Monster(monster);
        string element = Clans.Of(Catalog, starter.SpeciesId, starter.Seed).ToString();
        MonsterCatalog.BiomeEntry home = Catalog.Biomes.First(b => b.Elements.GetValueOrDefault(element) > 1);
        MonsterCatalog.BiomeEntry away = Catalog.Biomes.First(b => b.Elements.GetValueOrDefault(element) <= 1);

        BattleDto atHome = (await (await p.Http.PostAsJsonAsync("/api/walking/battles", new BattleStartRequest(Trainer, new List<Guid> { monster }, 0, home.Id))).Content.ReadFromJsonAsync<BattleDto>())!;
        BattleDto awayFrom = (await (await p.Http.PostAsJsonAsync("/api/walking/battles", new BattleStartRequest(Trainer, new List<Guid> { monster }, 0, away.Id))).Content.ReadFromJsonAsync<BattleDto>())!;

        Assert.True(atHome.MyArena);
        Assert.False(awayFrom.MyArena);
    }

    // ------------------------------------------------------------------ what it pays

    [Fact]
    public async Task Beating_a_trainer_pays_and_the_monster_grows()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        BattleDto battle = await Started(p, Trainer, monster);

        BattleResultDto result = await Fight(p, battle.Id, true);

        Assert.Equal(Catalog.Battle.GlimmerPerWin, result.GlimmerChange);
        Assert.Equal(Catalog.Battle.GlimmerPerWin, result.Glimmer);
        Assert.Equal(Catalog.Battle.GlimmerPerWin, (await Profile(p)).Glimmer);
        Assert.Equal(Catalog.Battle.MonsterXpPerBattle, result.MonsterXp);
        Assert.Equal(Catalog.Battle.PlayerXpPerWin, result.PlayerXp);
        Assert.True(result.TrainerReward);
        Assert.Equal(0, result.StepsToWake);

        WalkingMonster after = await Monster(monster);
        Assert.Equal(MonsterLevels.FromTotal(Catalog.Battle.MonsterXpPerBattle), (after.Level, after.Experience));
        Assert.Equal(after.Level, result.Monsters.Single().Level);
    }

    [Fact]
    public async Task Losing_to_a_trainer_means_a_snooze_until_you_walk()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        BattleDto battle = await Started(p, Trainer, monster);

        BattleResultDto result = await Fight(p, battle.Id, false);

        Assert.Equal(Catalog.Battle.GlimmerPerLoss, result.GlimmerChange);
        Assert.Equal(Catalog.Battle.MonsterXpPerBattle / 3, result.MonsterXp);
        Assert.Equal(BattleEndpoints.SnoozeSteps, result.StepsToWake);
        Assert.False(result.TrainerReward);
        Assert.Equal(BattleEndpoints.SnoozeSteps, result.Monsters.Single().StepsToWake);

        Assert.Equal(HttpStatusCode.BadRequest, (await Start(p, Trainer, monster)).StatusCode);

        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).LifetimeSteps += BattleEndpoints.SnoozeSteps);
        Assert.Equal(HttpStatusCode.OK, (await Start(p, Trainer, monster)).StatusCode);
    }

    [Fact]
    public async Task A_team_of_three_adds_up_its_stats_pays_double_and_snoozes_together()
    {
        Player p = await NewPlayer();
        Guid a = await StarterOf(p), b = await Extra(p, 1), c = await Extra(p, 2);

        BattleDto win = await Started(p, Trainer, a, b, c);
        Assert.Equal(3, win.Opponents.Count);

        // Each of mine against the one lined up against it, then everything added together.
        int power = 0, theirPower = 0;
        Guid[] team = { a, b, c };
        for (int i = 0; i < 3; i++)
        {
            WalkingMonster m = await Monster(team[i]);
            BattleMonsterDto o = win.Opponents[i];
            Clan mine = Clans.Of(Catalog, m.SpeciesId, m.Seed), theirs = Clans.Of(Catalog, o.SpeciesId, o.Seed);
            power += Clans.InBattle(DuelStats.From(RankedEndpoints.StatsOf(Catalog, m)), mine, theirs).Power;
            theirPower += Clans.InBattle(new DuelStats(o.Stats[0], o.Stats[1], o.Stats[2], o.Stats[3]), theirs, mine).Power;
        }
        Assert.Equal(power, win.MyStats[0]);
        Assert.Equal(theirPower, win.OpponentStats[0]);

        BattleResultDto won = await Fight(p, win.Id, true);
        Assert.Equal(Catalog.Battle.GlimmerPerWin * 2, won.GlimmerChange);
        Assert.Equal(3, won.Monsters.Count);
        Assert.All(won.Monsters, m => Assert.Equal(MonsterLevels.FromTotal(Catalog.Battle.MonsterXpPerBattle).Level, m.Level));

        BattleResultDto lost = await Fight(p, (await Started(p, Trainer, a, b, c)).Id, false);
        Assert.Equal(BattleEndpoints.SnoozeSteps, lost.StepsToWake);
        Assert.All(lost.Monsters, m => Assert.Equal(BattleEndpoints.SnoozeSteps, m.StepsToWake));
    }

    [Fact]
    public async Task A_wild_win_takes_a_small_bite_three_times_a_day()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);

        for (int i = 0; i < PowerBattles.WildBitesPerDay; i++)
        {
            BattleResultDto bite = await Fight(p, (await Started(p, "wild", monster)).Id, true);
            Assert.True(bite.PowerChange > 0);
        }

        BattleResultDto noBite = await Fight(p, (await Started(p, "wild", monster)).Id, true);
        Assert.Equal(0, noBite.PowerChange);
        Assert.False(noBite.TrainerReward);
    }

    [Fact]
    public async Task A_bite_of_a_rare_wild_monster_only_counts_it_a_little_above_yours()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        BattleDto battle = await Started(p, "wild", monster);
        await WithDb(async db =>
        {
            WalkingBattle b = await db.WalkingBattles.FirstAsync(x => x.Id == battle.Id);
            b.TheirPower = 100000;
            b.MyPower = 100000000;   // so it's a win
        });

        BattleResultDto bite = await Fight(p, battle.Id, true);

        WalkingMonster before = (await Monster(monster));
        Assert.True(bite.PowerChange < 100000 * PowerBattles.WildBiteShare, "counted " + bite.PowerChange + " out of " + before.Level);
    }

    [Fact]
    public async Task A_wild_loss_turns_it_to_crystal_and_a_crystal_cant_battle()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);

        BattleResultDto lost = await Fight(p, (await Started(p, "wild", monster)).Id, false);

        Assert.True(lost.PowerChange < 0);
        Assert.Equal(PowerBattles.DefeatSteps, lost.CrystalSteps);
        Assert.Equal(0, lost.StepsToWake);
        Assert.True(PowerBattles.IsCrystal(await Monster(monster)));

        Assert.Equal(HttpStatusCode.BadRequest, (await Start(p, Trainer, monster)).StatusCode);
    }

    [Fact]
    public async Task Battle_Glimmer_stops_at_the_days_limit_and_a_new_day_starts_again()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        await WithDb(async db =>
        {
            WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id);
            profile.BattleDay = today;
            profile.BattleGlimmerToday = BattleEndpoints.MaxGlimmerPerDay - 10;
        });

        Assert.Equal(10, (await Fight(p, (await Started(p, Trainer, monster)).Id, true)).GlimmerChange);
        Assert.Equal(0, (await Fight(p, (await Started(p, Trainer, monster)).Id, true)).GlimmerChange);   // the win still counts, and still grows it
        Assert.Equal(BattleEndpoints.MaxGlimmerPerDay, (await Profile(p)).BattleGlimmerToday);

        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).BattleDay = "2000-01-01");
        Assert.Equal(Catalog.Battle.GlimmerPerWin, (await Fight(p, (await Started(p, Trainer, monster)).Id, true)).GlimmerChange);
    }

    [Fact]
    public async Task A_fast_script_cant_level_a_monster_past_the_days_limit()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        await WithDb(async db =>
        {
            WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == monster);
            m.XpDay = today;
            m.XpToday = MonsterLevels.MaxXpPerDay - 10;
        });

        BattleResultDto result = await Fight(p, (await Started(p, Trainer, monster)).Id, true);

        Assert.Equal(10, result.MonsterXp);
    }

    // ------------------------------------------------------------------ stakes

    [Fact]
    public async Task A_stake_doubles_if_you_win_and_goes_if_you_lose()
    {
        Player winner = await NewPlayer(1000);
        BattleResultDto won = await Fight(winner, (await Started(winner, Trainer, await StarterOf(winner))).Id, true, 100);
        Assert.Equal(100 + Catalog.Battle.GlimmerPerWin, won.GlimmerChange);
        Assert.Equal(1000 + 100 + Catalog.Battle.GlimmerPerWin, (await Profile(winner)).Glimmer);

        Player loser = await NewPlayer(1000);
        BattleResultDto lost = await Fight(loser, (await Started(loser, Trainer, await StarterOf(loser))).Id, false, 100);
        Assert.Equal(Catalog.Battle.GlimmerPerLoss - 100, lost.GlimmerChange);
        Assert.Equal(1000 - 100 + Catalog.Battle.GlimmerPerLoss, (await Profile(loser)).Glimmer);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(201)]
    public async Task A_stake_has_limits(int stake)
    {
        Player p = await NewPlayer(5000);
        BattleDto battle = await Started(p, Trainer, await StarterOf(p));
        Assert.Equal(200, battle.MaxStake);

        HttpResponseMessage response = await p.Http.PostAsJsonAsync($"/api/walking/battles/{battle.Id}/fight", new BattleFightRequest("Balanced", false, stake));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(5000, (await Profile(p)).Glimmer);
    }

    [Fact]
    public async Task A_stake_is_at_most_a_fifth_of_your_Glimmer_and_what_today_can_still_pay()
    {
        Player p = await NewPlayer(100);
        BattleDto small = await Started(p, Trainer, await StarterOf(p));
        Assert.Equal(20, small.MaxStake);

        Player q = await NewPlayer(5000);
        await WithDb(async db =>
        {
            WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == q.Id);
            profile.BattleDay = DateTime.UtcNow.ToString("yyyy-MM-dd");
            profile.BattleGlimmerToday = BattleEndpoints.MaxGlimmerPerDay - 40;
        });
        Assert.Equal(40, (await Started(q, Trainer, await StarterOf(q))).MaxStake);
    }

    // ------------------------------------------------------------------ tonics and signature moves

    [Fact]
    public async Task A_tonic_is_used_up_and_you_need_one()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);

        BattleDto none = await Started(p, Trainer, monster);
        Assert.Equal(0, none.Tonics);
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Http.PostAsJsonAsync($"/api/walking/battles/{none.Id}/fight", new BattleFightRequest("Balanced", true))).StatusCode);

        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).Tonics = 2);
        BattleDto two = await Started(p, Trainer, monster);
        Assert.Equal(2, two.Tonics);

        BattleResultDto used = await Fight(p, two.Id, true, tonic: true);

        Assert.True(used.Tonic);
        Assert.Equal(1, used.Tonics);
        Assert.Equal(1, (await Profile(p)).Tonics);
    }

    [Fact]
    public async Task A_refused_fight_doesnt_use_up_your_tonic()
    {
        Player p = await NewPlayer();
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).Tonics = 1);
        BattleDto battle = await Started(p, Trainer, await StarterOf(p));

        HttpResponseMessage refused = await p.Http.PostAsJsonAsync($"/api/walking/battles/{battle.Id}/fight", new BattleFightRequest("Balanced", true, 99999));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(1, (await Profile(p)).Tonics);
    }

    [Fact]
    public async Task A_signature_move_needs_a_best_friend()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);

        BattleDto notYet = await Started(p, Trainer, monster);
        Assert.False(notYet.HasSignature);
        Assert.Equal(HttpStatusCode.BadRequest, (await p.Http.PostAsJsonAsync($"/api/walking/battles/{notYet.Id}/fight", new BattleFightRequest("Balanced", false, 0, true))).StatusCode);

        await WithDb(async db => (await db.WalkingMonsters.FirstAsync(m => m.Id == monster)).BondSteps = BattleEndpoints.BestFriendsSteps);
        BattleDto friends = await Started(p, Trainer, monster);
        Assert.True(friends.HasSignature);

        Assert.True((await Fight(p, friends.Id, true, signature: true)).Signature);
    }

    [Fact]
    public void A_signature_move_adds_ten_percent_rounding_halves_up()
    {
        var even = new DuelStats(100, 100, 100, 100);
        DuelOutcome plain = DuelRules.Resolve(1, even, DuelStrategy.Balanced, false, 0, false, even, DuelStrategy.Balanced, false, 0, false);
        DuelOutcome move = DuelRules.Resolve(1, even, DuelStrategy.Balanced, false, 0, false, even, DuelStrategy.Balanced, false, 0, false, true);

        Assert.Equal(plain.Player.Score + plain.Player.Score / 10, move.Player.Score);
        Assert.True(move.Player.Chance > plain.Player.Chance);

        // 625 points: a tenth is 62.5. Floats round that differently from place to place, so it mustn't be up to them.
        var round = new DuelStats(156, 156, 156, 157);
        Assert.Equal(625 + 63, DuelRules.Resolve(1, round, DuelStrategy.Balanced, false, 0, false, new DuelStats(1, 1000, 1, 1000), DuelStrategy.Balanced, false, 0, false, true).Player.Score);
    }

    // These same numbers are in the game's tests (ServerBattleTests): the phone replays what the server decided, so the
    // rules on both sides have to agree. Change them on one side and a test fails.
    [Fact]
    public void The_server_works_out_a_fight_exactly_as_the_game_does()
    {
        var a = new DuelStats(120, 90, 80, 100);
        var b = new DuelStats(100, 110, 95, 85);

        DuelOutcome plain = DuelRules.Resolve(12345L, a, DuelStrategy.Balanced, false, 0, false, b, DuelStrategy.Balanced, false, 0, false);
        Assert.Equal((0.5f, 0.661216855f, 400, 400, false), (plain.Player.Chance, plain.Roll, plain.Player.Score, plain.Opponent.Score, plain.PlayerWon));

        DuelOutcome everything = DuelRules.Resolve(987654321L, a, DuelStrategy.MaxPower, true, 0, true, b, DuelStrategy.Balanced, false, 0, true, true);
        Assert.Equal((0.589434683f, 0.557728231f, 636, 443, true), (everything.Player.Chance, everything.Roll, everything.Player.Score, everything.Opponent.Score, everything.PlayerWon));

        DuelOutcome negative = DuelRules.Resolve(-5551234567L, b, DuelStrategy.Balanced, false, 0, true, a, DuelStrategy.MaxPower, false, 0, false, true);
        Assert.Equal((0.5295337f, 0.8581393f, 511, 454, false), (negative.Player.Chance, negative.Roll, negative.Player.Score, negative.Opponent.Score, negative.PlayerWon));
    }

    [Theory]
    [InlineData(DuelStyle.Balanced, "MaxPower Balanced MaxPower MaxPower MaxPower Balanced")]
    [InlineData(DuelStyle.Random, "MaxTrickery MaxTrickery MaxWits MaxGuard MaxWits MaxTrickery")]
    [InlineData(DuelStyle.Aggressive, "MaxPower MaxPower MaxPower MaxPower MaxPower MaxPower")]
    [InlineData(DuelStyle.Defensive, "MaxGuard MaxGuard MaxGuard MaxGuard MaxGuard MaxGuard")]
    public void The_computer_chooses_exactly_as_it_does_in_the_game(DuelStyle style, string expected)
    {
        var a = new DuelStats(120, 90, 80, 100);
        var b = new DuelStats(100, 110, 95, 85);
        var picks = new List<string>();
        for (long seed = 1; seed <= 6; seed++)
        {
            picks.Add(DuelRules.PickForComputer(style, b, a, new DeterministicRandom(SeedUtility.Derive(seed, "ai"))).ToString());
        }

        Assert.Equal(expected, string.Join(" ", picks));
    }

    // ------------------------------------------------------------------ walking

    [Fact]
    public async Task Walking_earns_up_to_two_tonics_a_day_and_grows_your_companion()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).CompanionMonsterId = monster);

        async Task Walk(int added, int acceptedToday) => await WithDb(async db =>
        {
            WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id);
            await BattleEndpoints.OnStepsAsync(db, Catalog, profile, added, acceptedToday, true, DateTime.UtcNow, new List<string>(), CancellationToken.None);
        });

        await Walk(4999, 4999);
        Assert.Equal(0, (await Profile(p)).Tonics);
        await Walk(1, 5000);
        Assert.Equal(1, (await Profile(p)).Tonics);
        await Walk(20000, 25000);
        Assert.Equal(2, (await Profile(p)).Tonics);

        WalkingMonster after = await Monster(monster);
        Assert.Equal(MonsterLevels.FromTotal(25000 / Catalog.Battle.CompanionStepsPerXp), (after.Level, after.Experience));
    }

    [Fact]
    public async Task Steps_left_over_are_kept_for_the_next_point_of_experience()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).CompanionMonsterId = monster);

        for (int i = 0; i < 3; i++)
        {
            await WithDb(async db =>
            {
                WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id);
                await BattleEndpoints.OnStepsAsync(db, Catalog, profile, 3, 3 * (i + 1), true, DateTime.UtcNow, new List<string>(), CancellationToken.None);
            });
        }

        Assert.Equal(1, (await Monster(monster)).Experience);   // 9 steps at 5 a point: 1, and 4 left over
        Assert.Equal(4, (await Profile(p)).CompanionStepRemainder);
    }

    [Fact]
    public async Task A_starter_brings_only_so_much_bond_from_before_signing_in()
    {
        Player p = await NewPlayer();
        Guid monster = await StarterOf(p);

        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress",
            new ProgressRequest(null, new List<MonsterProgressDto> { new(monster, 1, 0, 0, 0, 0, 0, 0, 99_999_999) }));

        Assert.Equal(GameServices.Api.Walking.Progress.ProgressEndpoints.MostStartingSteps, (await Monster(monster)).BondSteps);
    }

    [Fact]
    public async Task A_monster_with_a_new_walker_starts_afresh_with_them()
    {
        Player a = await NewPlayer();
        Player b = await NewPlayer();
        Guid monster = await StarterOf(a);
        await WithDb(async db =>
        {
            WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == monster);
            m.RestUntilSteps = 5000000;
            m.BondSteps = 40000;
            await MonsterHandover.Move(db, m, b.Id, DateTime.UtcNow, null, null, null, CancellationToken.None);
        });

        WalkingMonster after = await Monster(monster);
        Assert.Equal(0, after.RestUntilSteps);
        Assert.Equal(0, after.BondSteps);
    }

    // ------------------------------------------------------------------ every Glimmer accounted for

    private Task<AuditReport> Audit(Player p) => FromDb(db => WalletAudit.RunAsync(db, p.Id));

    [Fact]
    public async Task The_books_add_up_after_every_kind_of_battle()
    {
        Player p = await NewPlayer(2000);
        Guid monster = await StarterOf(p);

        await Fight(p, (await Started(p, Trainer, monster)).Id, true, 100);
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).Tonics = 1);
        await Fight(p, (await Started(p, Trainer, monster)).Id, true, 50, tonic: true);
        await Fight(p, (await Started(p, "wild", monster)).Id, true, 25);
        await Fight(p, (await Started(p, Trainer, monster)).Id, false, 25);

        AuditReport report = await Audit(p);

        Assert.Empty(report.Problems);
        Assert.Equal(4, report.BattlesChecked);
        Assert.Equal((await Profile(p)).Glimmer, report.InCirculation);
        Assert.Equal(report.EverCreated - report.EverSpent, report.InCirculation);
        Assert.True(report.ByKind.ContainsKey("battle"));
        Assert.True(report.ByKind.ContainsKey("battle-stake"));
    }

    [Fact]
    public async Task The_audit_notices_Glimmer_that_appeared_from_nowhere()
    {
        Player p = await NewPlayer(100);
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).Glimmer += 5000);

        AuditReport report = await Audit(p);

        Assert.Contains(report.Problems, line => line.Contains("holds 5100"));
    }

    [Fact]
    public async Task The_audit_notices_a_ledger_line_that_was_changed()
    {
        Player p = await NewPlayer(100);
        await WithDb(async db =>
        {
            WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id);
            WalkingWallet.Change(db, profile, 50, "test", "More");
        });
        await WithDb(async db => (await db.WalkingLedger.Where(l => l.UserId == p.Id && l.Amount == 50).FirstAsync()).BalanceAfter = 9999);

        AuditReport report = await Audit(p);

        Assert.Contains(report.Problems, line => line.Contains("follow on"));
    }

    [Fact]
    public async Task One_break_in_the_books_is_reported_once_not_on_every_line_after_it()
    {
        Player p = await NewPlayer(100);
        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).Glimmer += 5000);   // from nowhere
        for (int i = 0; i < 3; i++)
        {
            await WithDb(async db =>
            {
                WalkingProfile profile = await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id);
                WalkingWallet.Change(db, profile, 10, "test", "A little more");
            });
        }

        AuditReport report = await Audit(p);

        Assert.Single(report.Problems, line => line.Contains("follow on"));
        Assert.Single(report.Problems, line => line.Contains("holds 5130 but its ledger adds up to 130"));
    }

    [Fact]
    public async Task The_audit_notices_a_battle_whose_result_was_changed()
    {
        Player p = await NewPlayer();
        BattleDto battle = await Started(p, Trainer, await StarterOf(p));
        await Fight(p, battle.Id, true);
        Assert.Empty((await Audit(p)).Problems);

        await WithDb(async db => (await db.WalkingBattles.FirstAsync(b => b.Id == battle.Id)).Won = false);

        Assert.Contains((await Audit(p)).Problems, line => line.Contains("played again"));
    }

    [Fact]
    public async Task Deleting_an_account_takes_its_battles_with_it()
    {
        Player p = await NewPlayer();
        BattleDto battle = await Started(p, Trainer, await StarterOf(p));
        await Fight(p, battle.Id, true);

        HttpResponseMessage deleted = await p.Http.PostAsJsonAsync("/api/auth/delete-account", new { Password = "CorrectHorse1" });

        Assert.True(deleted.IsSuccessStatusCode);
        Assert.False(await FromDb(db => db.WalkingBattles.AnyAsync(b => b.UserId == p.Id)));
    }

    // ------------------------------------------------------------------ the route the phone used to report on has gone

    [Fact]
    public async Task A_phone_cant_just_say_it_won_a_wild_battle_any_more()
    {
        Player p = await NewPlayer();
        HttpResponseMessage response = await p.Http.PostAsJsonAsync($"/api/walking/monsters/{await StarterOf(p)}/wild", new { Won = true, OpponentPower = 100000 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
