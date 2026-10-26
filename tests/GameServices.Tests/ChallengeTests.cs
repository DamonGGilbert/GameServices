using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// CHALLENGES: stakes held by the server, the winner taking both, refunds when declined, cancelled or expired,
/// ranked-or-friendly by BST, the limits, and countries.
/// </summary>
public class ChallengeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public ChallengeTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(int glimmer = 0)
    {
        HttpClient client = factory.CreateClient();
        string name = "c" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        await client.GetAsync("/api/walking/wallet");   // makes the walking profile
        if (glimmer > 0)
        {
            await WithDb(async db => (await db.WalkingProfiles.FirstAsync(p => p.UserId == auth.User.Id)).Glimmer = glimmer);
        }
        return new Player(client, auth.User.Id, name);
    }

    private async Task WithDb(Func<AppDbContext, Task> change)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    private static async Task<int> Glimmer(Player p) => (await p.Http.GetFromJsonAsync<WalletDto>("/api/walking/wallet"))!.Glimmer;

    // The starter, made to a known recipe so tests control its BST.
    private async Task<Guid> Monster(Player p, long seed, int level, int tier = 0, string species = "MON_0001")
    {
        WalkingMonsterDto starter = (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0];
        await WithDb(async db =>
        {
            WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == starter.Id);
            m.Seed = seed;
            m.Level = level;
            m.RarityTier = tier;
            m.SpeciesId = species;
        });
        return starter.Id;
    }

    private static async Task<ChallengeDto> Send(Player from, Player to, Guid monster, int stake, string strategy = "Balanced")
    {
        HttpResponseMessage r = await from.Http.PostAsJsonAsync("/api/walking/challenges", new ChallengeSendRequest(to.Name, null, monster, stake, strategy));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ChallengeDto>())!;
    }

    private static async Task<ChallengeDto> Accept(Player p, Guid challenge, Guid monster, string strategy = "MaxPower")
    {
        HttpResponseMessage r = await p.Http.PostAsJsonAsync($"/api/walking/challenges/{challenge}/accept", new ChallengeAcceptRequest(monster, strategy));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ChallengeDto>())!;
    }

    private static Task<ChallengeListDto> List(Player p) => p.Http.GetFromJsonAsync<ChallengeListDto>("/api/walking/challenges")!;

    // ------------------------------------------------------------------ the fight and the pot

    [Fact]
    public async Task The_stakes_are_held_and_the_winner_takes_both()
    {
        Player alice = await NewPlayer(300), bob = await NewPlayer(300);
        Guid a = await Monster(alice, 41, 20), b = await Monster(bob, 42, 20);

        ChallengeDto sent = await Send(alice, bob, a, 100, "MaxGuard");
        Assert.Equal(200, await Glimmer(alice));   // held by the server
        Assert.Equal("Pending", sent.Status);
        Assert.Null(sent.Fight);                   // nobody sees anyone's strategy yet

        ChallengeListDto bobs = await List(bob);
        ChallengeDto waiting = Assert.Single(bobs.Incoming);
        Assert.Equal(alice.Name, waiting.OtherName);
        Assert.Equal(100, waiting.Stake);
        Assert.True(waiting.FromMonster.Bst > 0);
        Assert.Single((await List(alice)).Outgoing);

        ChallengeDto result = await Accept(bob, sent.Id, b);
        Assert.Equal("Fought", result.Status);
        Assert.NotNull(result.Fight);
        Assert.NotNull(result.ToMonster);

        bool aliceWon = result.Fight!.FromWon;
        Assert.Equal(!aliceWon, result.IWon);
        Assert.Equal(aliceWon ? 400 : 200, await Glimmer(alice));
        Assert.Equal(aliceWon ? 200 : 400, await Glimmer(bob));

        // The phone can replay it exactly from what it's given.
        int[] f = result.FromMonster.Stats, t = result.ToMonster!.Stats;
        DuelOutcome replay = DuelRules.ResolveRanked(result.Fight.Seed,
            DuelStats.From(new MonsterBattleStats(f[0], f[1], f[2], f[3], 0)), DuelStrategy.MaxGuard,
            DuelStats.From(new MonsterBattleStats(t[0], t[1], t[2], t[3], 0)), DuelStrategy.MaxPower);
        Assert.Equal(aliceWon, replay.PlayerWon);
        Assert.Equal(result.Fight.Roll, replay.Roll);

        // It's now a result for both of them, and nothing's waiting.
        ChallengeListDto after = await List(alice);
        Assert.Empty(after.Outgoing);
        Assert.Equal(aliceWon, Assert.Single(after.Recent).IWon);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.Http.PostAsJsonAsync($"/api/walking/challenges/{sent.Id}/accept", new ChallengeAcceptRequest(b, "Balanced"))).StatusCode);
    }

    [Fact]
    public async Task You_need_the_Glimmer_to_match_their_stake()
    {
        Player alice = await NewPlayer(100), bob = await NewPlayer(10);
        ChallengeDto sent = await Send(alice, bob, await Monster(alice, 1, 5), 50);
        HttpResponseMessage r = await bob.Http.PostAsJsonAsync($"/api/walking/challenges/{sent.Id}/accept", new ChallengeAcceptRequest(await Monster(bob, 2, 5), "Balanced"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Single((await List(bob)).Incoming);   // still there for when they have it
    }

    [Fact]
    public async Task A_free_challenge_needs_no_Glimmer()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        ChallengeDto sent = await Send(alice, bob, await Monster(alice, 3, 5), 0);
        ChallengeDto result = await Accept(bob, sent.Id, await Monster(bob, 4, 5));
        Assert.Equal(0, result.GlimmerChange);
        Assert.Equal(0, await Glimmer(alice) + await Glimmer(bob));
    }

    // ------------------------------------------------------------------ ranked or friendly

    [Fact]
    public async Task Close_monsters_fight_for_Ranked_Points()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        ChallengeDto sent = await Send(alice, bob, await Monster(alice, 51, 30), 0);
        ChallengeDto result = await Accept(bob, sent.Id, await Monster(bob, 52, 30));

        Assert.True(result.Ranked);
        Assert.NotEqual(0, result.MyPointsChange);
        string season = RankedPoints.SeasonOf(DateTime.UtcNow);
        await WithDb(async db =>
        {
            RankedSeasonRecord me = await db.RankedSeasons.FirstAsync(r => r.UserId == bob.Id && r.Season == season);
            Assert.Equal(result.IWon == true ? 1 : 0, me.Wins);
            Assert.Equal(result.IWon == true ? 0 : 1, me.Losses);
            WalkingMonster theirs = await db.WalkingMonsters.FirstAsync(m => m.Id == sent.FromMonster.Id);
            Assert.Equal(result.IWon == true ? 1 : 0, theirs.RankedLosses);
        });
    }

    [Fact]
    public async Task Mismatched_monsters_make_a_friendly()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        ChallengeDto sent = await Send(alice, bob, await Monster(alice, 61, 90, 4, "MON_0016"), 0);
        ChallengeDto result = await Accept(bob, sent.Id, await Monster(bob, 62, 1));

        Assert.False(result.Ranked);
        Assert.Equal(0, result.MyPointsChange);
        await WithDb(async db =>
        {
            Assert.False(await db.RankedSeasons.AnyAsync(r => r.UserId == bob.Id && r.Wins + r.Losses > 0));
        });
    }

    // ------------------------------------------------------------------ refunds

    [Fact]
    public async Task Declining_or_cancelling_gives_the_stake_back()
    {
        Player alice = await NewPlayer(200), bob = await NewPlayer(), carol = await NewPlayer();
        Guid a = await Monster(alice, 7, 5);

        ChallengeDto toBob = await Send(alice, bob, a, 50);
        ChallengeDto toCarol = await Send(alice, carol, a, 50);
        Assert.Equal(100, await Glimmer(alice));

        // Only the person challenged can decline; only the challenger can cancel.
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Http.PostAsync($"/api/walking/challenges/{toBob.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await carol.Http.PostAsync($"/api/walking/challenges/{toCarol.Id}/cancel", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.PostAsync($"/api/walking/challenges/{toBob.Id}/decline", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.PostAsync($"/api/walking/challenges/{toCarol.Id}/cancel", null)).StatusCode);
        Assert.Equal(200, await Glimmer(alice));
        Assert.Empty((await List(alice)).Outgoing);
        Assert.Equal(HttpStatusCode.Conflict, (await bob.Http.PostAsync($"/api/walking/challenges/{toBob.Id}/decline", null)).StatusCode);
    }

    [Fact]
    public async Task Unanswered_challenges_expire_and_the_stake_comes_back()
    {
        Player alice = await NewPlayer(100), bob = await NewPlayer();
        ChallengeDto sent = await Send(alice, bob, await Monster(alice, 8, 5), 60);
        Assert.Equal(DateTime.UtcNow + ChallengeEndpoints.Lifetime, sent.ExpiresUtc, TimeSpan.FromMinutes(1));

        await WithDb(async db => (await db.Challenges.FirstAsync(c => c.Id == sent.Id)).ExpiresUtc = DateTime.UtcNow.AddMinutes(-1));

        Assert.Empty((await List(bob)).Incoming);
        Assert.Equal(100, await Glimmer(alice));
        await WithDb(async db => Assert.Equal(ChallengeStatus.Expired, (await db.Challenges.FirstAsync(c => c.Id == sent.Id)).Status));
    }

    [Fact]
    public async Task If_their_monster_has_gone_the_challenge_is_called_off()
    {
        Player alice = await NewPlayer(100), bob = await NewPlayer(100);
        Guid a = await Monster(alice, 9, 5);
        ChallengeDto sent = await Send(alice, bob, a, 40);
        await WithDb(async db => (await db.WalkingMonsters.FirstAsync(m => m.Id == a)).OwnerId = bob.Id);   // traded away

        HttpResponseMessage r = await bob.Http.PostAsJsonAsync($"/api/walking/challenges/{sent.Id}/accept", new ChallengeAcceptRequest(await Monster(bob, 10, 5), "Balanced"));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(100, await Glimmer(alice));
        Assert.Equal(100, await Glimmer(bob));
    }

    // ------------------------------------------------------------------ limits

    [Fact]
    public async Task Challenges_have_sensible_limits()
    {
        Player alice = await NewPlayer(5000), bob = await NewPlayer();
        Guid a = await Monster(alice, 11, 5);
        Guid notMine = await Monster(bob, 12, 5);

        async Task<HttpStatusCode> Try(string to, Guid monster, int stake, string strategy = "Balanced") =>
            (await alice.Http.PostAsJsonAsync("/api/walking/challenges", new ChallengeSendRequest(to, null, monster, stake, strategy))).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Try(alice.Name, a, 0));                       // yourself
        Assert.Equal(HttpStatusCode.NotFound, await Try("nobody" + Guid.NewGuid().ToString("N")[..6], a, 0));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(bob.Name, notMine, 0));                   // not your monster
        Assert.Equal(HttpStatusCode.BadRequest, await Try(bob.Name, a, ChallengeEndpoints.MaxStake + 1));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(bob.Name, a, -5));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(bob.Name, a, 0, "Sneaky"));

        Assert.Equal(HttpStatusCode.OK, await Try(bob.Name, a, 0));
        Assert.Equal(HttpStatusCode.Conflict, await Try(bob.Name, a, 0));                           // one at a time each

        for (int i = 1; i < ChallengeEndpoints.MaxOpenSent; i++)
        {
            Player other = await NewPlayer();
            Assert.Equal(HttpStatusCode.OK, await Try(other.Name, a, 0));
        }
        Player oneTooMany = await NewPlayer();
        Assert.Equal(HttpStatusCode.TooManyRequests, await Try(oneTooMany.Name, a, 0));
    }

    [Fact]
    public async Task Blocked_walkers_cant_challenge_each_other()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        await bob.Http.PostAsJsonAsync("/api/blocks", new UsernameRequest(alice.Name));
        HttpResponseMessage r = await alice.Http.PostAsJsonAsync("/api/walking/challenges",
            new ChallengeSendRequest(bob.Name, null, await Monster(alice, 13, 5), 0, "Balanced"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task You_cant_spend_Glimmer_you_dont_have()
    {
        Player alice = await NewPlayer(20), bob = await NewPlayer();
        HttpResponseMessage r = await alice.Http.PostAsJsonAsync("/api/walking/challenges",
            new ChallengeSendRequest(bob.Name, null, await Monster(alice, 14, 5), 50, "Balanced"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(20, await Glimmer(alice));
    }

    // ------------------------------------------------------------------ countries

    [Fact]
    public async Task Walkers_choose_the_country_they_show()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.Http.PutAsJsonAsync("/api/walking/country", new CountryRequest("Spain"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.PutAsJsonAsync("/api/walking/country", new CountryRequest("es"))).StatusCode);

        Assert.Equal("ES", (await List(alice)).MyCountry);
        await Send(alice, bob, await Monster(alice, 15, 5), 0);
        Assert.Equal("ES", Assert.Single((await List(bob)).Incoming).OtherCountry);

        Assert.Equal(HttpStatusCode.NoContent, (await alice.Http.PutAsJsonAsync("/api/walking/country", new CountryRequest(""))).StatusCode);
        Assert.Equal("", (await List(alice)).MyCountry);
    }
}
