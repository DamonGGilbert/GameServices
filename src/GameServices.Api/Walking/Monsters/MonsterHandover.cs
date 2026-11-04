using GameServices.Api.Data;
using GameServices.Api.Walking.Bazaar;
using GameServices.Api.Walking.Progress;
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
        monster.RestUntilSteps = 0;   // a snooze is counted in its old walker's steps
        monster.BondSteps = 0;        // and a bond is with one walker
        monster.TrainingSteps = 0;    // and so is today's training
        monster.TrainingDay = "";

        // It counts as a trade for them both (the Trader isn't a walker, and buying and selling there isn't trading).
        if (oldOwner != Trader.Id && newOwner != Trader.Id) await Explorer.OnTradeAsync(db, oldOwner, newOwner, monster, now, cancel);

        await LetGo(db, monster, now, exceptOffer, exceptSwap, exceptListing, cancel);
    }

    /// <summary>Everything waiting on a monster that's leaving its walker ends, and any Glimmer held goes back to its owner.</summary>
    public static async Task LetGo(AppDbContext db, WalkingMonster monster, DateTime now, Guid? exceptOffer, Guid? exceptSwap,
        Guid? exceptListing, CancellationToken cancel)
    {
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
