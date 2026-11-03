using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Monsters;

public record SwapRequest(Guid MyMonsterId, Guid TheirMonsterId);

/// <summary>One swap as I see it: Mine is the monster I'd give (or gave), Theirs the one I'd get (or got).</summary>
public record SwapDto(Guid Id, string Status, bool Incoming, Guid OtherUserId, string OtherUsername,
    WalkingMonsterDto? Mine, WalkingMonsterDto? Theirs, DateTime CreatedUtc, DateTime ExpiresUtc, DateTime? DecidedUtc);

/// <param name="NextSwapUtc">When I can next swap (null = I can now).</param>
/// <param name="SettlingMonsterIds">My monsters still settling in, which can't be swapped yet.</param>
public record SwapListDto(List<SwapDto> Swaps, DateTime? NextSwapUtc, List<Guid> SettlingMonsterIds);

/// <summary>
/// THE DAILY SWAP: one of your monsters for one of a friend's, once a day.
///
///   POST /api/walking/swaps                      ask a friend: my monster for yours
///   GET  /api/walking/swaps                      swap requests to me and from me, and when I can next swap
///   POST /api/walking/swaps/{id}/accept | decline | cancel
///
/// Both walkers agree (one asks, the other accepts) and the server swaps the two monsters in one step - each keeps its
/// level and training - so nothing can go missing halfway. The rules keep it friendly:
///   * friends only (never strangers, never anyone blocked)
///   * one completed swap a day each (24 hours from your last one)
///   * companions never swap, and a monster must have been with its walker for 3 days first (as with offers)
///   * up to 3 swap requests waiting at once, each of your monsters in only one of them; requests expire after 2 days
/// </summary>
public static class MonsterSwaps
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(2);
    public const int MaxOpen = 3;

    public static RouteGroupBuilder MapMonsterSwapEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/swaps", Ask);
        group.MapGet("/swaps", MySwaps);
        group.MapPost("/swaps/{swapId:guid}/accept", Accept);
        group.MapPost("/swaps/{swapId:guid}/decline", (Guid swapId, ClaimsPrincipal p, AppDbContext db, CancellationToken c) => Close(swapId, SwapStatus.Declined, p, db, c));
        group.MapPost("/swaps/{swapId:guid}/cancel", (Guid swapId, ClaimsPrincipal p, AppDbContext db, CancellationToken c) => Close(swapId, SwapStatus.Cancelled, p, db, c));
        return group;
    }

    private static async Task<IResult> Ask(SwapRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await ExpireOld(db, now, cancel);

        WalkingMonster? mine = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == request.MyMonsterId && m.OwnerId == me, cancel);
        WalkingMonster? theirs = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == request.TheirMonsterId, cancel);
        if (mine == null) return Problem("Choose one of your own monsters to swap.");
        if (theirs == null || theirs.OwnerId == me) return Problem("That monster can't be swapped.");

        Guid friend = theirs.OwnerId;
        if (!await SocialEndpoints.AreFriends(db, me, friend, cancel) || await SocialEndpoints.IsBlockedEitherWay(db, me, friend, cancel))
        {
            return Problem("You can only swap with friends.");
        }

        string? problem = await CheckMonsters(db, mine, theirs, now, cancel);
        if (problem != null) return Problem(problem);
        if (await NextSwap(db, me, now, cancel) is DateTime next) return Problem("You've had today's swap. You can swap again " + When(next, now) + ".");

        if (await db.MonsterSwaps.CountAsync(s => s.FromUserId == me && s.Status == SwapStatus.Pending, cancel) >= MaxOpen)
        {
            return Problem($"You already have {MaxOpen} swap requests waiting for an answer.");
        }

        if (await db.MonsterSwaps.AnyAsync(s => s.FromUserId == me && s.FromMonsterId == mine.Id && s.Status == SwapStatus.Pending, cancel))
        {
            return Problem("That monster is already in one of your swap requests.");
        }

        var swap = new MonsterSwap { FromUserId = me, ToUserId = friend, FromMonsterId = mine.Id, ToMonsterId = theirs.Id, CreatedUtc = now, ExpiresUtc = now + Lifetime };
        db.MonsterSwaps.Add(swap);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await ToDto(db, swap, me, cancel));
    }

    private static async Task<IResult> MySwaps(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await ExpireOld(db, now, cancel);

        DateTime recent = now.AddDays(-7);
        List<MonsterSwap> swaps = await db.MonsterSwaps.AsNoTracking()
            .Where(s => (s.FromUserId == me || s.ToUserId == me) && (s.Status == SwapStatus.Pending || s.CreatedUtc > recent))
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync(cancel);

        var list = new List<SwapDto>();
        foreach (MonsterSwap s in swaps) list.Add(await ToDto(db, s, me, cancel));

        DateTime settledSince = now - MonsterOffers.SettleIn;
        List<Guid> settling = await db.WalkingMonsters.AsNoTracking().Where(m => m.OwnerId == me && m.AcquiredUtc > settledSince).Select(m => m.Id).ToListAsync(cancel);
        return Results.Ok(new SwapListDto(list, await NextSwap(db, me, now, cancel), settling));
    }

    private static async Task<IResult> Accept(Guid swapId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await ExpireOld(db, now, cancel);

        MonsterSwap? swap = await db.MonsterSwaps.FirstOrDefaultAsync(s => s.Id == swapId && s.ToUserId == me, cancel);
        if (swap == null) return Results.NotFound();
        if (swap.Status != SwapStatus.Pending) return Problem("That swap isn't open any more.");

        WalkingMonster? mine = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == swap.ToMonsterId, cancel);
        WalkingMonster? theirs = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == swap.FromMonsterId, cancel);
        if (mine == null || mine.OwnerId != me) return Problem("That monster isn't with you any more.");
        if (theirs == null || theirs.OwnerId != swap.FromUserId) return Problem("Your friend's monster isn't with them any more.");
        if (!await SocialEndpoints.AreFriends(db, me, swap.FromUserId, cancel) || await SocialEndpoints.IsBlockedEitherWay(db, me, swap.FromUserId, cancel))
        {
            return Problem("You can only swap with friends.");
        }

        string? problem = await CheckMonsters(db, mine, theirs, now, cancel);
        if (problem != null) return Problem(problem);
        if (await NextSwap(db, me, now, cancel) is DateTime mineNext) return Problem("You've had today's swap. You can swap again " + When(mineNext, now) + ".");
        if (await NextSwap(db, swap.FromUserId, now, cancel) is DateTime theirNext) return Problem("Your friend has had their swap today. Try again " + When(theirNext, now) + ".");

        // Both monsters - level, training and all - go to their new walkers in one step.
        swap.Status = SwapStatus.Done;
        swap.DecidedUtc = now;
        await MonsterHandover.Move(db, mine, swap.FromUserId, now, null, swap.Id, null, cancel);
        await MonsterHandover.Move(db, theirs, me, now, null, swap.Id, null, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await ToDto(db, swap, me, cancel));
    }

    private static async Task<IResult> Close(Guid swapId, SwapStatus status, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        await ExpireOld(db, now, cancel);

        // The friend declines; the one who asked cancels.
        MonsterSwap? swap = await db.MonsterSwaps.FirstOrDefaultAsync(s => s.Id == swapId &&
            (status == SwapStatus.Declined ? s.ToUserId == me : s.FromUserId == me), cancel);
        if (swap == null) return Results.NotFound();
        if (swap.Status != SwapStatus.Pending) return Problem("That swap isn't open any more.");

        swap.Status = status;
        swap.DecidedUtc = now;
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await ToDto(db, swap, me, cancel));
    }

    // Neither may be its walker's companion, and both must have settled in with their walkers ("mine" = the caller's).
    private static async Task<string?> CheckMonsters(AppDbContext db, WalkingMonster mine, WalkingMonster theirs, DateTime now, CancellationToken cancel)
    {
        WalkingProfile myProfile = await WalkingEndpoints.GetOrCreateProfile(db, mine.OwnerId, cancel);
        WalkingProfile theirProfile = await WalkingEndpoints.GetOrCreateProfile(db, theirs.OwnerId, cancel);
        if (myProfile.CompanionMonsterId == mine.Id) return "That's your companion - the monster you walk with. Choose a different companion first if you'd like to swap it.";
        if (theirProfile.CompanionMonsterId == theirs.Id) return "That's their companion - the monster they walk with. It doesn't swap.";
        if (now < mine.AcquiredUtc + MonsterOffers.SettleIn) return "Your monster is still settling in with you. You can swap it " + When(mine.AcquiredUtc + MonsterOffers.SettleIn, now) + ".";
        if (now < theirs.AcquiredUtc + MonsterOffers.SettleIn) return "That monster is still settling in with its walker. Try again in a few days.";
        return null;
    }

    /// <summary>When this walker can next swap (24 hours after their last swap), or null if they can now.</summary>
    public static async Task<DateTime?> NextSwap(AppDbContext db, Guid user, DateTime now, CancellationToken cancel)
    {
        DateTime? last = await db.MonsterSwaps
            .Where(s => s.Status == SwapStatus.Done && (s.FromUserId == user || s.ToUserId == user))
            .MaxAsync(s => s.DecidedUtc, cancel);
        return last is DateTime at && at + Cooldown > now ? at + Cooldown : null;
    }

    // Swap requests nobody answered in time are closed.
    private static async Task ExpireOld(AppDbContext db, DateTime now, CancellationToken cancel)
    {
        List<MonsterSwap> stale = await db.MonsterSwaps.Where(s => s.Status == SwapStatus.Pending && s.ExpiresUtc < now).ToListAsync(cancel);
        if (stale.Count == 0) return;
        foreach (MonsterSwap s in stale)
        {
            s.Status = SwapStatus.Expired;
            s.DecidedUtc = now;
        }
        await db.SaveChangesAsync(cancel);
    }

    // "in 5 hours", "in 20 minutes", "in 3 days".
    private static string When(DateTime at, DateTime now)
    {
        TimeSpan wait = at - now;
        if (wait.TotalHours >= 24)
        {
            int days = (int)Math.Ceiling(wait.TotalDays);
            return "in " + days + (days == 1 ? " day" : " days");
        }
        if (wait.TotalHours >= 1)
        {
            int hours = (int)Math.Ceiling(wait.TotalHours);
            return "in " + hours + (hours == 1 ? " hour" : " hours");
        }
        int minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));
        return "in " + minutes + (minutes == 1 ? " minute" : " minutes");
    }

    private static async Task<SwapDto> ToDto(AppDbContext db, MonsterSwap s, Guid me, CancellationToken cancel)
    {
        bool incoming = s.ToUserId == me;
        Guid other = incoming ? s.FromUserId : s.ToUserId;
        Guid mineId = incoming ? s.ToMonsterId : s.FromMonsterId;
        Guid theirsId = incoming ? s.FromMonsterId : s.ToMonsterId;
        WalkingMonster? mine = await db.WalkingMonsters.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mineId, cancel);
        WalkingMonster? theirs = await db.WalkingMonsters.AsNoTracking().FirstOrDefaultAsync(x => x.Id == theirsId, cancel);
        string name = await db.Users.Where(u => u.Id == other).Select(u => u.Username).FirstOrDefaultAsync(cancel) ?? "A friend";
        return new SwapDto(s.Id, s.Status.ToString(), incoming, other, name,
            mine == null ? null : MonsterEndpoints.ToDto(mine, null), theirs == null ? null : MonsterEndpoints.ToDto(theirs, null),
            s.CreatedUtc, s.ExpiresUtc, s.DecidedUtc);
    }

    private static IResult Problem(string message) => Results.Problem(message, statusCode: StatusCodes.Status400BadRequest);
}
