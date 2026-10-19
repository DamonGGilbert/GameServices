using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GameServices.Api.Auth;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using GameServices.Api.Social;
using GameServices.Api.Walking;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameServices.Tests;

/// <summary>
/// THE WALKING GAME'S GLIMMER, PACKS AND OFFERS: Glimmer kept on the server (walking earns it, phone rewards are
/// capped), packs paid for with it, and friendly offers for a friend's monster - with every safety rule.
/// </summary>
public class WalkingTradeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public WalkingTradeTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "t" + Guid.NewGuid().ToString("N")[..10];
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

    private async Task WithDb(Func<AppDbContext, Task> change)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }

    private Task GiveGlimmer(Player p, int amount) => WithDb(async db =>
    {
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, p.Id, CancellationToken.None);
        profile.WalletStarted = true;
        profile.Glimmer = amount;
    });

    private async Task<int> Balance(Player p) => (await p.Http.GetFromJsonAsync<WalletDto>("/api/walking/wallet"))!.Glimmer;

    private static async Task<WalkingMonsterDto> Starter(Player p) => (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0];

    // A monster that has been with its walker long enough to be offered for.
    private Task Settle(Guid monsterId) => WithDb(async db =>
    {
        WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == monsterId);
        m.AcquiredUtc = DateTime.UtcNow.AddDays(-4);
        m.FoundUtc = DateTime.UtcNow.AddDays(-4);
    });

    private static Task<HttpResponseMessage> Offer(Player buyer, Guid monsterId, int price) =>
        buyer.Http.PostAsJsonAsync("/api/walking/offers", new OfferRequest(monsterId, price));

    // ------------------------------------------------------------------ the wallet

    [Fact]
    public async Task The_first_sync_brings_the_phone_balance_across_up_to_1000()
    {
        Player p = await NewPlayer();
        WalletDto wallet = (await (await p.Http.PostAsJsonAsync("/api/walking/wallet/sync", new WalletSyncRequest(0, 5000))).Content.ReadFromJsonAsync<WalletDto>())!;
        Assert.Equal(1000, wallet.Glimmer);
    }

    [Fact]
    public async Task Phone_rewards_count_up_to_300_a_day_and_spending_always_counts()
    {
        Player p = await NewPlayer();
        await GiveGlimmer(p, 100);

        await p.Http.PostAsJsonAsync("/api/walking/wallet/sync", new WalletSyncRequest(250, 0));
        await p.Http.PostAsJsonAsync("/api/walking/wallet/sync", new WalletSyncRequest(250, 0));   // only 50 more fits today
        Assert.Equal(400, await Balance(p));

        await p.Http.PostAsJsonAsync("/api/walking/wallet/sync", new WalletSyncRequest(-150, 0));
        Assert.Equal(250, await Balance(p));
    }

    [Fact]
    public async Task Walking_earns_one_Glimmer_per_100_steps()
    {
        Player p = await NewPlayer();
        DateTime now = DateTime.UtcNow;
        int offset = Math.Clamp((int)Math.Round((720 - now.TimeOfDay.TotalMinutes) / 15.0) * 15, -720, 720);
        await p.Http.PostAsJsonAsync("/api/walking/steps", new StepReportRequest(now.AddMinutes(offset).ToString("yyyy-MM-dd"), 4250, offset));

        Assert.Equal(42, await Balance(p));
    }

    // ------------------------------------------------------------------ packs

    [Fact]
    public async Task Packs_list_their_exact_odds()
    {
        List<PackDto> packs = (await (await NewPlayer()).Http.GetFromJsonAsync<List<PackDto>>("/api/walking/packs"))!;
        Assert.Contains(packs, p => p.Id == "trail" && p.Odds.Any(o => o.Contains("chance of a sealed monster card")));
    }

    [Fact]
    public async Task Opening_a_pack_costs_Glimmer_and_gives_tonics_and_Glimmer_back()
    {
        Player p = await NewPlayer();
        await GiveGlimmer(p, 1000);

        PackResult result = (await (await p.Http.PostAsJsonAsync("/api/walking/packs/trail/open", new PackOpenRequest())).Content.ReadFromJsonAsync<PackResult>())!;

        Assert.InRange(result.Tonics, 1, 2);
        Assert.InRange(result.GlimmerBack, 25, 100);
        Assert.Equal(1000 - 250 + result.GlimmerBack, result.Glimmer);
        Assert.Equal(result.Glimmer, await Balance(p));
    }

    [Fact]
    public async Task No_Glimmer_no_pack_and_never_more_than_three_a_day()
    {
        Player poor = await NewPlayer();
        Assert.Equal(HttpStatusCode.BadRequest, (await poor.Http.PostAsJsonAsync("/api/walking/packs/trail/open", new PackOpenRequest())).StatusCode);

        Player rich = await NewPlayer();
        await GiveGlimmer(rich, 10000);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await rich.Http.PostAsJsonAsync("/api/walking/packs/trail/open", new PackOpenRequest())).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await rich.Http.PostAsJsonAsync("/api/walking/packs/trail/open", new PackOpenRequest())).StatusCode);
    }

    // ------------------------------------------------------------------ offers

    [Fact]
    public async Task Offers_are_for_friends_only()
    {
        Player owner = await NewPlayer();
        Player stranger = await NewPlayer();
        await GiveGlimmer(stranger, 1000);
        WalkingMonsterDto monster = await Starter(owner);
        await Settle(monster.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await Offer(stranger, monster.Id, 200)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/walking/friends/{owner.Id}/monsters")).StatusCode);
    }

    [Fact]
    public async Task A_companion_a_newcomer_and_silly_prices_cannot_be_offered_for()
    {
        Player owner = await NewPlayer();
        Player friend = await NewPlayer();
        await MakeFriends(owner, friend);
        await GiveGlimmer(friend, 5000);
        WalkingMonsterDto monster = await Starter(owner);

        Assert.Equal(HttpStatusCode.BadRequest, (await Offer(friend, monster.Id, 200)).StatusCode);   // still settling in
        await Settle(monster.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await Offer(friend, monster.Id, 1)).StatusCode);     // below the Common minimum
        Assert.Equal(HttpStatusCode.BadRequest, (await Offer(friend, monster.Id, 9999)).StatusCode);  // above the Common maximum

        await owner.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(monster.Id, new List<MonsterProgressDto>()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Offer(friend, monster.Id, 200)).StatusCode);   // it's their companion
    }

    [Fact]
    public async Task An_offer_holds_the_Glimmer_and_declining_gives_it_back()
    {
        Player owner = await NewPlayer();
        Player friend = await NewPlayer();
        await MakeFriends(owner, friend);
        await GiveGlimmer(friend, 1000);
        WalkingMonsterDto monster = await Starter(owner);
        await Settle(monster.Id);

        OfferDto offer = (await (await Offer(friend, monster.Id, 300)).Content.ReadFromJsonAsync<OfferDto>())!;
        Assert.Equal(700, await Balance(friend));

        List<OfferDto> incoming = (await owner.Http.GetFromJsonAsync<List<OfferDto>>("/api/walking/offers"))!;
        Assert.Contains(incoming, o => o.Id == offer.Id && o.Incoming && o.OtherUsername == friend.Name);

        Assert.Equal(HttpStatusCode.OK, (await owner.Http.PostAsync($"/api/walking/offers/{offer.Id}/decline", null)).StatusCode);
        Assert.Equal(1000, await Balance(friend));
    }

    [Fact]
    public async Task Accepting_sends_the_monster_with_its_training_to_its_new_home()
    {
        Player owner = await NewPlayer();
        Player buyer = await NewPlayer();
        Player other = await NewPlayer();
        await MakeFriends(owner, buyer);
        await MakeFriends(owner, other);
        await GiveGlimmer(buyer, 1000);
        await GiveGlimmer(other, 1000);
        await GiveGlimmer(owner, 0);

        WalkingMonsterDto monster = await Starter(owner);
        await Settle(monster.Id);
        await owner.Http.PutAsJsonAsync("/api/walking/monsters/progress",
            new ProgressRequest(null, new List<MonsterProgressDto> { new(monster.Id, 4, 30, 2, 1, 1, 0, 700) }));

        OfferDto winning = (await (await Offer(buyer, monster.Id, 400)).Content.ReadFromJsonAsync<OfferDto>())!;
        await Offer(other, monster.Id, 250);

        Assert.Equal(HttpStatusCode.OK, (await owner.Http.PostAsync($"/api/walking/offers/{winning.Id}/accept", null)).StatusCode);

        WalkingMonsterDto arrived = (await buyer.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))!.Single(m => m.Id == monster.Id);
        Assert.False(arrived.Seen);
        Assert.Equal(owner.Name, arrived.ArrivedFrom);
        Assert.Equal(4, arrived.Level);
        Assert.Equal(2, arrived.TrainedPower);

        Assert.DoesNotContain(await owner.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters") ?? new(), m => m.Id == monster.Id);
        Assert.Equal(400, await Balance(owner));
        Assert.Equal(600, await Balance(buyer));
        Assert.Equal(1000, await Balance(other));   // the other offer was turned down and refunded
    }

    [Fact]
    public async Task Unanswered_offers_expire_and_return_the_Glimmer()
    {
        Player owner = await NewPlayer();
        Player friend = await NewPlayer();
        await MakeFriends(owner, friend);
        await GiveGlimmer(friend, 1000);
        WalkingMonsterDto monster = await Starter(owner);
        await Settle(monster.Id);
        OfferDto offer = (await (await Offer(friend, monster.Id, 300)).Content.ReadFromJsonAsync<OfferDto>())!;

        await WithDb(async db => (await db.MonsterOffers.FirstAsync(o => o.Id == offer.Id)).ExpiresUtc = DateTime.UtcNow.AddMinutes(-1));

        List<OfferDto> mine = (await friend.Http.GetFromJsonAsync<List<OfferDto>>("/api/walking/offers"))!;
        Assert.Equal("Expired", mine.Single(o => o.Id == offer.Id).Status);
        Assert.Equal(1000, await Balance(friend));
    }

    [Fact]
    public async Task Training_more_than_the_potential_allows_is_not_believed()
    {
        Player p = await NewPlayer();
        WalkingMonsterDto monster = await Starter(p);

        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress",
            new ProgressRequest(null, new List<MonsterProgressDto> { new(monster.Id, 500, 0, 99, 99, 99, 99, 0) }));

        WalkingMonsterDto after = await Starter(p);
        Assert.Equal(100, after.Level);
        Assert.Equal(0, after.TrainedPower + after.TrainedGuard + after.TrainedTricks + after.TrainedWits);
    }

    [Fact]
    public async Task Training_is_one_point_a_day_by_the_servers_clock()
    {
        Player p = await NewPlayer();
        WalkingMonsterDto monster = await Starter(p);   // found today: one point at most

        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress",
            new ProgressRequest(null, new List<MonsterProgressDto> { new(monster.Id, 1, 0, 1, 0, 0, 0, 3000) }));
        Assert.Equal(1, (await Starter(p)).TrainedPower);

        // Five points on its first day (say, after moving the phone's date on): not taken - the one point stays.
        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress",
            new ProgressRequest(null, new List<MonsterProgressDto> { new(monster.Id, 1, 0, 3, 2, 0, 0, 3000) }));
        WalkingMonsterDto after = await Starter(p);
        Assert.Equal(1, after.TrainedPower + after.TrainedGuard + after.TrainedTricks + after.TrainedWits);

        // Four days on, five points are believable.
        await Settle(monster.Id);
        await p.Http.PutAsJsonAsync("/api/walking/monsters/progress",
            new ProgressRequest(null, new List<MonsterProgressDto> { new(monster.Id, 1, 0, 3, 2, 0, 0, 3000) }));
        after = await Starter(p);
        Assert.Equal(5, after.TrainedPower + after.TrainedGuard);
    }
}
