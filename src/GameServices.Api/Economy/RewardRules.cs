using GameServices.Api.Data;

namespace GameServices.Api.Economy;

/// <summary>
/// WHEN A SINGLE-PLAYER OPPONENT PAYS DUEL POINTS (the game shows the same rule; the server decides).
///
///   * Every opponent pays once per Prestige rank. Win again at the same rank: the duel still counts for XP and the
///     record, but it pays nothing. Prestige, and they pay once more.
///   * From CooldownPrestige on, an opponent pays once every 24 hours instead, so the top ranks cannot be farmed.
///   * Online duels pay no Duel Points at all.
/// </summary>
public static class RewardRules
{
    public const int MaxPrestige = 10;

    /// <summary>From this Prestige rank on, a paid-out opponent comes back after Cooldown instead of at the next rank.</summary>
    public const int CooldownPrestige = 3;

    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);

    public static bool IsEligible(OpponentReward? claim, int prestige, DateTime now, out string note)
    {
        note = "";

        if (claim == null || prestige > claim.Prestige)
        {
            return true;
        }

        if (prestige >= CooldownPrestige)
        {
            TimeSpan left = Cooldown - (now - claim.LastClaimUtc);

            if (left <= TimeSpan.Zero)
            {
                return true;
            }

            note = $"Ready again in {Describe(left)}.";
            return false;
        }

        note = "Already paid this rank. Prestige to unlock.";
        return false;
    }

    private static string Describe(TimeSpan span)
    {
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{Math.Max(1, span.Minutes)}m";
    }
}
