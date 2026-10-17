using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Monsters;

public record FriendMonsterDto(WalkingMonsterDto Monster, bool IsCompanion, DateTime OfferableFromUtc, int MinPrice, int MaxPrice);

public record OfferRequest(Guid MonsterId, int Price);

public record OfferDto(Guid Id, Guid MonsterId, string SpeciesId, int RarityTier, long Seed, int Serial, int Price, string Status,
    bool Incoming, Guid OtherUserId, string OtherUsername, DateTime CreatedUtc, DateTime ExpiresUtc);

/// <summary>
/// OFFERS: asking a friend if you may give their monster a new home, for Glimmer.
///
///   GET  /api/walking/friends/{friendId}/monsters   a friend's monsters (and what each could be offered for)
///   POST /api/walking/offers                        make an offer (your Glimmer is held until it's answered)
///   GET  /api/walking/offers                        offers to me and from me
///   POST /api/walking/offers/{id}/accept | decline | cancel
///
/// Nothing is ever taken: the owner decides. The rules keep it friendly and safe:
///   * friends only (never strangers, never anyone blocked) and Glimmer only - never real money
///   * a companion can't be offered for - it's the monster you walk with
///   * a monster must have been with its owner for 3 days first (no quick flipping)
///   * prices are kept within limits for its rarity (no "1 Glimmer" tricks, no funnelling Glimmer to a friend)
///   * at most 3 trades a week each, 5 open offers each, and offers expire after 2 days (Glimmer returned)
/// When an offer is accepted the monster - with its level and training - moves to its new walker, the seller gets the
/// Glimmer, and any other offers for it are turned down and refunded.
/// </summary>
public static class MonsterOffers
{
    public static readonly TimeSpan SettleIn = TimeSpan.FromDays(3);
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromDays(2);
    public const int MaxTradesPerWeek = 3;
    public const int MaxOpenOffers = 5;

    // Price limits by rarity tier: Common, Uncommon, Rare, Super Rare, Ultra Rare, Mythic, Legendary.
    private static readonly int[] MinPrices = { 50, 100, 200, 500, 1000, 3000, 5000 };
    private static readonly int[] MaxPrices = { 500, 1000, 2500, 6000, 15000, 40000, 100000 };

    public static int MinPrice(int tier) => MinPrices[Math.Clamp(tier, 0, 6)];
    public static int MaxPrice(int tier) => MaxPrices[Math.Clamp(tier, 0, 6)];

    public static RouteGroupBuilder MapMonsterOfferEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/friends/{friendId:guid}/monsters", FriendMonsters);
        group.MapPost("/offers", MakeOffer);
        group.MapGet("/offers", MyOffers);
        group.MapPost("/offers/{offerId:guid}/accept", Accept);
        group.MapPost("/offers/{offerId:guid}/decline", (Guid offerId, ClaimsPrincipal p, AppDbContext db, CancellationToken c) => Close(offerId, OfferStatus.Declined, p, db, c));
        group.MapPost("/offers/{offerId:guid}/cancel", (Guid offerId, ClaimsPrincipal p, AppDbContext db, CancellationToken c) => Close(offerId, OfferStatus.Cancelled, p, db, c));
        return group;
    }

    private static async Task<IResult> FriendMonsters(Guid friendId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!await SocialEndpoints.AreFriends(db, me, friendId, cancel) || await SocialEndpoints.IsBlockedEitherWay(db, me, friendId, cancel))
        {
            return Results.NotFound();
        }

        Guid? companion = (await db.WalkingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == friendId, cancel))?.CompanionMonsterId;
        List<WalkingMonster> monsters = await db.WalkingMonsters.AsNoTracking().Where(m => m.OwnerId == friendId).OrderBy(m => m.FoundUtc).ToListAsync(cancel);

        return Results.Ok(monsters.Select(m => new FriendMonsterDto(MonsterEndpoints.ToDto(m, null), m.Id == companion, m.AcquiredUtc + SettleIn,
            MinPrice(m.RarityTier), MaxPrice(m.RarityTier))).ToList());
    }

    private static async Task<IResult> MakeOffer(OfferRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await ExpireOld(db, now, cancel);

        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == request.MonsterId, cancel);
        if (monster == null || monster.OwnerId == me) return Problem("That monster can't be offered for.");

        Guid owner = monster.OwnerId;
        if (!await SocialEndpoints.AreFriends(db, me, owner, cancel) || await SocialEndpoints.IsBlockedEitherWay(db, me, owner, cancel))
        {
            return Problem("You can only make offers to friends.");
        }

        WalkingProfile ownerProfile = await WalkingEndpoints.GetOrCreateProfile(db, owner, cancel);
        if (ownerProfile.CompanionMonsterId == monster.Id) return Problem("That's their companion - the monster they walk with. It isn't up for offers.");
        if (now < monster.AcquiredUtc + SettleIn) return Problem("That monster is still settling in with its walker. Try again in a few days.");
        if (request.Price < MinPrice(monster.RarityTier) || request.Price > MaxPrice(monster.RarityTier))
        {
            return Problem($"For this rarity, offers are between {MinPrice(monster.RarityTier):N0} and {MaxPrice(monster.RarityTier):N0} Glimmer.");
        }

        if (await db.MonsterOffers.CountAsync(o => o.FromUserId == me && o.Status == OfferStatus.Pending, cancel) >= MaxOpenOffers)
        {
            return Problem($"You already have {MaxOpenOffers} offers waiting for an answer.");
        }

        if (await db.MonsterOffers.AnyAsync(o => o.FromUserId == me && o.MonsterId == monster.Id && o.Status == OfferStatus.Pending, cancel))
        {
            return Problem("You've already made an offer for this monster.");
        }

        if (await TradesThisWeek(db, me, now, cancel) >= MaxTradesPerWeek) return Problem($"That's {MaxTradesPerWeek} trades this week - offers open again soon.");

        WalkingProfile buyer = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        if (!WalkingWallet.TrySpend(db, buyer, request.Price, "offer-held", "Held for an offer"))
        {
            return Problem($"You need {request.Price:N0} Glimmer for that offer (you have {buyer.Glimmer:N0}).");
        }

        var offer = new MonsterOffer { MonsterId = monster.Id, FromUserId = me, ToUserId = owner, Price = request.Price, CreatedUtc = now, ExpiresUtc = now + OfferLifetime };
        db.MonsterOffers.Add(offer);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await ToDto(db, offer, me, cancel));
    }

    private static async Task<IResult> MyOffers(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        await ExpireOld(db, DateTime.UtcNow, cancel);

        DateTime recent = DateTime.UtcNow.AddDays(-7);
        List<MonsterOffer> offers = await db.MonsterOffers.AsNoTracking()
            .Where(o => (o.FromUserId == me || o.ToUserId == me) && (o.Status == OfferStatus.Pending || o.CreatedUtc > recent))
            .OrderByDescending(o => o.CreatedUtc)
            .ToListAsync(cancel);

        var list = new List<OfferDto>();
        foreach (MonsterOffer o in offers) list.Add(await ToDto(db, o, me, cancel));
        return Results.Ok(list);
    }

    private static async Task<IResult> Accept(Guid offerId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await ExpireOld(db, now, cancel);

        MonsterOffer? offer = await db.MonsterOffers.FirstOrDefaultAsync(o => o.Id == offerId && o.ToUserId == me, cancel);
        if (offer == null) return Results.NotFound();
        if (offer.Status != OfferStatus.Pending) return Problem("That offer isn't open any more.");

        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == offer.MonsterId, cancel);
        WalkingProfile seller = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        if (monster == null || monster.OwnerId != me) return Problem("That monster isn't with you any more.");
        if (seller.CompanionMonsterId == monster.Id) return Problem("That's your companion. Choose a different companion first if you really want to say goodbye.");
        if (await TradesThisWeek(db, me, now, cancel) >= MaxTradesPerWeek) return Problem($"That's {MaxTradesPerWeek} trades this week for you.");
        if (await TradesThisWeek(db, offer.FromUserId, now, cancel) >= MaxTradesPerWeek) return Problem("Your friend has made all their trades for this week.");

        // The monster - level, training and all - moves to its new walker.
        monster.OwnerId = offer.FromUserId;
        monster.AcquiredUtc = now;
        monster.TradeCount++;
        monster.ArrivedFromUserId = me;
        monster.Seen = false;   // so its new walker gets a welcome reveal

        offer.Status = OfferStatus.Accepted;
        offer.DecidedUtc = now;
        WalkingWallet.Change(db, seller, offer.Price, "offer-sold", "A monster found a new home");

        // Anyone else who offered for it gets their Glimmer back.
        List<MonsterOffer> others = await db.MonsterOffers.Where(o => o.MonsterId == monster.Id && o.Status == OfferStatus.Pending && o.Id != offer.Id).ToListAsync(cancel);
        foreach (MonsterOffer other in others) await Refund(db, other, OfferStatus.Declined, now, cancel);

        await db.SaveChangesAsync(cancel);
        return Results.Ok(await ToDto(db, offer, me, cancel));
    }

    private static async Task<IResult> Close(Guid offerId, OfferStatus status, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        await ExpireOld(db, DateTime.UtcNow, cancel);

        // The owner declines; the one who made the offer cancels.
        MonsterOffer? offer = await db.MonsterOffers.FirstOrDefaultAsync(o => o.Id == offerId &&
            (status == OfferStatus.Declined ? o.ToUserId == me : o.FromUserId == me), cancel);
        if (offer == null) return Results.NotFound();
        if (offer.Status != OfferStatus.Pending) return Problem("That offer isn't open any more.");

        await Refund(db, offer, status, DateTime.UtcNow, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await ToDto(db, offer, me, cancel));
    }

    private static async Task Refund(AppDbContext db, MonsterOffer offer, OfferStatus status, DateTime now, CancellationToken cancel)
    {
        offer.Status = status;
        offer.DecidedUtc = now;
        WalkingProfile buyer = await WalkingEndpoints.GetOrCreateProfile(db, offer.FromUserId, cancel);
        WalkingWallet.Change(db, buyer, offer.Price, "offer-returned", "Offer " + status.ToString().ToLowerInvariant() + " - Glimmer returned");
    }

    // Offers nobody answered in time are closed, and their Glimmer goes back.
    private static async Task ExpireOld(AppDbContext db, DateTime now, CancellationToken cancel)
    {
        List<MonsterOffer> stale = await db.MonsterOffers.Where(o => o.Status == OfferStatus.Pending && o.ExpiresUtc < now).ToListAsync(cancel);
        if (stale.Count == 0) return;
        foreach (MonsterOffer o in stale) await Refund(db, o, OfferStatus.Expired, now, cancel);
        await db.SaveChangesAsync(cancel);
    }

    private static Task<int> TradesThisWeek(AppDbContext db, Guid user, DateTime now, CancellationToken cancel)
    {
        DateTime since = now.AddDays(-7);
        return db.MonsterOffers.CountAsync(o => o.Status == OfferStatus.Accepted && o.DecidedUtc > since && (o.FromUserId == user || o.ToUserId == user), cancel);
    }

    private static async Task<OfferDto> ToDto(AppDbContext db, MonsterOffer o, Guid me, CancellationToken cancel)
    {
        WalkingMonster? m = await db.WalkingMonsters.AsNoTracking().FirstOrDefaultAsync(x => x.Id == o.MonsterId, cancel);
        bool incoming = o.ToUserId == me;
        Guid other = incoming ? o.FromUserId : o.ToUserId;
        string name = await db.Users.Where(u => u.Id == other).Select(u => u.Username).FirstOrDefaultAsync(cancel) ?? "A friend";
        return new OfferDto(o.Id, o.MonsterId, m?.SpeciesId ?? "", m?.RarityTier ?? 0, m?.Seed ?? 0, m?.Serial ?? 0, o.Price, o.Status.ToString(),
            incoming, other, name, o.CreatedUtc, o.ExpiresUtc);
    }

    private static IResult Problem(string message) => Results.Problem(message, statusCode: StatusCodes.Status400BadRequest);
}
