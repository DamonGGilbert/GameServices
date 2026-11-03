using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Monsters;

/// <summary>
/// A monster going to walk with someone new - through an accepted offer, a Daily Swap or the Market. One place, so every
/// way a monster changes hands tidies up the same:
///   * it keeps its level and training, and counts as newly arrived (settling in again, and a welcome reveal)
///   * its place on its old walker's ranked team ends (otherwise its new walker could never add it to theirs)
///   * any other offers for it are turned down (Glimmer refunded), any other swap requests with it are closed, and if
///     it's on the Market its listing closes (the top bid goes back)
/// </summary>
public static class MonsterHandover
{
    /// <param name="exceptOffer">The offer being accepted (left alone).</param>
    /// <param name="exceptSwap">The swap being done (left alone).</param>
    /// <param name="exceptListing">The Market listing being sold (left alone).</param>
    public static async Task Move(AppDbContext db, WalkingMonster monster, Guid newOwner, DateTime now, Guid? exceptOffer, Guid? exceptSwap,
        Guid? exceptListing, CancellationToken cancel)
    {
        Guid oldOwner = monster.OwnerId;
        monster.OwnerId = newOwner;
        monster.AcquiredUtc = now;
        monster.TradeCount++;
        monster.ArrivedFromUserId = oldOwner;
        monster.Seen = false;   // so its new walker gets a welcome reveal

        db.RankedEntries.RemoveRange(await db.RankedEntries.Where(e => e.MonsterId == monster.Id).ToListAsync(cancel));

        List<MonsterOffer> offers = await db.MonsterOffers
            .Where(o => o.MonsterId == monster.Id && o.Status == OfferStatus.Pending && o.Id != exceptOffer).ToListAsync(cancel);
        foreach (MonsterOffer offer in offers) await MonsterOffers.Refund(db, offer, OfferStatus.Declined, now, cancel);

        List<MonsterSwap> swaps = await db.MonsterSwaps
            .Where(s => (s.FromMonsterId == monster.Id || s.ToMonsterId == monster.Id) && s.Status == SwapStatus.Pending && s.Id != exceptSwap).ToListAsync(cancel);
        foreach (MonsterSwap swap in swaps)
        {
            swap.Status = SwapStatus.Closed;
            swap.DecidedUtc = now;
        }

        List<MarketListing> listings = await db.MarketListings
            .Where(l => l.MonsterId == monster.Id && l.Status == ListingStatus.Active && l.Id != exceptListing).ToListAsync(cancel);
        foreach (MarketListing listing in listings) await MonsterMarket.Close(db, listing, ListingStatus.Closed, now, cancel);
    }
}
