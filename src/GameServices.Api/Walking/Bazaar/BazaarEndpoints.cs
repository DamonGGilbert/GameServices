using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Bazaar;

public record CardStyleDto(string Id, string Name, int Price, bool Owned);

/// <param name="PreviousOwners">How many walkers it's been with before (it was found by the first).</param>
public record TraderStockDto(WalkingMonsterDto Monster, int Price, int PreviousOwners);

/// <param name="WhyNot">Why Juniper won't buy it (your companion, a crystal, on the Market), or null if she will.</param>
public record TraderQuoteDto(Guid MonsterId, int Price, string? WhyNot);

public record BazaarDto(int Glimmer, List<CardStyleDto> Styles, List<TraderStockDto> Stock, List<TraderQuoteDto> Quotes,
    int SellsLeft, int BuysLeft, bool HasRoom, int TonicPrice, int TonicsLeft, int MenderPrice, int MendersLeft, int MenderSteps,
    int NameTags, int NameTagPrice, string Message = "");

public record WearStyleRequest(Guid MonsterId, string StyleId);

/// <summary>
/// THE BAZAAR: the walking game's shops, all paid for with Glimmer earned by walking (never real money).
///
///   GET  /api/walking/bazaar                          everything on sale, and what I can still do today
///   POST /api/walking/bazaar/trader/{monsterId}/sell  sell one of my monsters to Juniper the Trader
///   POST /api/walking/bazaar/trader/{monsterId}/buy   buy one from her stall
///   POST /api/walking/bazaar/styles/{styleId}/buy     the Monster Maker: buy a card style
///   POST /api/walking/bazaar/styles/wear              put a style I own on one of my monsters ("" = plain)
///   POST /api/walking/bazaar/tonic                    the Tonic Stand: a Trail Tonic (the game adds it to my bag)
///   POST /api/walking/bazaar/mender/{monsterId}       a Crystal Mender: walks a crystal 3,000 steps closer to whole
///
/// Every answer is the whole Bazaar again, with a Message saying what happened. (The Market is its own place -
/// Monsters/MonsterMarket - and Name Tags are Monsters/MonsterNames; the Bazaar just shows how many you have.)
/// </summary>
public static class BazaarEndpoints
{
    public const int TonicPrice = 120;
    public const int TonicsPerDay = 3;
    public const int MenderPrice = 250;
    public const int MendersPerDay = 2;
    public const int MenderSteps = 3000;

    public static RouteGroupBuilder MapBazaarEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/bazaar", Get);
        group.MapPost("/bazaar/trader/{monsterId:guid}/sell", Sell);
        group.MapPost("/bazaar/trader/{monsterId:guid}/buy", Buy);
        group.MapPost("/bazaar/styles/{styleId}/buy", BuyStyle);
        group.MapPost("/bazaar/styles/wear", WearStyle);
        group.MapPost("/bazaar/tonic", BuyTonic);
        group.MapPost("/bazaar/mender/{monsterId:guid}", Mend);
        return group;
    }

    private static IResult Problem(string message, int status = StatusCodes.Status400BadRequest) => Results.Problem(message, statusCode: status);

    // A new UTC day starts the day's counters again.
    private static void RollDay(WalkingProfile profile)
    {
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (profile.BazaarDay == today) return;
        profile.BazaarDay = today;
        profile.TraderSellsToday = profile.TraderBuysToday = profile.TonicsToday = profile.MendersToday = 0;
    }

    private static async Task<IResult> Get(ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        await Trader.EnsureAccountAsync(db, cancel);
        await Trader.RestockAsync(db, finder, cancel);
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        RollDay(profile);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await Dto(db, finder.Catalog, me, profile, "", cancel));
    }

    private static async Task<BazaarDto> Dto(AppDbContext db, MonsterCatalog catalog, Guid me, WalkingProfile profile, string message, CancellationToken cancel)
    {
        HashSet<string> owned = CardStyles.Owned(profile.CardStyles);
        List<CardStyleDto> styles = CardStyles.All.Select(s => new CardStyleDto(s.Id, s.Name, s.Price, owned.Contains(s.Id))).ToList();

        List<WalkingMonster> stall = await db.WalkingMonsters.AsNoTracking().Where(m => m.OwnerId == Trader.Id).OrderBy(m => m.AcquiredUtc).ToListAsync(cancel);
        List<TraderStockDto> stock = stall.Select(m => new TraderStockDto(MonsterEndpoints.ToDto(m, null), Trader.SellingPrice(catalog, m), m.TradeCount)).ToList();

        List<WalkingMonster> mine = await db.WalkingMonsters.AsNoTracking().Where(m => m.OwnerId == me).OrderBy(m => m.FoundUtc).ToListAsync(cancel);
        HashSet<Guid> listed = (await db.MarketListings.Where(l => l.SellerId == me && l.Status == ListingStatus.Active).Select(l => l.MonsterId).ToListAsync(cancel)).ToHashSet();
        List<TraderQuoteDto> quotes = mine.Select(m => new TraderQuoteDto(m.Id, Trader.BuyingPrice(catalog, m), WhyNotSell(profile, m, listed))).ToList();

        return new BazaarDto(profile.Glimmer, styles, stock, quotes,
            Math.Max(0, Trader.SellsPerDay - profile.TraderSellsToday), Math.Max(0, Trader.BuysPerDay - profile.TraderBuysToday),
            await MonsterSlots.HasRoomAsync(db, me, cancel),
            TonicPrice, Math.Max(0, TonicsPerDay - profile.TonicsToday), MenderPrice, Math.Max(0, MendersPerDay - profile.MendersToday), MenderSteps,
            profile.NameTags, MonsterNames.NameTagPrice, message);
    }

    private static string? WhyNotSell(WalkingProfile profile, WalkingMonster m, HashSet<Guid> listed)
    {
        if (profile.CompanionMonsterId == m.Id) return "Your companion stays with you.";
        if (PowerBattles.IsCrystal(m)) return "It's a crystal - walk its power back first.";
        if (listed.Contains(m.Id)) return "It's on the Market.";
        return null;
    }

    private static async Task<IResult> Sell(Guid monsterId, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        await Trader.EnsureAccountAsync(db, cancel);

        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == monsterId && m.OwnerId == me, cancel);
        if (monster == null) return Results.NotFound();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        RollDay(profile);
        if (profile.TraderSellsToday >= Trader.SellsPerDay) return Problem($"Juniper buys {Trader.SellsPerDay} monsters a day from each walker - come back tomorrow!");

        HashSet<Guid> listed = (await db.MarketListings.Where(l => l.MonsterId == monster.Id && l.Status == ListingStatus.Active).Select(l => l.MonsterId).ToListAsync(cancel)).ToHashSet();
        string? why = WhyNotSell(profile, monster, listed);
        if (why != null) return Problem(why);

        int price = Trader.BuyingPrice(finder.Catalog, monster);
        DateTime now = DateTime.UtcNow;
        await MonsterHandover.Move(db, monster, Trader.Id, now, null, null, null, cancel);
        WalkingWallet.Change(db, profile, price, "trader-sell", "Sold to Juniper the Trader");
        profile.TraderSellsToday++;
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await Dto(db, finder.Catalog, me, profile, $"Juniper paid {price:N0} Glimmer. She'll look after it!", cancel));
    }

    private static async Task<IResult> Buy(Guid monsterId, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == monsterId && m.OwnerId == Trader.Id, cancel);
        if (monster == null) return Problem("Someone's just bought that one - have a look at what else Juniper has.", StatusCodes.Status409Conflict);
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        RollDay(profile);
        if (profile.TraderBuysToday >= Trader.BuysPerDay) return Problem($"Juniper sells {Trader.BuysPerDay} monsters a day to each walker - come back tomorrow!");
        if (!await MonsterSlots.HasRoomAsync(db, me, cancel)) return Problem("Your team is full - level up for another slot first.");

        int price = Trader.SellingPrice(finder.Catalog, monster);
        if (!WalkingWallet.TrySpend(db, profile, price, "trader-buy", "Bought from Juniper the Trader"))
            return Problem($"That's {price:N0} Glimmer - you have {profile.Glimmer:N0}. Walking earns more!");

        await MonsterHandover.Move(db, monster, me, DateTime.UtcNow, null, null, null, cancel);
        profile.TraderBuysToday++;
        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problem("Someone's just bought that one - have a look at what else Juniper has.", StatusCodes.Status409Conflict);
        }
        return Results.Ok(await Dto(db, finder.Catalog, me, profile, "It's yours! Say hello to your new monster.", cancel));
    }

    private static async Task<IResult> BuyStyle(string styleId, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        CardStyles.Style? style = CardStyles.Find(styleId);
        if (style == null) return Results.NotFound();

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        HashSet<string> owned = CardStyles.Owned(profile.CardStyles);
        if (owned.Contains(style.Id)) return Problem("You already have that style - put it on any of your monsters.", StatusCodes.Status409Conflict);
        if (!WalkingWallet.TrySpend(db, profile, style.Price, "style", "Card style: " + style.Name))
            return Problem($"That's {style.Price:N0} Glimmer - you have {profile.Glimmer:N0}. Walking earns more!");

        owned.Add(style.Id);
        profile.CardStyles = CardStyles.Write(owned);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await Dto(db, finder.Catalog, me, profile, style.Name + " is yours - put it on any of your monsters.", cancel));
    }

    private static async Task<IResult> WearStyle(WearStyleRequest request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (request == null) return Problem("Pick a monster and a style.");

        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == request.MonsterId && m.OwnerId == me, cancel);
        if (monster == null) return Results.NotFound();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);

        string id = request.StyleId ?? "";
        CardStyles.Style? style = CardStyles.Find(id);
        if (id.Length > 0 && style == null) return Results.NotFound();
        if (style != null && !CardStyles.Owned(profile.CardStyles).Contains(style.Id)) return Problem("Buy that style at the Monster Maker first.");

        monster.CardStyle = style?.Id ?? "";
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await Dto(db, finder.Catalog, me, profile, style == null ? "Back to its plain card." : "Looking good in " + style.Name + "!", cancel));
    }

    private static async Task<IResult> BuyTonic(ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        RollDay(profile);
        if (profile.TonicsToday >= TonicsPerDay) return Problem($"The Tonic Stand sells {TonicsPerDay} Trail Tonics a day - walking earns them too!");
        if (!WalkingWallet.TrySpend(db, profile, TonicPrice, "tonic", "Trail Tonic"))
            return Problem($"That's {TonicPrice:N0} Glimmer - you have {profile.Glimmer:N0}. Walking earns more!");

        profile.TonicsToday++;
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await Dto(db, finder.Catalog, me, profile, "A Trail Tonic - it's in your bag for your next battle.", cancel));
    }

    private static async Task<IResult> Mend(Guid monsterId, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == monsterId && m.OwnerId == me, cancel);
        if (monster == null) return Results.NotFound();
        if (!PowerBattles.IsCrystal(monster)) return Problem("That monster isn't a crystal - it doesn't need mending.");

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        RollDay(profile);
        if (profile.MendersToday >= MendersPerDay) return Problem($"The Tonic Stand sells {MendersPerDay} Crystal Menders a day - walking mends crystals too!");
        if (!WalkingWallet.TrySpend(db, profile, MenderPrice, "mender", "Crystal Mender"))
            return Problem($"That's {MenderPrice:N0} Glimmer - you have {profile.Glimmer:N0}. Walking earns more!");

        profile.MendersToday++;
        monster.CrystalStepsLeft = Math.Max(0, monster.CrystalStepsLeft - MenderSteps);
        bool whole = monster.CrystalStepsLeft == 0;
        if (whole)
        {
            monster.CrystalPower = 0;
            monster.CrystalSteps = 0;
        }

        await db.SaveChangesAsync(cancel);
        return Results.Ok(await Dto(db, finder.Catalog, me, profile,
            whole ? "Mended - all its power is back!" : $"Mended a little - {monster.CrystalStepsLeft:N0} steps still to walk.", cancel));
    }
}
