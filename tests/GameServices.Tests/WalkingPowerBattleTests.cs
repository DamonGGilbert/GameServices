using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// POWER BATTLES: an invitation the other walker accepts or declines. The winner's monster absorbs 5% of the loser's
/// power for good; the loser's becomes a crystal - it can't battle - until its walker walks it back. Fair fights only
/// (within 25% BST), never with a crystal, and 3 a day.
/// </summary>
public class WalkingPowerBattleTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public WalkingPowerBattleTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "p" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        await client.GetAsync("/api/walking/wallet");
        return new Player(client, auth.User.Id, name);
    }

    private async Task WithDb(Func<AppDbContext, Task> change)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    private async Task<WalkingMonster> Get(Guid monsterId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().WalkingMonsters.AsNoTracking().FirstAsync(m => m.Id == monsterId);
    }

    // The starter, made to a known recipe (same species, seed and level for both sides = an even, fair fight).
    private async Task<Guid> Monster(Player p, long seed = 42, int level = 10)
    {
        WalkingMonsterDto starter = (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0];
        await WithDb(async db =>
        {
            WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == starter.Id);
            m.Seed = seed;
            m.Level = level;
            m.RarityTier = 0;
            m.SpeciesId = "MON_0001";
        });
        return starter.Id;
    }

    private static Task<HttpResponseMessage> Invite(Player from, Player to, Guid monster, bool power = true) =>
        from.Http.PostAsJsonAsync("/api/walking/challenges", new ChallengeSendRequest(to.Name, null, monster, 0, "Balanced", null, power));

    private static async Task<ChallengeDto> InviteOk(Player from, Player to, Guid monster)
    {
        HttpResponseMessage r = await Invite(from, to, monster);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ChallengeDto>())!;
    }

    private static Task<HttpResponseMessage> Accept(Player p, Guid challenge, Guid monster) =>
        p.Http.PostAsJsonAsync($"/api/walking/challenges/{challenge}/accept", new ChallengeAcceptRequest(monster, "Balanced"));

    [Fact]
    public async Task TheWinnerAbsorbsPower_AndTheLoserBecomesACrystal()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        Guid a = await Monster(alice), b = await Monster(bob);

        ChallengeDto invite = await InviteOk(alice, bob, a);
        Assert.True(invite.PowerBattle);
        HttpResponseMessage r = await Accept(bob, invite.Id, b);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        ChallengeDto fought = (await r.Content.ReadFromJsonAsync<ChallengeDto>())!;

        (Guid winnerId, Guid loserId) = fought.IWon ? (b, a) : (a, b);
        WalkingMonster winner = await Get(winnerId), loser = await Get(loserId);
        int loserPower = fought.FromMonster.Stats.Sum();   // both start the same, so the loser's power is either side's
        int moved = (int)Math.Round(loserPower * PowerBattles.AbsorbShare);

        Assert.Equal(moved, winner.AbsorbedPower + winner.AbsorbedGuard + winner.AbsorbedTricks + winner.AbsorbedWits);
        Assert.Equal(0, winner.CrystalStepsLeft);
        Assert.Equal(moved, loser.CrystalPower);
        Assert.Equal(PowerBattles.CrystalSteps, loser.CrystalStepsLeft);
        Assert.Equal(fought.IWon ? moved : -moved, fought.PowerChange);

        // The winner's absorbed power is part of its stats from now on (and shows in its monster list).
        WalkingMonsterDto listed = (await (fought.IWon ? bob : alice).Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))!.First(m => m.Id == winnerId);
        Assert.Equal(moved, listed.AbsorbedPower + listed.AbsorbedGuard + listed.AbsorbedTricks + listed.AbsorbedWits);
    }

    [Fact]
    public async Task ACrystal_CantBattle_UntilItsWalkedBack()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        Guid a = await Monster(alice), b = await Monster(bob);
        await WithDb(async db =>
        {
            WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == a);
            m.CrystalPower = 50;
            m.CrystalSteps = PowerBattles.CrystalSteps;
            m.CrystalStepsLeft = PowerBattles.CrystalSteps;
        });

        Assert.Contains("crystal", await (await Invite(alice, bob, a)).Content.ReadAsStringAsync());
        Assert.Contains("crystal", await (await Invite(alice, bob, a, power: false)).Content.ReadAsStringAsync());

        // Walking brings it back: part of the way, then all of it.
        await WithDb(db => PowerBattles.WalkBackAsync(db, alice.Id, 2000, CancellationToken.None));
        Assert.Equal(PowerBattles.CrystalSteps - 2000, (await Get(a)).CrystalStepsLeft);
        await WithDb(db => PowerBattles.WalkBackAsync(db, alice.Id, 10000, CancellationToken.None));
        WalkingMonster back = await Get(a);
        Assert.Equal(0, back.CrystalStepsLeft);
        Assert.Equal(0, back.CrystalPower);
        Assert.Equal(HttpStatusCode.OK, (await Invite(alice, bob, a)).StatusCode);
    }

    [Fact]
    public async Task APowerBattle_MustBeAFairFight()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        Guid a = await Monster(alice, level: 5), b = await Monster(bob, level: 60);
        ChallengeDto invite = await InviteOk(alice, bob, a);

        HttpResponseMessage r = await Accept(bob, invite.Id, b);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("within 25%", await r.Content.ReadAsStringAsync());
        Assert.Equal(0, (await Get(a)).CrystalStepsLeft);   // nothing happened
    }

    [Fact]
    public async Task ThreePowerBattlesADay_IsTheLimit()
    {
        Player alice = await NewPlayer();
        Guid a = await Monster(alice);
        for (int i = 0; i < PowerBattles.MaxPerDay; i++)
        {
            Player other = await NewPlayer();
            await InviteOk(alice, other, a);
        }

        Player one = await NewPlayer();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Invite(alice, one, a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Invite(alice, one, a, power: false)).StatusCode);   // ordinary challenges still fine
    }

    [Fact]
    public async Task Declining_RisksNothing()
    {
        Player alice = await NewPlayer(), bob = await NewPlayer();
        Guid a = await Monster(alice), b = await Monster(bob);
        ChallengeDto invite = await InviteOk(alice, bob, a);
        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.PostAsync($"/api/walking/challenges/{invite.Id}/decline", null)).StatusCode);
        Assert.Equal(0, (await Get(a)).CrystalStepsLeft);
        Assert.Equal(0, (await Get(b)).CrystalStepsLeft);
        Assert.Equal(0, (await Get(a)).AbsorbedPower);
    }

    [Theory]
    [InlineData(37, new[] { 100, 200, 300, 400 })]
    [InlineData(5, new[] { 1, 1, 1, 1 })]
    [InlineData(1, new[] { 0, 0, 10, 0 })]
    public void AbsorbedPower_IsSharedOutExactly(int amount, int[] weights)
    {
        int[] split = PowerBattles.Split(amount, weights);
        Assert.Equal(amount, split.Sum());
        Assert.All(split, s => Assert.True(s >= 0));
    }
}
