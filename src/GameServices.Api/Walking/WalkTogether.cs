using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking;

/// <summary>What walking together earned on one step report.</summary>
public readonly record struct TogetherResult(List<string> WalkingWith, int Bonus, int DaysTotal);

/// <summary>
/// WALK WITH FRIENDS: walking at the same time and place as a friend earns +25% Glimmer on those steps.
///
/// How "together" is worked out - privately:
///   * Only when the walker has switched it on does the phone add a ROUGH position to its step report (rounded on the
///     phone). The server keeps just a ~100 m square ("cell"), and never sends anyone's cell or position to anyone.
///   * Two FRIENDS are together when both reported from the same or a neighbouring cell within the last few minutes,
///     and BOTH reports added steps - so two phones on a sofa (or posted to a friend) don't count.
///   * The bonus is 1 Glimmer per 400 steps walked together (a quarter of the usual 1 per 100), up to DailyBonusCap a
///     day. The other person only ever learns that you're walking with them - by name.
/// </summary>
public static class WalkTogether
{
    /// <summary>One cell is about this many degrees of latitude tall (~111 m), and the same distance wide.</summary>
    public const double CellDegrees = 0.001;

    /// <summary>A friend's report counts as "now" for this long.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(3);

    /// <summary>+25%: the usual rate is 1 Glimmer per 100 steps.</summary>
    public const int StepsPerBonusGlimmer = WalkingWallet.StepsPerGlimmer * 4;

    public const int DailyBonusCap = 50;

    public static (int X, int Y) Cell(double lat, double lon)
    {
        int y = (int)Math.Floor(lat / CellDegrees);
        // Squares, not strips: a degree of longitude shrinks towards the poles. The scale comes from the cell's own
        // latitude, so neighbours always agree.
        double scale = Math.Cos(y * CellDegrees * Math.PI / 180.0);
        int x = (int)Math.Floor(lon * scale / CellDegrees);
        return (x, y);
    }

    /// <summary>
    /// Called with every step report. lat/lon are null when the walker hasn't switched the feature on (then they're
    /// never matched with anyone). day is the walker's own calendar day, as for the steps.
    /// </summary>
    public static async Task<TogetherResult> ApplyAsync(AppDbContext db, WalkingProfile profile, double? lat, double? lon,
        int addedSteps, string day, DateTime nowUtc, CancellationToken cancel)
    {
        var none = new TogetherResult(new List<string>(), 0, profile.TogetherDaysTotal);

        if (lat is not double la || lon is not double lo || Math.Abs(la) > 90 || Math.Abs(lo) > 180)
        {
            profile.TogetherSeenUtc = null;   // switched off: forget the last cell straight away
            return none;
        }

        if (addedSteps <= 0)
        {
            profile.TogetherSeenUtc = null;   // standing still doesn't count as walking with anyone
            return none;
        }

        (int x, int y) = Cell(la, lo);
        profile.TogetherCellX = x;
        profile.TogetherCellY = y;
        profile.TogetherSeenUtc = nowUtc;

        // Friends who were walking in the same or a neighbouring cell just now.
        Guid me = profile.UserId;
        List<Guid> friends = await db.Friendships.AsNoTracking()
            .Where(f => (f.UserAId == me || f.UserBId == me) && f.Status == FriendshipStatus.Accepted)
            .Select(f => f.UserAId == me ? f.UserBId : f.UserAId)
            .ToListAsync(cancel);
        if (friends.Count == 0) return none;

        DateTime since = nowUtc - Window;
        List<Guid> together = await db.WalkingProfiles.AsNoTracking()
            .Where(p => friends.Contains(p.UserId) && p.TogetherSeenUtc != null && p.TogetherSeenUtc >= since
                        && p.TogetherCellX >= x - 1 && p.TogetherCellX <= x + 1
                        && p.TogetherCellY >= y - 1 && p.TogetherCellY <= y + 1)
            .Select(p => p.UserId)
            .ToListAsync(cancel);
        if (together.Count == 0) return none;

        List<string> names = await db.Users.AsNoTracking()
            .Where(u => together.Contains(u.Id))
            .OrderBy(u => u.Username)
            .Select(u => u.Username)
            .ToListAsync(cancel);

        // A new day: the daily cap starts again, and it's one more day walked with a friend.
        if (profile.TogetherDay != day)
        {
            profile.TogetherDay = day;
            profile.TogetherBonusToday = 0;
            profile.TogetherDaysTotal++;
        }

        profile.TogetherStepRemainder += addedSteps;
        int bonus = Math.Min(profile.TogetherStepRemainder / StepsPerBonusGlimmer, DailyBonusCap - profile.TogetherBonusToday);
        profile.TogetherStepRemainder %= StepsPerBonusGlimmer;

        if (bonus > 0)
        {
            profile.TogetherBonusToday += bonus;
            WalkingWallet.Change(db, profile, bonus, "together", "Walking with " + string.Join(", ", names));
        }

        return new TogetherResult(names, Math.Max(0, bonus), profile.TogetherDaysTotal);
    }
}
