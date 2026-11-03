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
/// THE MARKET: list a monster, others bid (their Glimmer held, returned when outbid) or Buy Now; when time runs out the
/// top bid wins. The monster moves with its level and training and counts one more previous owner; the seller gets the
/// price less the 5% fee; and the safety rules hold (no companions, settled in first, price limits, no blocked walkers).
/// </summary>
public class WalkingMarketTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;

    public WalkingMarketTests(ApiFactory factory)
    {
        this.factory = factory;
    }

    private record Player(HttpClient Http, Guid Id, string Name);

    private async Task<Player> NewPlayer(int glimmer = 0)
    {
        HttpClient client = factory.CreateClient();
        string name = "m" + Guid.NewGuid().ToString("N")[..10];
        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        client.DefaultRequestHeaders.Add(GameIds.Header, GameIds.Walking);
        var player = new Player(client, auth.User.Id, name);
        if (glimmer > 0) await GiveGlimmer(player, glimmer);
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

    private Task GiveGlimmer(Player p, int amount) => WithDb(async db =>
    {
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, p.Id, CancellationToken.None);
        profile.WalletStarted = true;
        profile.Glimmer = amount;
    });

    private async Task<int> Balance(Player p) => (await p.Http.GetFromJsonAsync<WalletDto>("/api/walking/wallet"))!.Glimmer;

    // A player's starter, settled in (been with them long enough to go on the Market).
    private async Task<WalkingMonsterDto> SettledStarter(Player p)
    {
        WalkingMonsterDto starter = (await p.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0];
        await Settle(starter.Id);
        return starter;
    }

    private Task Settle(Guid monsterId) => WithDb(async db =>
    {
        WalkingMonster m = await db.WalkingMonsters.FirstAsync(x => x.Id == monsterId);
        m.AcquiredUtc = DateTime.UtcNow.AddDays(-4);
    });

    private Task<Guid> OwnerOf(Guid monsterId) => FromDb(db => db.WalkingMonsters.Where(m => m.Id == monsterId).Select(m => m.OwnerId).FirstAsync());

    private Task<ListingStatus> StatusOf(Guid listingId) => FromDb(db => db.MarketListings.Where(l => l.Id == listingId).Select(l => l.Status).FirstAsync());

    private static Task<HttpResponseMessage> List(Player p, Guid monsterId, int start, int buyNow, int hours = 24) =>
        p.Http.PostAsJsonAsync("/api/walking/market", new ListRequest(monsterId, start, buyNow, hours));

    private static async Task<MarketListingDto> ListOk(Player p, Guid monsterId, int start, int buyNow, int hours = 24)
    {
        HttpResponseMessage response = await List(p, monsterId, start, buyNow, hours);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MarketListingDto>())!;
    }

    private static Task<HttpResponseMessage> Bid(Player p, Guid listingId, int amount) =>
        p.Http.PostAsJsonAsync($"/api/walking/market/{listingId}/bid", new BidRequest(amount));

    private static Task<HttpResponseMessage> Buy(Player p, Guid listingId) => p.Http.PostAsync($"/api/walking/market/{listingId}/buy", null);

    private static async Task<string> Refused(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    // Time runs out for a listing (and the background job finishes it).
    private Task EndNow(Guid listingId) => WithDb(async db =>
    {
        MarketListing l = await db.MarketListings.FirstAsync(x => x.Id == listingId);
        l.EndsUtc = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        await WalkingLifecycleService.RunOnce(db, DateTime.UtcNow);
    });

    [Fact]
    public async Task BuyNow_MovesTheMonster_PaysTheSellerLessTheFee_AndCountsAPreviousOwner()
    {
        Player seller = await NewPlayer();
        Player buyer = await NewPlayer(glimmer: 2000);
        WalkingMonsterDto monster = await SettledStarter(seller);
        int start = MonsterOffers.MinPrice(monster.RarityTier);
        int buyNow = MonsterOffers.MaxPrice(monster.RarityTier);

        MarketListingDto listing = await ListOk(seller, monster.Id, start, buyNow);
        Assert.Equal(0, listing.PreviousOwners);
        Assert.Equal(seller.Name, listing.SellerName);
        Assert.Contains((await buyer.Http.GetFromJsonAsync<MarketPageDto>("/api/walking/market"))!.Listings, l => l.Id == listing.Id);

        Assert.Equal(HttpStatusCode.OK, (await Buy(buyer, listing.Id)).StatusCode);
        Assert.Equal(buyer.Id, await OwnerOf(monster.Id));
        Assert.Equal(2000 - buyNow, await Balance(buyer));
        Assert.Equal(buyNow - MonsterMarket.Fee(buyNow), await Balance(seller));
        Assert.Equal(ListingStatus.Sold, await StatusOf(listing.Id));

        // Its new walker sells it on later: it now has one previous owner.
        await Settle(monster.Id);
        MarketListingDto again = await ListOk(buyer, monster.Id, start, buyNow);
        Assert.Equal(1, again.PreviousOwners);
    }

    [Fact]
    public async Task Bids_AreHeld_AndAnOutbidWalkerGetsTheirGlimmerBack()
    {
        Player seller = await NewPlayer();
        Player first = await NewPlayer(glimmer: 1000);
        Player second = await NewPlayer(glimmer: 1000);
        WalkingMonsterDto monster = await SettledStarter(seller);
        int start = MonsterOffers.MinPrice(monster.RarityTier);
        MarketListingDto listing = await ListOk(seller, monster.Id, start, MonsterOffers.MaxPrice(monster.RarityTier));

        Assert.Contains("own listing", await Refused(await Bid(seller, listing.Id, start)));
        Assert.Contains("at least", await Refused(await Bid(first, listing.Id, start - 1)));
        Assert.Equal(HttpStatusCode.OK, (await Bid(first, listing.Id, start)).StatusCode);
        Assert.Equal(1000 - start, await Balance(first));
        Assert.Contains("already the top bidder", await Refused(await Bid(first, listing.Id, start + 50)));

        int least = start + MonsterMarket.Increment(start);
        Assert.Contains("at least", await Refused(await Bid(second, listing.Id, least - 1)));
        Assert.Equal(HttpStatusCode.OK, (await Bid(second, listing.Id, least)).StatusCode);
        Assert.Equal(1000, await Balance(first));            // outbid: all back
        Assert.Equal(1000 - least, await Balance(second));    // held

        MarketDetailDto detail = (await first.Http.GetFromJsonAsync<MarketDetailDto>($"/api/walking/market/{listing.Id}"))!;
        Assert.Equal(2, detail.Bids.Count);
        Assert.Equal("A walker", detail.Bids[0].Who);         // bidders stay anonymous...
        Assert.Equal("You", detail.Bids[1].Who);              // ...except you
        Assert.Equal(least, detail.Listing.CurrentBid);
    }

    [Fact]
    public async Task WhenTimeRunsOut_TheTopBidWins_OrWithNoBidsItStays()
    {
        Player seller = await NewPlayer();
        Player bidder = await NewPlayer(glimmer: 1000);
        WalkingMonsterDto monster = await SettledStarter(seller);
        int start = MonsterOffers.MinPrice(monster.RarityTier);
        int buyNow = MonsterOffers.MaxPrice(monster.RarityTier);

        // No bids: it stays with its walker.
        MarketListingDto quiet = await ListOk(seller, monster.Id, start, buyNow, hours: 1);
        await EndNow(quiet.Id);
        Assert.Equal(ListingStatus.Expired, await StatusOf(quiet.Id));
        Assert.Equal(seller.Id, await OwnerOf(monster.Id));

        // A bid: the top bidder takes it home for their bid, and the seller gets it less the fee.
        MarketListingDto listing = await ListOk(seller, monster.Id, start, buyNow, hours: 1);
        Assert.Equal(HttpStatusCode.OK, (await Bid(bidder, listing.Id, start)).StatusCode);
        await EndNow(listing.Id);
        Assert.Equal(ListingStatus.Sold, await StatusOf(listing.Id));
        Assert.Equal(bidder.Id, await OwnerOf(monster.Id));
        Assert.Equal(1000 - start, await Balance(bidder));
        Assert.Equal(start - MonsterMarket.Fee(start), await Balance(seller));

        MarketActivityDto activity = (await bidder.Http.GetFromJsonAsync<MarketActivityDto>("/api/walking/market/activity"))!;
        Assert.Contains(activity.History, l => l.Id == listing.Id && l.BoughtByMe && !l.BoughtNow);
    }

    [Fact]
    public async Task BuyNow_ReturnsTheTopBid_AndBidsStopAtTheBuyNowPrice()
    {
        Player seller = await NewPlayer();
        Player bidder = await NewPlayer(glimmer: 1000);
        Player buyer = await NewPlayer(glimmer: 1000);
        WalkingMonsterDto monster = await SettledStarter(seller);
        int start = MonsterOffers.MinPrice(monster.RarityTier);
        int buyNow = start + 100;
        MarketListingDto listing = await ListOk(seller, monster.Id, start, buyNow);

        Assert.Contains("Buy Now", await Refused(await Bid(bidder, listing.Id, buyNow)));
        Assert.Equal(HttpStatusCode.OK, (await Bid(bidder, listing.Id, start)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Buy(buyer, listing.Id)).StatusCode);

        Assert.Equal(1000, await Balance(bidder));
        Assert.Equal(1000 - buyNow, await Balance(buyer));
        Assert.Equal(buyer.Id, await OwnerOf(monster.Id));
        Assert.Contains("finished", await Refused(await Buy(bidder, listing.Id)));
    }

    [Fact]
    public async Task TheRules_KeepItFair()
    {
        Player seller = await NewPlayer();
        WalkingMonsterDto monster = (await seller.Http.GetFromJsonAsync<List<WalkingMonsterDto>>("/api/walking/monsters"))![0];
        int min = MonsterOffers.MinPrice(monster.RarityTier);
        int max = MonsterOffers.MaxPrice(monster.RarityTier);

        Assert.Contains("settling in", await Refused(await List(seller, monster.Id, min, max)));
        await Settle(monster.Id);

        await WithDb(async db => (await WalkingEndpoints.GetOrCreateProfile(db, seller.Id, CancellationToken.None)).CompanionMonsterId = monster.Id);
        Assert.Contains("companion", await Refused(await List(seller, monster.Id, min, max)));
        await WithDb(async db => (await WalkingEndpoints.GetOrCreateProfile(db, seller.Id, CancellationToken.None)).CompanionMonsterId = null);

        Assert.Contains("between", await Refused(await List(seller, monster.Id, min - 1, max)));
        Assert.Contains("between", await Refused(await List(seller, monster.Id, min, max + 1)));
        Assert.Contains("between", await Refused(await List(seller, monster.Id, min + 20, min + 10)));
        Assert.Contains("how long", await Refused(await List(seller, monster.Id, min, max, hours: 5)));

        MarketListingDto listing = await ListOk(seller, monster.Id, min, max);
        Assert.Contains("already", await Refused(await List(seller, monster.Id, min, max)));

        List<SellableDto> sellable = (await seller.Http.GetFromJsonAsync<List<SellableDto>>("/api/walking/market/sellable"))!;
        Assert.False(sellable.Single(s => s.Monster.Id == monster.Id).CanList);

        // It can be taken off the Market while nobody has bid; once someone has, it stays.
        Assert.Equal(HttpStatusCode.OK, (await seller.Http.PostAsync($"/api/walking/market/{listing.Id}/cancel", null)).StatusCode);
        Assert.Equal(ListingStatus.Cancelled, await StatusOf(listing.Id));
        MarketListingDto relisted = await ListOk(seller, monster.Id, min, max);
        Player bidder = await NewPlayer(glimmer: 1000);
        Assert.Equal(HttpStatusCode.OK, (await Bid(bidder, relisted.Id, min)).StatusCode);
        Assert.Contains("stays", await Refused(await seller.Http.PostAsync($"/api/walking/market/{relisted.Id}/cancel", null)));
    }

    [Fact]
    public async Task ALastMinuteBid_AddsAMinute()
    {
        Player seller = await NewPlayer();
        Player bidder = await NewPlayer(glimmer: 1000);
        WalkingMonsterDto monster = await SettledStarter(seller);
        int start = MonsterOffers.MinPrice(monster.RarityTier);
        MarketListingDto listing = await ListOk(seller, monster.Id, start, MonsterOffers.MaxPrice(monster.RarityTier), hours: 1);
        await WithDb(async db => (await db.MarketListings.FirstAsync(l => l.Id == listing.Id)).EndsUtc = DateTime.UtcNow.AddSeconds(10));

        Assert.Equal(HttpStatusCode.OK, (await Bid(bidder, listing.Id, start)).StatusCode);
        DateTime ends = await FromDb(db => db.MarketListings.Where(l => l.Id == listing.Id).Select(l => l.EndsUtc).FirstAsync());
        Assert.True(ends > DateTime.UtcNow.AddSeconds(50));
    }

    [Fact]
    public async Task BlockedWalkers_CantSeeOrBuyEachOthersListings()
    {
        Player seller = await NewPlayer();
        Player buyer = await NewPlayer(glimmer: 1000);
        WalkingMonsterDto monster = await SettledStarter(seller);
        MarketListingDto listing = await ListOk(seller, monster.Id, MonsterOffers.MinPrice(monster.RarityTier), MonsterOffers.MaxPrice(monster.RarityTier));

        await WithDb(db =>
        {
            db.Blocks.Add(new UserBlock { BlockerId = seller.Id, BlockedId = buyer.Id });
            return Task.CompletedTask;
        });

        Assert.DoesNotContain((await buyer.Http.GetFromJsonAsync<MarketPageDto>("/api/walking/market"))!.Listings, l => l.Id == listing.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await Buy(buyer, listing.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await buyer.Http.GetAsync($"/api/walking/market/{listing.Id}")).StatusCode);
    }

    [Fact]
    public async Task ChoosingItAsYourCompanion_TakesItOffTheMarket_AndReturnsTheTopBid()
    {
        Player seller = await NewPlayer();
        Player bidder = await NewPlayer(glimmer: 1000);
        WalkingMonsterDto monster = await SettledStarter(seller);
        int start = MonsterOffers.MinPrice(monster.RarityTier);
        MarketListingDto listing = await ListOk(seller, monster.Id, start, MonsterOffers.MaxPrice(monster.RarityTier));
        Assert.Equal(HttpStatusCode.OK, (await Bid(bidder, listing.Id, start)).StatusCode);

        HttpResponseMessage saved = await seller.Http.PutAsJsonAsync("/api/walking/monsters/progress", new ProgressRequest(monster.Id, new List<MonsterProgressDto>()));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        Assert.Equal(ListingStatus.Closed, await StatusOf(listing.Id));
        Assert.Equal(1000, await Balance(bidder));
        Assert.Equal(seller.Id, await OwnerOf(monster.Id));
    }

    [Fact]
    public async Task Sales_MakeThePriceHistory_AndTheTicker()
    {
        Player seller = await NewPlayer(glimmer: 5000);
        Player buyer = await NewPlayer(glimmer: 5000);
        WalkingMonsterDto monster = await SettledStarter(seller);
        int min = MonsterOffers.MinPrice(monster.RarityTier);
        int max = MonsterOffers.MaxPrice(monster.RarityTier);

        // Sold once a couple of days ago for the minimum, then again today for double.
        MarketListingDto first = await ListOk(seller, monster.Id, min, min);
        Assert.Equal(HttpStatusCode.OK, (await Buy(buyer, first.Id)).StatusCode);
        await WithDb(async db => (await db.MarketListings.FirstAsync(l => l.Id == first.Id)).ClosedUtc = DateTime.UtcNow.AddDays(-2));
        await Settle(monster.Id);
        MarketListingDto second = await ListOk(buyer, monster.Id, min * 2, min * 2);
        Assert.Equal(HttpStatusCode.OK, (await Buy(seller, second.Id)).StatusCode);

        await Settle(monster.Id);
        MarketListingDto third = await ListOk(seller, monster.Id, min, max);
        MarketDetailDto detail = (await buyer.Http.GetFromJsonAsync<MarketDetailDto>($"/api/walking/market/{third.Id}"))!;
        Assert.True(detail.History.Count >= 2);
        Assert.Equal(min * 2, detail.LastSale);
        Assert.Equal(2, third.PreviousOwners);

        MarketPageDto page = (await buyer.Http.GetFromJsonAsync<MarketPageDto>("/api/walking/market"))!;
        Assert.Contains(page.Ticker, t => t.SpeciesId == monster.SpeciesId && t.RarityTier == monster.RarityTier && t.LastPrice > 0);
    }
}
