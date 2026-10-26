using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Ranked;

/// <summary>Season records: made the first time a walker is seen in a season, starting from a quarter of last season's points.</summary>
public static class RankedSeasons
{
    public static async Task<RankedSeasonRecord> GetOrCreateAsync(AppDbContext db, Guid userId, string season, CancellationToken cancel)
    {
        RankedSeasonRecord? record = db.RankedSeasons.Local.FirstOrDefault(r => r.UserId == userId && r.Season == season)
            ?? await db.RankedSeasons.FirstOrDefaultAsync(r => r.UserId == userId && r.Season == season, cancel);
        if (record != null) return record;

        string previous = RankedPoints.PreviousSeason(season);
        int last = await db.RankedSeasons.Where(r => r.UserId == userId && r.Season == previous).Select(r => r.Points).FirstOrDefaultAsync(cancel);

        record = new RankedSeasonRecord { UserId = userId, Season = season, Points = RankedPoints.CarryOver(last) };
        db.RankedSeasons.Add(record);
        return record;
    }

    /// <summary>Leaderboard order: points, then high-value wins, then wins.</summary>
    public static IQueryable<RankedSeasonRecord> Ordered(IQueryable<RankedSeasonRecord> records) =>
        records.OrderByDescending(r => r.Points).ThenByDescending(r => r.HighValueWins).ThenByDescending(r => r.Wins).ThenBy(r => r.UpdatedUtc);

    /// <summary>1-based place in the world for this season (0 if the walker hasn't fought this season).</summary>
    public static async Task<int> WorldPlaceAsync(AppDbContext db, RankedSeasonRecord record, CancellationToken cancel)
    {
        if (record.Wins + record.Losses == 0 && record.Points == 0) return 0;
        int ahead = await db.RankedSeasons.CountAsync(r => r.Season == record.Season && r.UserId != record.UserId &&
            (r.Points > record.Points ||
             (r.Points == record.Points && r.HighValueWins > record.HighValueWins) ||
             (r.Points == record.Points && r.HighValueWins == record.HighValueWins && r.Wins > record.Wins)), cancel);
        return ahead + 1;
    }

    public static async Task<RankedTier> TierAsync(AppDbContext db, RankedSeasonRecord record, CancellationToken cancel)
    {
        RankedTier tier = RankedPoints.TierFor(record.Points);
        if (tier != RankedTier.Diamond) return tier;
        return RankedPoints.TierFor(record.Points, await WorldPlaceAsync(db, record, cancel));
    }
}
