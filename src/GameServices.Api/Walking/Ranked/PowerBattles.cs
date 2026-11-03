using GameServices.Api.Data;
using GameServices.Api.Walking.Monsters;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Ranked;

/// <summary>
/// POWER BATTLES: a challenge both walkers agree to, where the winner's monster ABSORBS power from the loser's.
///
///   * It's an invitation (ChallengeEndpoints with PowerBattle = true): the walker challenged sees it's a power battle
///     and accepts or declines - nothing is at risk until they say yes.
///   * The winner absorbs 5% of the loser's power, shared across its four stats the way its own are - for good.
///   * The loser's monster loses that much and turns into a CRYSTAL: it can't battle (ranked, challenges, trainers)
///     until its walker walks 6,000 steps, which bring its power back bit by bit. Rarity never changes and nothing is
///     lost for ever - it's walked back.
///   * Fair fights only: the two monsters must be within 25% of each other's BST, neither can already be a crystal,
///     and each walker has at most 3 power battles a day.
/// </summary>
public static class PowerBattles
{
    public const double AbsorbShare = 0.05;
    public const int CrystalSteps = 6000;
    public const int MaxPerDay = 3;

    /// <summary>A monster's power: its four battle stats together (as its card shows it).</summary>
    public static int PowerOf(MonsterBattleStats s) => s.Power + s.Guard + s.Tricks + s.Wits;

    public static bool IsCrystal(WalkingMonster m) => m.CrystalStepsLeft > 0;

    /// <summary>
    /// The power battle is decided: the winner absorbs 5% of the loser's power (in proportion to its own stats) and the
    /// loser becomes a crystal, drained by the same amount. Returns how much power moved.
    /// </summary>
    public static int Settle(WalkingMonster winner, MonsterBattleStats winnerStats, WalkingMonster loser, MonsterBattleStats loserStats)
    {
        int amount = Math.Max(1, (int)Math.Round(PowerOf(loserStats) * AbsorbShare));
        int[] share = Split(amount, new[] { winnerStats.Power, winnerStats.Guard, winnerStats.Tricks, winnerStats.Wits });
        winner.AbsorbedPower += share[0];
        winner.AbsorbedGuard += share[1];
        winner.AbsorbedTricks += share[2];
        winner.AbsorbedWits += share[3];

        loser.CrystalPower = amount;
        loser.CrystalSteps = CrystalSteps;
        loser.CrystalStepsLeft = CrystalSteps;
        return amount;
    }

    /// <summary>Shares an amount out in proportion to the weights, whole numbers that add up exactly (largest remainder).</summary>
    public static int[] Split(int amount, int[] weights)
    {
        long total = weights.Sum(w => (long)Math.Max(0, w));
        var result = new int[weights.Length];
        if (total <= 0)
        {
            for (int i = 0; i < amount; i++) result[i % result.Length]++;
            return result;
        }

        var remainders = new (double Part, int Index)[weights.Length];
        int given = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            double exact = amount * (double)Math.Max(0, weights[i]) / total;
            result[i] = (int)Math.Floor(exact);
            given += result[i];
            remainders[i] = (exact - result[i], i);
        }

        foreach ((double _, int index) in remainders.OrderByDescending(r => r.Part).ThenBy(r => r.Index).Take(amount - given)) result[index]++;
        return result;
    }

    /// <summary>Steps the server has accepted bring this walker's crystals back, a little at a time.</summary>
    public static async Task WalkBackAsync(AppDbContext db, Guid owner, int steps, CancellationToken cancel)
    {
        if (steps <= 0) return;
        List<WalkingMonster> crystals = await db.WalkingMonsters.Where(m => m.OwnerId == owner && m.CrystalStepsLeft > 0).ToListAsync(cancel);
        foreach (WalkingMonster m in crystals)
        {
            m.CrystalStepsLeft = Math.Max(0, m.CrystalStepsLeft - steps);
            if (m.CrystalStepsLeft > 0) continue;
            m.CrystalPower = 0;   // all its power is back
            m.CrystalSteps = 0;
        }
    }

    /// <summary>Power battles this walker has waiting or has fought in the last day (sent or answered).</summary>
    public static Task<int> TodayAsync(AppDbContext db, Guid user, DateTime now, Guid? except, CancellationToken cancel)
    {
        DateTime since = now.AddDays(-1);
        return db.Challenges.CountAsync(c => c.PowerBattle && c.Id != except && (c.FromUserId == user || c.ToUserId == user) &&
            (c.Status == ChallengeStatus.Pending || (c.Status == ChallengeStatus.Fought && c.DecidedUtc > since)), cancel);
    }
}
