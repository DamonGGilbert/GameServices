using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// RANKED FIGHTS: entering monsters, matchmaking by BST (10% first, widening), shadow monsters when nobody's in range,
/// the server deciding the fight, the daily Glimmer cap, and history.
/// </summary>
public class RankedTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public RankedTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "r" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        return new Player(client, auth.User.Id, name);
    }

    private async Task WithDb(Func<AppDbContext, Task> change)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    // Tests share one database: each matchmaking test starts with an empty ranked pool, so it knows who's in it.
    private Task EmptyPool() => WithDb(async db => db.RankedEntries.RemoveRange(await db.RankedEntries.ToListAsync()));

    private static async Task<WalkingMonsterDto> Starter(Player p) => (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0];

    // A starter, made to a known recipe (so tests know its BST), and entered into ranked.
    private async Task<Guid> RankedMonster(Player p, long seed, int level, int tier = 0, string species = "MON_0001")
    {
        WalkingMonsterDto starter = await Starter(p);
        await WithDb(async db =>
        {
            WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == starter.Id);
            m.Seed = seed;
            m.Level = level;
            m.RarityTier = tier;
            m.SpeciesId = species;
        });
        HttpResponseMessage r = await p.Http.PutAsJsonAsync("/api/walking/ranked/entries",
            new RankedEntriesRequest(new List<RankedEntryRequest> { new(starter.Id, "Balanced") }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return starter.Id;
    }

    private static async Task<RankedMatchDto> Match(Player p, Guid monster)
    {
        HttpResponseMessage r = await p.Http.PostAsJsonAsync("/api/walking/ranked/match", new RankedMatchRequest(monster));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<RankedMatchDto>())!;
    }

    private static int Bst(long seed, int level, int tier = 0, string species = "MON_0001")
    {
        MonsterCatalog catalog = MonsterCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "Walking", "monster-catalog.json"));
        return MonsterStats.For(catalog, species, seed, tier, level, 0, 0, 0, 0).Total;
    }

    // A huge BST nobody else in the tests has, so matches with the "real" players below are deliberate.
    private const string Rare = "MON_0016";

    // ------------------------------------------------------------------ entering

    [Fact]
    public async Task Up_to_three_of_your_own_monsters_can_be_entered()
    {
        Player p = await NewPlayer(), other = await NewPlayer();
        WalkingMonsterDto mine = await Starter(p), theirs = await Starter(other);

        HttpResponseMessage notMine = await p.Http.PutAsJsonAsync("/api/walking/ranked/entries",
            new RankedEntriesRequest(new List<RankedEntryRequest> { new(theirs.Id, "Balanced") }));
        Assert.Equal(HttpStatusCode.BadRequest, notMine.StatusCode);

        HttpResponseMessage four = await p.Http.PutAsJsonAsync("/api/walking/ranked/entries",
            new RankedEntriesRequest(Enumerable.Range(0, 4).Select(_ => new RankedEntryRequest(Guid.NewGuid(), "Balanced")).ToList()));
        Assert.Equal(HttpStatusCode.BadRequest, four.StatusCode);

        HttpResponseMessage ok = await p.Http.PutAsJsonAsync("/api/walking/ranked/entries",
            new RankedEntriesRequest(new List<RankedEntryRequest> { new(mine.Id, "Defensive") }));
        RankedMeDto me = (await ok.Content.ReadFromJsonAsync<RankedMeDto>())!;
        Assert.Equal("Defensive", Assert.Single(me.Entries).Style);
        Assert.True(me.Entries[0].Bst > 0);
    }

    [Fact]
    public async Task Only_entered_monsters_can_fight()
    {
        Player p = await NewPlayer();
        WalkingMonsterDto mine = await Starter(p);
        HttpResponseMessage r = await p.Http.PostAsJsonAsync("/api/walking/ranked/match", new RankedMatchRequest(mine.Id));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ------------------------------------------------------------------ matchmaking

    [Fact]
    public async Task An_opponent_with_a_similar_BST_is_found()
    {
        await EmptyPool();
        Player alice = await NewPlayer(), bob = await NewPlayer();
        long seed = 900_001;
        Guid a = await RankedMonster(alice, seed, 77, 4, Rare);
        await RankedMonster(bob, seed + 1, 77, 4, Rare);

        RankedMatchDto match = await Match(alice, a);
        Assert.False(match.Shadow);
        Assert.Equal(bob.Name, match.OpponentName);
        Assert.Equal(Bst(seed + 1, 77, 4, Rare), match.OpponentBst);
        Assert.Equal(Bst(seed, 77, 4, Rare), match.Bst);
        Assert.InRange(Math.Abs(match.OpponentBst - match.Bst), 0, match.Bst * match.WindowPercent / 100.0);
    }

    [Fact]
    public async Task Nobody_in_range_means_a_shadow_monster_near_your_BST()
    {
        await EmptyPool();
        Player p = await NewPlayer();
        Guid m = await RankedMonster(p, 31337, 99, 4, "MON_0013");   // nobody else is anywhere near this
        RankedMatchDto match = await Match(p, m);

        Assert.True(match.Shadow);
        Assert.StartsWith("Shadow", match.OpponentName);
        Assert.InRange(match.OpponentBst, match.Bst * 0.85, match.Bst * 1.15);
    }

    [Fact]
    public async Task Blocked_players_are_never_matched()
    {
        await EmptyPool();
        Player alice = await NewPlayer(), bob = await NewPlayer();
        Guid a = await RankedMonster(alice, 800_001, 83, 4, Rare);
        await RankedMonster(bob, 800_002, 83, 4, Rare);
        await WithDb(db => { db.Blocks.Add(new UserBlock { BlockerId = bob.Id, BlockedId = alice.Id }); return Task.CompletedTask; });

        RankedMatchDto match = await Match(alice, a);
        Assert.NotEqual(bob.Name, match.OpponentName);
    }

    // ------------------------------------------------------------------ fighting

    [Fact]
    public async Task The_server_decides_the_fight_and_it_can_be_checked()
    {
        await EmptyPool();
        Player alice = await NewPlayer(), bob = await NewPlayer();
        Guid a = await RankedMonster(alice, 700_001, 88, 4, Rare);
        Guid b = await RankedMonster(bob, 700_002, 88, 4, Rare);
        RankedMatchDto match = await Match(alice, a);

        HttpResponseMessage r = await alice.Http.PostAsJsonAsync($"/api/walking/ranked/matches/{match.Id}/fight", new RankedFightRequest("MaxPower"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        RankedFightResultDto result = (await r.Content.ReadFromJsonAsync<RankedFightResultDto>())!;

        // Replaying the same fight from its seed gives the same answer.
        long seed = 0;
        await WithDb(async db => seed = (await db.RankedMatches.FirstAsync(x => x.Id == match.Id)).Seed);
        MonsterCatalog catalog = MonsterCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "Walking", "monster-catalog.json"));
        DuelStats mine = DuelStats.From(MonsterStats.For(catalog, Rare, 700_001, 4, 88, 0, 0, 0, 0));
        DuelStats theirs = DuelStats.From(MonsterStats.For(catalog, Rare, 700_002, 4, 88, 0, 0, 0, 0));
        DuelStrategy defence = DuelRules.PickForComputer(DuelStyle.Balanced, theirs, mine, new DeterministicRandom(SeedUtility.Derive(seed, "defence")));
        DuelOutcome replay = DuelRules.ResolveRanked(seed, mine, DuelStrategy.MaxPower, theirs, defence);

        Assert.Equal(replay.PlayerWon, result.Won);
        Assert.Equal(replay.Player.Chance, result.Chance);
        Assert.Equal(defence.ToString(), result.OpponentStrategy);

        // The fight is over: it can't be fought again.
        HttpResponseMessage again = await alice.Http.PostAsJsonAsync($"/api/walking/ranked/matches/{match.Id}/fight", new RankedFightRequest("MaxPower"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // Both records moved, and both see it in their history.
        RankedMeDto aliceMe = (await alice.Http.GetFromJsonAsync<RankedMeDto>("/api/walking/ranked"))!;
        RankedMeDto bobMe = (await bob.Http.GetFromJsonAsync<RankedMeDto>("/api/walking/ranked"))!;
        Assert.Equal(1, aliceMe.Wins + aliceMe.Losses);
        Assert.Equal(result.Won ? 1 : 0, bobMe.Losses);
        List<RankedHistoryDto> bobHistory = (await bob.Http.GetFromJsonAsync<List<RankedHistoryDto>>("/api/walking/ranked/history"))!;
        RankedHistoryDto defended = Assert.Single(bobHistory);
        Assert.False(defended.Attacking);
        Assert.Equal(alice.Name, defended.OtherName);
        Assert.Equal(!result.Won, defended.Won);
    }

    // Makes the next fight a sure win: a tiny defender, and a seed whose spin lands well inside 95%.
    private Task RigForWin(Guid matchId) => WithDb(async db =>
    {
        RankedMatch m = await db.RankedMatches.FirstAsync(x => x.Id == matchId);
        m.DefenderPower = m.DefenderGuard = m.DefenderTricks = m.DefenderWits = 1;
        m.Seed = 1;   // spin 0.54 (see DuelRulesTests)
    });

    [Fact]
    public async Task A_win_pays_Glimmer_until_the_daily_cap()
    {
        await EmptyPool();
        Player p = await NewPlayer();
        Guid m = await RankedMonster(p, 600_001, 95, 4, "MON_0012");

        RankedMatchDto first = await Match(p, m);
        await RigForWin(first.Id);
        RankedFightResultDto won = (await (await p.Http.PostAsJsonAsync($"/api/walking/ranked/matches/{first.Id}/fight", new RankedFightRequest("Balanced")))
            .Content.ReadFromJsonAsync<RankedFightResultDto>())!;
        Assert.True(won.Won);
        Assert.Equal(first.Shadow ? RankedEndpoints.ShadowWinGlimmer : RankedEndpoints.WinGlimmer, won.Reward);
        Assert.Equal(won.Reward, (await p.Http.GetFromJsonAsync<WalletDto>("/api/walking/wallet"))!.Glimmer);

        await WithDb(async db => (await db.WalkingProfiles.FirstAsync(x => x.UserId == p.Id)).RankedRewardedWinsToday = RankedEndpoints.RewardedWinsPerDay);
        RankedMatchDto second = await Match(p, m);
        await RigForWin(second.Id);
        RankedFightResultDto capped = (await (await p.Http.PostAsJsonAsync($"/api/walking/ranked/matches/{second.Id}/fight", new RankedFightRequest("Balanced")))
            .Content.ReadFromJsonAsync<RankedFightResultDto>())!;
        Assert.True(capped.Won);
        Assert.Equal(0, capped.Reward);   // still a win, but no more Glimmer today
    }

    [Fact]
    public async Task A_fight_left_too_long_times_out()
    {
        await EmptyPool();
        Player p = await NewPlayer();
        Guid m = await RankedMonster(p, 500_001, 90, 3, "MON_0012");
        RankedMatchDto match = await Match(p, m);
        await WithDb(async db => (await db.RankedMatches.FirstAsync(x => x.Id == match.Id)).ExpiresUtc = DateTime.UtcNow.AddMinutes(-1));

        HttpResponseMessage r = await p.Http.PostAsJsonAsync($"/api/walking/ranked/matches/{match.Id}/fight", new RankedFightRequest("Balanced"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public void A_shadow_lands_close_to_any_BST()
    {
        MonsterCatalog catalog = MonsterCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "Walking", "monster-catalog.json"));
        foreach (int target in new[] { 300, 2000, 6000, 10000 })
        {
            var shadow = RankedEndpoints.MakeShadow(catalog, target, target > 5000 ? 4 : 1);
            Assert.InRange(shadow.Stats.Total, target * 0.8, target * 1.2);
        }
    }
}
