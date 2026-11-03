using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Monsters;

public record ListRequest(Guid MonsterId, int StartPrice, int BuyNowPrice, int Hours);

public record BidRequest(int Amount);

/// <summary>One listing as the Market shows it. Change: how the price of this kind of monster (species + rarity) has moved
/// over the last day, in percent. Spark: its recent sale prices, oldest first (for the little chart).</summary>
public record MarketListingDto(Guid Id, WalkingMonsterDto Monster, Guid SellerId, string SellerName, int PreviousOwners,
    int StartPrice, int BuyNowPrice, int CurrentBid, int BidCount, int MinNextBid, bool IsMine, bool TopBidderIsMe, bool IBid,
    string Status, DateTime CreatedUtc, DateTime EndsUtc, DateTime? ClosedUtc, int SoldPrice, bool BoughtNow, bool BoughtByMe,
    int LastSale, double Change, List<int> Spark);

/// <summary>A kind of monster (species + rarity) that's been selling: for the scrolling ticker.</summary>
public record TickerDto(string SpeciesId, int RarityTier, int LastPrice, double Change, int Sales);

public record MarketPageDto(List<MarketListingDto> Listings, List<TickerDto> Ticker, int Glimmer);

public record PricePointDto(int Price, DateTime Utc);

/// <summary>Bidders stay anonymous ("A walker") - except you.</summary>
public record MarketBidDto(string Who, int Amount, DateTime Utc, bool Mine);

public record MarketDetailDto(MarketListingDto Listing, List<PricePointDto> History, List<MarketBidDto> Bids,
    int Lowest, int LastSale, double Change, int SalesThisWeek, int Listed, int FeePercent, int Glimmer);

public record SellableDto(WalkingMonsterDto Monster, bool CanList, string Reason, int MinPrice, int MaxPrice, int Suggested);

public record MarketActivityDto(List<MarketListingDto> Selling, List<MarketListingDto> Bidding, List<MarketListingDto> History, int Glimmer);

/// <summary>
/// THE MARKET: monsters for Glimmer, between any walkers - list one with a starting price and a Buy Now price, then
/// others bid until time runs out, or buy it straight away.
///
///   GET  /api/walking/market                 what's for sale (filter by species / rarity, sort) and the price ticker
///   GET  /api/walking/market/{id}            one listing: its price history, bids, and how its kind is selling
///   GET  /api/walking/market/sellable        my monsters, and whether each can go on the Market
///   GET  /api/walking/market/activity        what I'm selling, what I've bid on, and what's finished
///   POST /api/walking/market                 list a monster
///   POST /api/walking/market/{id}/bid | buy | cancel
///
/// Glimmer only - never real money - and the same safety as offers between friends:
///   * a companion can't be listed (choosing it as your companion takes it off the Market)
///   * a monster must have been with its walker for 3 days first
///   * prices stay within the limits for its rarity (no "1 Glimmer" tricks)
///   * at most 5 listings each, and top bid on at most 10 at once
///   * no bidding on your own, and nothing between walkers who've blocked each other
/// The top bidder's Glimmer is held: outbid, and it comes straight back. A bid in the last minute adds a minute, so
/// nobody can snatch it at the final second. When time runs out the top bid wins; with no bids it stays with its walker.
/// The Market keeps 5% of each sale (Glimmer leaves the game, which keeps prices steady). The monster - level, training
/// and all - moves to its new walker (MonsterHandover), and adds one to its count of previous owners.
/// </summary>
public static class MonsterMarket
{
    public const int MaxListings = 5;
    public const int MaxTopBids = 10;
    public const int FeePercent = 5;
    public static readonly int[] Durations = { 1, 6, 24, 72 };
    public static readonly TimeSpan SnipeGuard = TimeSpan.FromMinutes(1);
    private const int PageSize = 40;

    public static RouteGroupBuilder MapMonsterMarketEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/market", Browse);
        group.MapGet("/market/sellable", Sellable);
        group.MapGet("/market/activity", Activity);
        group.MapGet("/market/{listingId:guid}", Detail);
        group.MapPost("/market", List);
        group.MapPost("/market/{listingId:guid}/bid", Bid);
        group.MapPost("/market/{listingId:guid}/buy", Buy);
        group.MapPost("/market/{listingId:guid}/cancel", Cancel);
        return group;
    }

    /// <summary>The smallest bid that beats the current one: the starting price, then at least 5% more (in tens).</summary>
    public static int MinNextBid(MarketListing l) => l.CurrentBid == 0 ? l.StartPrice : l.CurrentBid + Increment(l.CurrentBid);

    public static int Increment(int bid) => Math.Max(10, (int)Math.Ceiling(bid * 0.05 / 10.0) * 10);

    public static int Fee(int price) => price * FeePercent / 100;

    // ------------------------------------------------------------------ looking

    private static async Task<IResult> Browse(string? species, int? rarity, string? sort, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await SettleDue(db, now, cancel);

        List<Guid> blocked = await BlockedEitherWay(db, me, cancel);
        IQueryable<MarketListing> query = db.MarketListings.AsNoTracking().Where(l => l.Status == ListingStatus.Active && !blocked.Contains(l.SellerId));
        if (!string.IsNullOrEmpty(species)) query = query.Where(l => l.SpeciesId == species);
        if (rarity is int tier && tier >= 0) query = query.Where(l => l.RarityTier == tier);

        // The price to beat: the top bid, or the starting price before anyone's bid.
        query = sort switch
        {
            "new" => query.OrderByDescending(l => l.CreatedUtc),
            "low" => query.OrderBy(l => l.CurrentBid > 0 ? l.CurrentBid : l.StartPrice),
            "high" => query.OrderByDescending(l => l.CurrentBid > 0 ? l.CurrentBid : l.StartPrice),
            _ => query.OrderBy(l => l.EndsUtc),
        };

        List<MarketListing> listings = await query.Take(PageSize).ToListAsync(cancel);
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(new MarketPageDto(await ToDtos(db, listings, me, now, cancel), await Ticker(db, now, cancel), profile.Glimmer));
    }

    private static async Task<IResult> Detail(Guid listingId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await SettleDue(db, now, cancel);

        MarketListing? listing = await db.MarketListings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listingId, cancel);
        if (listing == null || await SocialEndpoints.IsBlockedEitherWay(db, me, listing.SellerId, cancel)) return Results.NotFound();

        List<MarketListing> sales = await Sales(db, listing.SpeciesId, listing.RarityTier, now.AddDays(-30), cancel);
        PriceStats stats = Stats(sales, now);
        List<MarketBid> bids = await db.MarketBids.AsNoTracking().Where(b => b.ListingId == listingId)
            .OrderByDescending(b => b.Amount).Take(10).ToListAsync(cancel);
        int lowest = await db.MarketListings.Where(l => l.Status == ListingStatus.Active && l.SpeciesId == listing.SpeciesId && l.RarityTier == listing.RarityTier)
            .Select(l => (int?)l.BuyNowPrice).MinAsync(cancel) ?? 0;
        int listed = await db.MarketListings.CountAsync(l => l.Status == ListingStatus.Active && l.SpeciesId == listing.SpeciesId && l.RarityTier == listing.RarityTier, cancel);
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        await db.SaveChangesAsync(cancel);

        return Results.Ok(new MarketDetailDto(
            (await ToDtos(db, new List<MarketListing> { listing }, me, now, cancel))[0],
            sales.TakeLast(30).Select(s => new PricePointDto(s.SoldPrice, s.ClosedUtc ?? s.EndsUtc)).ToList(),
            bids.Select(b => new MarketBidDto(b.BidderId == me ? "You" : "A walker", b.Amount, b.CreatedUtc, b.BidderId == me)).ToList(),
            lowest, stats.LastSale, stats.Change, sales.Count(s => (s.ClosedUtc ?? s.EndsUtc) > now.AddDays(-7)), listed, FeePercent, profile.Glimmer));
    }

    private static async Task<IResult> Sellable(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await SettleDue(db, now, cancel);

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        List<WalkingMonster> monsters = await db.WalkingMonsters.AsNoTracking().Where(m => m.OwnerId == me).OrderBy(m => m.FoundUtc).ToListAsync(cancel);
        HashSet<Guid> listed = (await db.MarketListings.Where(l => l.SellerId == me && l.Status == ListingStatus.Active).Select(l => l.MonsterId).ToListAsync(cancel)).ToHashSet();

        var result = new List<SellableDto>();
        foreach (WalkingMonster m in monsters)
        {
            string reason = WhyNot(m, profile, listed.Contains(m.Id), now);
            List<MarketListing> sales = await Sales(db, m.SpeciesId, m.RarityTier, now.AddDays(-30), cancel);
            int min = MonsterOffers.MinPrice(m.RarityTier), max = MonsterOffers.MaxPrice(m.RarityTier);
            int suggested = Math.Clamp(sales.Count > 0 ? sales[^1].SoldPrice : min * 2, min, max);
            result.Add(new SellableDto(MonsterEndpoints.ToDto(m, null), reason == "", reason, min, max, suggested));
        }

        await db.SaveChangesAsync(cancel);
        return Results.Ok(result);
    }

    private static async Task<IResult> Activity(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await SettleDue(db, now, cancel);

        List<Guid> bidOn = await db.MarketBids.Where(b => b.BidderId == me).Select(b => b.ListingId).Distinct().ToListAsync(cancel);
        List<MarketListing> selling = await db.MarketListings.AsNoTracking()
            .Where(l => l.SellerId == me && l.Status == ListingStatus.Active).OrderBy(l => l.EndsUtc).ToListAsync(cancel);
        List<MarketListing> bidding = await db.MarketListings.AsNoTracking()
            .Where(l => bidOn.Contains(l.Id) && l.Status == ListingStatus.Active).OrderBy(l => l.EndsUtc).ToListAsync(cancel);
        DateTime recent = now.AddDays(-14);
        List<MarketListing> history = await db.MarketListings.AsNoTracking()
            .Where(l => l.Status != ListingStatus.Active && l.ClosedUtc > recent && (l.SellerId == me || l.BuyerId == me || bidOn.Contains(l.Id)))
            .OrderByDescending(l => l.ClosedUtc).Take(30).ToListAsync(cancel);

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(new MarketActivityDto(await ToDtos(db, selling, me, now, cancel), await ToDtos(db, bidding, me, now, cancel),
            await ToDtos(db, history, me, now, cancel), profile.Glimmer));
    }

    // ------------------------------------------------------------------ doing

    private static async Task<IResult> List(ListRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await SettleDue(db, now, cancel);

        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == request.MonsterId && m.OwnerId == me, cancel);
        if (monster == null) return Problem("That monster isn't with you.");

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        bool listed = await db.MarketListings.AnyAsync(l => l.MonsterId == monster.Id && l.Status == ListingStatus.Active, cancel);
        string reason = WhyNot(monster, profile, listed, now);
        if (reason != "") return Problem(reason);

        int min = MonsterOffers.MinPrice(monster.RarityTier), max = MonsterOffers.MaxPrice(monster.RarityTier);
        if (request.StartPrice < min || request.BuyNowPrice > max || request.StartPrice > request.BuyNowPrice)
        {
            return Problem($"For this rarity, prices are between {min:N0} and {max:N0} Glimmer, and Buy Now can't be less than the starting price.");
        }

        if (!Durations.Contains(request.Hours)) return Problem("Choose how long it's on the Market: 1 hour, 6 hours, 1 day or 3 days.");
        if (await db.MarketListings.CountAsync(l => l.SellerId == me && l.Status == ListingStatus.Active, cancel) >= MaxListings)
        {
            return Problem($"You already have {MaxListings} monsters on the Market.");
        }

        var listing = new MarketListing
        {
            MonsterId = monster.Id, SellerId = me, SpeciesId = monster.SpeciesId, RarityTier = monster.RarityTier,
            StartPrice = request.StartPrice, BuyNowPrice = request.BuyNowPrice, CreatedUtc = now, EndsUtc = now.AddHours(request.Hours),
        };
        db.MarketListings.Add(listing);
        await db.SaveChangesAsync(cancel);
        return Results.Ok((await ToDtos(db, new List<MarketListing> { listing }, me, now, cancel))[0]);
    }

    private static async Task<IResult> Bid(Guid listingId, BidRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await SettleDue(db, now, cancel);

        MarketListing? listing = await db.MarketListings.FirstOrDefaultAsync(l => l.Id == listingId, cancel);
        string? problem = await CanTrade(db, listing, me, cancel);
        if (problem != null) return problem == "" ? Results.NotFound() : Problem(problem);

        if (listing!.TopBidderId == me) return Problem("You're already the top bidder.");
        int least = MinNextBid(listing);
        if (least >= listing.BuyNowPrice) return Problem("Bidding has reached the Buy Now price - Buy Now to take it home.");
        if (request.Amount < least) return Problem($"The next bid has to be at least {least:N0} Glimmer.");
        if (request.Amount >= listing.BuyNowPrice) return Problem($"That's the Buy Now price or more - Buy Now for {listing.BuyNowPrice:N0} instead.");
        if (await db.MarketListings.CountAsync(l => l.TopBidderId == me && l.Status == ListingStatus.Active, cancel) >= MaxTopBids)
        {
            return Problem($"You're the top bidder on {MaxTopBids} monsters already - wait for some to finish.");
        }

        WalkingProfile bidder = await Profile(db, me, cancel);
        if (!WalkingWallet.TrySpend(db, bidder, request.Amount, "market-bid", "Held for a Market bid"))
        {
            return Problem($"You need {request.Amount:N0} Glimmer for that bid (you have {bidder.Glimmer:N0}).");
        }

        await ReturnTopBid(db, listing, "Outbid on the Market - Glimmer returned", cancel);
        listing.CurrentBid = request.Amount;
        listing.TopBidderId = me;
        listing.BidCount++;
        if (listing.EndsUtc - now < SnipeGuard) listing.EndsUtc = now + SnipeGuard;   // no snatching it at the last second
        db.MarketBids.Add(new MarketBid { ListingId = listing.Id, BidderId = me, Amount = request.Amount, CreatedUtc = now });

        await db.SaveChangesAsync(cancel);
        return Results.Ok((await ToDtos(db, new List<MarketListing> { listing }, me, now, cancel))[0]);
    }

    private static async Task<IResult> Buy(Guid listingId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await SettleDue(db, now, cancel);

        MarketListing? listing = await db.MarketListings.FirstOrDefaultAsync(l => l.Id == listingId, cancel);
        string? problem = await CanTrade(db, listing, me, cancel);
        if (problem != null) return problem == "" ? Results.NotFound() : Problem(problem);

        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == listing!.MonsterId, cancel);
        WalkingProfile seller = await Profile(db, listing!.SellerId, cancel);
        if (monster == null || monster.OwnerId != listing.SellerId || seller.CompanionMonsterId == monster.Id)
        {
            await Close(db, listing, ListingStatus.Closed, now, cancel);
            await db.SaveChangesAsync(cancel);
            return Problem("That monster isn't for sale any more.");
        }

        WalkingProfile buyer = await Profile(db, me, cancel);
        if (!WalkingWallet.TrySpend(db, buyer, listing.BuyNowPrice, "market-buy", "Bought on the Market"))
        {
            return Problem($"You need {listing.BuyNowPrice:N0} Glimmer to buy it (you have {buyer.Glimmer:N0}).");
        }

        await ReturnTopBid(db, listing, "Someone used Buy Now - Glimmer returned", cancel);
        await Sell(db, listing, monster, seller, me, listing.BuyNowPrice, true, now, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok((await ToDtos(db, new List<MarketListing> { listing }, me, now, cancel))[0]);
    }

    private static async Task<IResult> Cancel(Guid listingId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await SettleDue(db, now, cancel);

        MarketListing? listing = await db.MarketListings.FirstOrDefaultAsync(l => l.Id == listingId && l.SellerId == me, cancel);
        if (listing == null) return Results.NotFound();
        if (listing.Status != ListingStatus.Active) return Problem("That listing has already finished.");
        if (listing.BidCount > 0) return Problem("Someone has bid on it, so it stays until time runs out.");

        await Close(db, listing, ListingStatus.Cancelled, now, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok((await ToDtos(db, new List<MarketListing> { listing }, me, now, cancel))[0]);
    }

    // ------------------------------------------------------------------ finishing

    /// <summary>
    /// Listings whose time is up: the top bid wins (its Glimmer is already held), or with no bids the monster stays.
    /// Run by every Market request and by the walking background job (WalkingLifecycleService) once a minute.
    /// </summary>
    public static async Task<int> SettleDue(AppDbContext db, DateTime now, CancellationToken cancel = default)
    {
        List<MarketListing> due = await db.MarketListings.Where(l => l.Status == ListingStatus.Active && l.EndsUtc <= now).ToListAsync(cancel);
        foreach (MarketListing listing in due)
        {
            if (listing.TopBidderId is not Guid winner)
            {
                listing.Status = ListingStatus.Expired;
                listing.ClosedUtc = now;
                continue;
            }

            WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == listing.MonsterId, cancel);
            WalkingProfile seller = await Profile(db, listing.SellerId, cancel);
            if (monster == null || monster.OwnerId != listing.SellerId || seller.CompanionMonsterId == monster.Id)
            {
                await Close(db, listing, ListingStatus.Closed, now, cancel);
                continue;
            }

            await Sell(db, listing, monster, seller, winner, listing.CurrentBid, false, now, cancel);
        }

        if (due.Count > 0) await db.SaveChangesAsync(cancel);
        return due.Count;
    }

    /// <summary>A listing ends without a sale (cancelled, or its monster left another way): the top bid goes back.</summary>
    internal static async Task Close(AppDbContext db, MarketListing listing, ListingStatus status, DateTime now, CancellationToken cancel)
    {
        await ReturnTopBid(db, listing, "Market listing closed - Glimmer returned", cancel);
        listing.Status = status;
        listing.ClosedUtc = now;
    }

    private static async Task Sell(AppDbContext db, MarketListing listing, WalkingMonster monster, WalkingProfile seller, Guid buyer, int price,
        bool boughtNow, DateTime now, CancellationToken cancel)
    {
        listing.Status = ListingStatus.Sold;
        listing.ClosedUtc = now;
        listing.BuyerId = buyer;
        listing.SoldPrice = price;
        listing.BoughtNow = boughtNow;
        WalkingWallet.Change(db, seller, price - Fee(price), "market-sold", $"Sold on the Market ({FeePercent}% Market fee)");
        await MonsterHandover.Move(db, monster, buyer, now, null, null, listing.Id, cancel);
    }

    private static async Task ReturnTopBid(AppDbContext db, MarketListing listing, string detail, CancellationToken cancel)
    {
        if (listing.TopBidderId is not Guid top || listing.CurrentBid <= 0) return;
        WalkingWallet.Change(db, await Profile(db, top, cancel), listing.CurrentBid, "market-returned", detail);
        listing.TopBidderId = null;
    }

    // ------------------------------------------------------------------ rules and helpers

    // "" = it can be listed; otherwise why not, in words for the walker.
    private static string WhyNot(WalkingMonster m, WalkingProfile owner, bool alreadyListed, DateTime now)
    {
        if (alreadyListed) return "It's on the Market already.";
        if (owner.CompanionMonsterId == m.Id) return "Your companion walks with you - it can't go on the Market.";
        if (now < m.AcquiredUtc + MonsterOffers.SettleIn) return "Still settling in - it can go on the Market from " + (m.AcquiredUtc + MonsterOffers.SettleIn).ToString("d MMM") + ".";
        return "";
    }

    // null = fine; "" = not found (hidden); otherwise the reason.
    private static async Task<string?> CanTrade(AppDbContext db, MarketListing? listing, Guid me, CancellationToken cancel)
    {
        if (listing == null || await SocialEndpoints.IsBlockedEitherWay(db, me, listing.SellerId, cancel)) return "";
        if (listing.Status != ListingStatus.Active) return "That listing has finished.";
        if (listing.SellerId == me) return "That's your own listing.";
        return null;
    }

    // Profiles already loaded in this request are reused (so two changes to one walker's Glimmer add up).
    private static async Task<WalkingProfile> Profile(AppDbContext db, Guid userId, CancellationToken cancel) =>
        db.WalkingProfiles.Local.FirstOrDefault(p => p.UserId == userId) ?? await WalkingEndpoints.GetOrCreateProfile(db, userId, cancel);

    private static async Task<List<Guid>> BlockedEitherWay(AppDbContext db, Guid me, CancellationToken cancel) =>
        await db.Blocks.Where(b => b.BlockerId == me || b.BlockedId == me).Select(b => b.BlockerId == me ? b.BlockedId : b.BlockerId).ToListAsync(cancel);

    // Sales of one kind of monster (species + rarity) since a time, oldest first.
    private static Task<List<MarketListing>> Sales(AppDbContext db, string species, int tier, DateTime since, CancellationToken cancel) =>
        db.MarketListings.AsNoTracking()
            .Where(l => l.Status == ListingStatus.Sold && l.SpeciesId == species && l.RarityTier == tier && l.ClosedUtc > since)
            .OrderBy(l => l.ClosedUtc).ToListAsync(cancel);

    private record PriceStats(int LastSale, double Change, List<int> Spark);

    /// <summary>
    /// How a kind of monster is selling, from its sales (oldest first): the last price, and how it's moved since a day
    /// ago - the last sale compared with the most recent one at least a day old (or the first, if they're all newer).
    /// </summary>
    private static PriceStats Stats(List<MarketListing> sales, DateTime now)
    {
        if (sales.Count == 0) return new PriceStats(0, 0, new List<int>());
        int last = sales[^1].SoldPrice;
        MarketListing? before = sales.LastOrDefault(s => (s.ClosedUtc ?? s.EndsUtc) <= now.AddDays(-1)) ?? (sales.Count > 1 ? sales[0] : null);
        double change = before == null || before.SoldPrice == 0 ? 0 : Math.Round((last - before.SoldPrice) * 100.0 / before.SoldPrice, 1);
        return new PriceStats(last, change, sales.TakeLast(12).Select(s => s.SoldPrice).ToList());
    }

    private static async Task<List<TickerDto>> Ticker(AppDbContext db, DateTime now, CancellationToken cancel)
    {
        List<MarketListing> week = await db.MarketListings.AsNoTracking()
            .Where(l => l.Status == ListingStatus.Sold && l.ClosedUtc > now.AddDays(-7))
            .OrderBy(l => l.ClosedUtc).Take(2000).ToListAsync(cancel);

        return week.GroupBy(l => (l.SpeciesId, l.RarityTier))
            .Select(g => (Kind: g.Key, Sales: g.ToList()))
            .OrderByDescending(g => g.Sales.Count).Take(10)
            .Select(g =>
            {
                PriceStats s = Stats(g.Sales, now);
                return new TickerDto(g.Kind.SpeciesId, g.Kind.RarityTier, s.LastSale, s.Change, g.Sales.Count);
            })
            .ToList();
    }

    private static async Task<List<MarketListingDto>> ToDtos(AppDbContext db, List<MarketListing> listings, Guid me, DateTime now, CancellationToken cancel)
    {
        if (listings.Count == 0) return new List<MarketListingDto>();

        List<Guid> monsterIds = listings.Select(l => l.MonsterId).Distinct().ToList();
        Dictionary<Guid, WalkingMonster> monsters = await db.WalkingMonsters.AsNoTracking().Where(m => monsterIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancel);
        List<Guid> sellerIds = listings.Select(l => l.SellerId).Distinct().ToList();
        Dictionary<Guid, string> names = await db.Users.Where(u => sellerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancel);
        List<Guid> ids = listings.Select(l => l.Id).ToList();
        HashSet<Guid> iBid = (await db.MarketBids.Where(b => b.BidderId == me && ids.Contains(b.ListingId)).Select(b => b.ListingId).ToListAsync(cancel)).ToHashSet();

        // Price history for every kind of monster on this page, in one go.
        List<string> species = listings.Select(l => l.SpeciesId).Distinct().ToList();
        DateTime since = now.AddDays(-30);
        List<MarketListing> sold = await db.MarketListings.AsNoTracking()
            .Where(l => l.Status == ListingStatus.Sold && species.Contains(l.SpeciesId) && l.ClosedUtc > since)
            .OrderBy(l => l.ClosedUtc).Take(3000).ToListAsync(cancel);
        Dictionary<(string, int), PriceStats> stats = sold.GroupBy(l => (l.SpeciesId, l.RarityTier)).ToDictionary(g => g.Key, g => Stats(g.ToList(), now));

        var result = new List<MarketListingDto>();
        foreach (MarketListing l in listings)
        {
            // A monster that's moved on is shown as it was when it sold: it's in the new walker's collection now.
            monsters.TryGetValue(l.MonsterId, out WalkingMonster? m);
            PriceStats s = stats.TryGetValue((l.SpeciesId, l.RarityTier), out PriceStats? found) ? found : new PriceStats(0, 0, new List<int>());
            int previousOwners = m == null ? 0 : l.Status == ListingStatus.Sold ? Math.Max(0, m.TradeCount - 1) : m.TradeCount;
            WalkingMonsterDto monster = m != null
                ? MonsterEndpoints.ToDto(m, null)
                : new WalkingMonsterDto(l.MonsterId, l.SpeciesId, 0, l.RarityTier, 0, 1, 0, "", null, l.CreatedUtc, true, 1, 0, 0, 0, 0, 0, 0, null);

            result.Add(new MarketListingDto(l.Id, monster, l.SellerId, names.GetValueOrDefault(l.SellerId, "A walker"), previousOwners,
                l.StartPrice, l.BuyNowPrice, l.CurrentBid, l.BidCount, MinNextBid(l), l.SellerId == me, l.TopBidderId == me, iBid.Contains(l.Id),
                l.Status.ToString(), l.CreatedUtc, l.EndsUtc, l.ClosedUtc, l.SoldPrice, l.BoughtNow, l.BuyerId == me,
                s.LastSale, s.Change, s.Spark));
        }

        return result;
    }

    private static IResult Problem(string message) => Results.Problem(message, statusCode: StatusCodes.Status400BadRequest);
}
