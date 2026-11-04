using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Auth;

public record DeleteAccountRequest(string Password);

/// <summary>
/// DELETE MY ACCOUNT:  POST /api/auth/delete-account  { password }
///
/// Removes the account and everything that belongs to it, in every game: monsters, Glimmer, steps, friends, chat and
/// the rest go with the user row (the database's cascading foreign keys). Other players don't pay for it:
///   * their Glimmer comes back if it was held on this walker's monsters (offers, Market bids) or in a challenge to them
///   * a Market listing they were top bidder on carries on with no bids, and a guild they led passes to the next member
///
/// Only a report ABOUT this walker stays (it holds their name then, not an account), so safety problems can still be
/// looked into. It's cleared after a year (WalkingLifecycleService).
/// </summary>
public static class AccountDeletion
{
    public static async Task<IResult> Delete(DeleteAccountRequest request, ClaimsPrincipal principal, AppDbContext db, IPasswordHasher<User> hasher, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Id == me, cancel);
        if (user == null) return Results.NotFound();

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request?.Password ?? "") == PasswordVerificationResult.Failed)
        {
            return Results.Problem("That password isn't right.", statusCode: StatusCodes.Status400BadRequest);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancel);
        await SettleWithOthers(db, me, DateTime.UtcNow, cancel);
        await db.SaveChangesAsync(cancel);
        await RemoveLooseEnds(db, me, cancel);
        db.ChangeTracker.Clear();
        await db.Users.Where(u => u.Id == me).ExecuteDeleteAsync(cancel);
        await transaction.CommitAsync(cancel);
        return Results.NoContent();
    }

    // Everything other players are owed or waiting on.
    private static async Task SettleWithOthers(AppDbContext db, Guid me, DateTime now, CancellationToken cancel)
    {
        foreach (WalkingMonster monster in await db.WalkingMonsters.Where(m => m.OwnerId == me).ToListAsync(cancel))
        {
            await MonsterHandover.LetGo(db, monster, now, null, null, null, cancel);
        }

        foreach (Challenge challenge in await db.Challenges.Where(c => c.ToUserId == me && c.Status == ChallengeStatus.Pending).ToListAsync(cancel))
        {
            await ChallengeEndpoints.RefundAsync(db, challenge, ChallengeStatus.Cancelled, cancel);
        }

        foreach (MarketListing listing in await db.MarketListings.Where(l => l.TopBidderId == me && l.Status == ListingStatus.Active).ToListAsync(cancel))
        {
            listing.TopBidderId = null;
            listing.CurrentBid = 0;
            listing.BidCount = 0;
        }

        foreach (GuildMember led in await db.GuildMembers.Where(m => m.UserId == me && m.Role == GuildRole.Leader).ToListAsync(cancel))
        {
            GuildMember? next = await db.GuildMembers.Where(m => m.GuildId == led.GuildId && m.UserId != me)
                .OrderByDescending(m => m.Role).ThenBy(m => m.JoinedUtc).FirstOrDefaultAsync(cancel);
            if (next != null) next.Role = GuildRole.Leader;
            else await db.Guilds.Where(g => g.Id == led.GuildId).ExecuteDeleteAsync(cancel);
        }
    }

    // Records that mention this walker without the database knowing they're connected.
    private static async Task RemoveLooseEnds(AppDbContext db, Guid me, CancellationToken cancel)
    {
        await db.Challenges.Where(c => c.ToUserId == me).ExecuteDeleteAsync(cancel);
        await db.MonsterOffers.Where(o => o.FromUserId == me || o.ToUserId == me).ExecuteDeleteAsync(cancel);
        await db.MonsterSwaps.Where(s => s.FromUserId == me || s.ToUserId == me).ExecuteDeleteAsync(cancel);
        await db.MarketBids.Where(b => b.BidderId == me).ExecuteDeleteAsync(cancel);
        await db.GuildInvites.Where(i => i.InvitedById == me).ExecuteDeleteAsync(cancel);

        await db.MarketListings.Where(l => l.TopBidderId == me).ExecuteUpdateAsync(s => s.SetProperty(l => l.TopBidderId, (Guid?)null), cancel);
        await db.MarketListings.Where(l => l.BuyerId == me).ExecuteUpdateAsync(s => s.SetProperty(l => l.BuyerId, (Guid?)null), cancel);
        await db.WalkingMonsters.Where(m => m.ArrivedFromUserId == me).ExecuteUpdateAsync(s => s.SetProperty(m => m.ArrivedFromUserId, (Guid?)null), cancel);

        // Fights they defended stay in the attackers' histories, as a shadow monster.
        await db.RankedMatches.Where(m => m.DefenderId == me).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.DefenderId, (Guid?)null)
            .SetProperty(m => m.DefenderMonsterId, (Guid?)null)
            .SetProperty(m => m.Shadow, true), cancel);
    }
}
