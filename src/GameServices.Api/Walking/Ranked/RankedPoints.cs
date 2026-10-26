namespace GameServices.Api.Walking.Ranked;

public enum RankedTier { Bronze = 0, Silver = 1, Gold = 2, Platinum = 3, Diamond = 4, Legend = 5 }

/// <summary>What one fight did to the attacker's and defender's Ranked Points.</summary>
public readonly record struct PointsChange(int Attacker, int Defender, bool HighValue);

/// <summary>
/// RANKED POINTS - the player ranking (the walker asked for rank to reward powerful and rare monsters and big wins):
///
///   A WIN is worth more the more impressive it is:
///     10                                   for any win
///     + 3 per rarity tier of the loser     (beating a Legendary: +12)
///     + 1 per 1,000 of the loser's BST     (a 10,000 BST monster: +10)
///     + an UPSET bonus when the loser's BST was higher than yours: +2 for every 5% higher (up to +10)
///   A shadow monster counts half.
///
///   A HIGH-VALUE WIN is beating a monster with a higher BST than yours, or an Epic or Legendary one. They're counted
///   separately and break ties on the leaderboard.
///
///   A LOSS costs 5 points - only 2 if their monster was at least 10% stronger (no shame in that), and only 2 against a
///   shadow. Points never go below 0.
///
///   DEFENDING while offline counts half both ways: a monster that holds off an attack earns half the win points; one
///   that's beaten costs its walker half a loss.
///
/// TIERS come from the season's points: Bronze, Silver (100), Gold (300), Platinum (700), Diamond (1,500) - and
/// LEGEND for the top 100 Diamond walkers in the world. Each month is a new season; everyone starts it with a quarter
/// of last season's points, and last season's tier earns a reward.
/// </summary>
public static class RankedPoints
{
    public const int BaseWin = 10;
    public const int PerRarityTier = 3;
    public const int PerThousandBst = 1;
    public const int UpsetStepPercent = 5;
    public const int UpsetPointsPerStep = 2;
    public const int MaxUpset = 10;
    public const int Loss = 5;
    public const int GentleLoss = 2;

    public static readonly int[] TierThresholds = { 0, 100, 300, 700, 1500 };
    public const int LegendPlaces = 100;

    /// <summary>A month: "2026-10".</summary>
    public static string SeasonOf(DateTime utc) => utc.ToString("yyyy-MM");

    public static string PreviousSeason(string season)
    {
        DateTime start = DateTime.ParseExact(season + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return SeasonOf(start.AddMonths(-1));
    }

    public static DateTime SeasonEndsUtc(string season) =>
        DateTime.SpecifyKind(DateTime.ParseExact(season + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).AddMonths(1), DateTimeKind.Utc);

    /// <summary>Everyone starts a new season with a quarter of last season's points.</summary>
    public static int CarryOver(int lastSeasonPoints) => Math.Max(0, lastSeasonPoints) / 4;

    public static bool IsHighValue(int myBst, int theirBst, int theirTier) => theirBst > myBst || theirTier >= 3;

    public static int WinPoints(int myBst, int theirBst, int theirTier, bool shadow)
    {
        int points = BaseWin + PerRarityTier * Math.Clamp(theirTier, 0, 4) + PerThousandBst * Math.Max(0, theirBst) / 1000;

        if (theirBst > myBst && myBst > 0)
        {
            double percentHigher = (theirBst / (double)myBst - 1.0) * 100.0;
            points += Math.Min(MaxUpset, (int)(percentHigher / UpsetStepPercent) * UpsetPointsPerStep);
        }

        return shadow ? (points + 1) / 2 : points;
    }

    public static int LossPoints(int myBst, int theirBst, bool shadow) =>
        shadow || theirBst >= myBst * 1.10 ? GentleLoss : Loss;

    /// <summary>Points for both walkers after a fight (the defender's half, or 0 for a shadow).</summary>
    public static PointsChange For(bool attackerWon, int attackerBst, int attackerTier, int defenderBst, int defenderTier, bool shadow)
    {
        if (attackerWon)
        {
            int win = WinPoints(attackerBst, defenderBst, defenderTier, shadow);
            int defenderLoss = shadow ? 0 : (LossPoints(defenderBst, attackerBst, false) + 1) / 2;
            return new PointsChange(win, -defenderLoss, IsHighValue(attackerBst, defenderBst, defenderTier));
        }

        int loss = LossPoints(attackerBst, defenderBst, shadow);
        int defenderWin = shadow ? 0 : (WinPoints(defenderBst, attackerBst, attackerTier, false) + 1) / 2;
        return new PointsChange(-loss, defenderWin, false);
    }

    /// <summary>The tier for some points (Legend is decided by place, so this stops at Diamond).</summary>
    public static RankedTier TierFor(int points)
    {
        RankedTier tier = RankedTier.Bronze;
        for (int i = 0; i < TierThresholds.Length; i++)
        {
            if (points >= TierThresholds[i]) tier = (RankedTier)i;
        }
        return tier;
    }

    /// <summary>The tier including Legend: a Diamond walker placed in the world's top LegendPlaces.</summary>
    public static RankedTier TierFor(int points, int worldPlace) =>
        TierFor(points) == RankedTier.Diamond && worldPlace >= 1 && worldPlace <= LegendPlaces ? RankedTier.Legend : TierFor(points);

    /// <summary>Glimmer for finishing a season in each tier, and the title it unlocks.</summary>
    public static (int Glimmer, string Title) SeasonReward(RankedTier tier, string season) => tier switch
    {
        RankedTier.Silver => (100, ""),
        RankedTier.Gold => (250, "Gold Contender"),
        RankedTier.Platinum => (500, "Platinum Fighter"),
        RankedTier.Diamond => (1000, "Diamond Champion"),
        RankedTier.Legend => (2000, "Legend of " + SeasonName(season)),
        _ => (25, ""),
    };

    public static string SeasonName(string season) =>
        DateTime.ParseExact(season + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).ToString("MMMM yyyy");

    /// <summary>Monster leaderboard score: its BST, plus 100 for every ranked win.</summary>
    public const int MonsterScorePerWin = 100;
    public static int MonsterScore(int bst, int wins) => bst + MonsterScorePerWin * Math.Max(0, wins);
}
