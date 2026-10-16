using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Economy;

/// <summary>
/// LUCKY DRAW TICKETS. They are never sold: they are earned, and every way of earning one pays only ONCE
/// (the pair user + reason is unique in the database), so there is nothing to farm.
///
///   final-N      beat the final opponent at Prestige rank N   (claimed by the game, once per rank)
///   prestige-N   reached Prestige rank N                      (claimed by the game, once per rank)
///   war-ID       won your duel in a Guild War                 (granted by the server itself)
/// </summary>
public static class Tickets
{
    /// <summary>A hacked game cannot claim tickets faster than this.</summary>
    public static readonly TimeSpan MinTimeBetweenClaims = TimeSpan.FromSeconds(20);

    /// <summary>Is this one of the reasons the GAME may claim (as opposed to the server granting it)?</summary>
    public static bool TryMakeReason(string? kind, int prestige, out string reason)
    {
        reason = "";

        if (prestige < 0 || prestige > RewardRules.MaxPrestige)
        {
            return false;
        }

        switch ((kind ?? "").Trim().ToLowerInvariant())
        {
            case "final-opponent":
                reason = "final-" + prestige;
                return true;
            case "prestige":
                reason = "prestige-" + prestige;
                return prestige >= 1;
            default:
                return false;
        }
    }

    /// <summary>
    /// Adds one ticket to the player's wallet if this reason has not paid yet. Does NOT save: the caller saves, so the
    /// ticket arrives in the same all-or-nothing transaction as whatever caused it.
    /// </summary>
    public static async Task<bool> GrantAsync(AppDbContext db, Guid userId, string reason, CancellationToken cancel)
    {
        CardGameProfile? profile = await db.CardGameProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancel);

        if (profile == null || await db.TicketGrants.AnyAsync(g => g.UserId == userId && g.Reason == reason, cancel))
        {
            return false;
        }

        db.TicketGrants.Add(new TicketGrant { UserId = userId, Reason = reason });
        profile.SpinTickets += 1;
        profile.Version += 1;

        db.Ledger.Add(new LedgerEntry { UserId = userId, Kind = "ticket", Detail = "Lucky Draw ticket: " + reason, Amount = 0, BalanceAfter = profile.DuelPoints });

        return true;
    }
}
