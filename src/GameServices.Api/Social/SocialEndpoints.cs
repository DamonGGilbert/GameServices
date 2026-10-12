using System.Security.Claims;
using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace GameServices.Api.Social;

public record UsernameRequest(string Username);

public record FriendDto(Guid UserId, string Username, bool IsOnline, DateTime? LastSeenUtc, int Unread, DateTime SinceUtc, ShowcaseDto Showcase);

public record RequestDto(Guid UserId, string Username, DateTime SentUtc);

public record FriendsResponse(List<FriendDto> Friends, List<RequestDto> Incoming, List<RequestDto> Outgoing);

public record BlockedDto(Guid UserId, string Username);

/// <summary>
/// FRIENDS AND BLOCKING.
///
///   GET    /api/friends                          my friends + pending requests
///   POST   /api/friends/requests                 send a request by username (or accept theirs)
///   POST   /api/friends/requests/{id}/accept     accept a request they sent me
///   POST   /api/friends/requests/{id}/decline    decline it
///   DELETE /api/friends/requests/{id}            cancel a request I sent
///   DELETE /api/friends/{id}                     unfriend
///   GET    /api/blocks   POST /api/blocks   DELETE /api/blocks/{id}
///
/// Privacy rule: if either player has blocked the other, requests look exactly like "no such
/// player". Nobody can tell they've been blocked.
/// </summary>
public static class SocialEndpoints
{
    public const int MaxPendingOutgoing = 50;
    public const int MaxFriends = 200;
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    public static IEndpointRouteBuilder MapSocialEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder friends = app.MapGroup("/api/friends").WithTags("Friends").RequireAuthorization();
        friends.MapGet("", GetFriends);
        friends.MapPost("/requests", SendRequest);
        friends.MapPost("/requests/{userId:guid}/accept", Accept);
        friends.MapPost("/requests/{userId:guid}/decline", Decline);
        friends.MapDelete("/requests/{userId:guid}", CancelRequest);
        friends.MapDelete("/{userId:guid}", Unfriend);

        RouteGroupBuilder blocks = app.MapGroup("/api/blocks").WithTags("Blocking").RequireAuthorization();
        blocks.MapGet("", GetBlocks);
        blocks.MapPost("", Block);
        blocks.MapDelete("/{userId:guid}", Unblock);

        return app;
    }

    // ------------------------------------------------------------------ list

    private static async Task<IResult> GetFriends(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        await Presence.Touch(db, me, cancel);

        List<Friendship> rows = await db.Friendships.AsNoTracking()
            .Where(f => f.UserAId == me || f.UserBId == me)
            .ToListAsync(cancel);

        List<Guid> otherIds = rows.Select(f => f.OtherThan(me)).ToList();
        Dictionary<Guid, User> users = await db.Users.AsNoTracking()
            .Where(u => otherIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, cancel);

        Dictionary<Guid, int> unread = await db.ChatMessages.AsNoTracking()
            .Where(m => m.RecipientId == me && m.ReadUtc == null)
            .GroupBy(m => m.SenderId)
            .Select(g => new { Sender = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Sender, x => x.Count, cancel);

        DateTime now = DateTime.UtcNow;

        List<FriendDto> friends = rows.Where(f => f.Status == FriendshipStatus.Accepted && users.ContainsKey(f.OtherThan(me)))
            .Select(f =>
            {
                User other = users[f.OtherThan(me)];
                bool online = other.LastSeenUtc != null && now - other.LastSeenUtc.Value < OnlineWindow;
                return new FriendDto(other.Id, other.Username, online, other.LastSeenUtc, unread.GetValueOrDefault(other.Id), f.AcceptedUtc ?? f.CreatedUtc, ShowcaseEndpoints.Of(other));
            })
            .OrderByDescending(f => f.IsOnline).ThenBy(f => f.Username)
            .ToList();

        List<RequestDto> incoming = rows.Where(f => f.Status == FriendshipStatus.Pending && f.RequesterId != me && users.ContainsKey(f.RequesterId))
            .Select(f => new RequestDto(f.RequesterId, users[f.RequesterId].Username, f.CreatedUtc))
            .OrderBy(r => r.SentUtc).ToList();

        List<RequestDto> outgoing = rows.Where(f => f.Status == FriendshipStatus.Pending && f.RequesterId == me && users.ContainsKey(f.OtherThan(me)))
            .Select(f => new RequestDto(f.OtherThan(me), users[f.OtherThan(me)].Username, f.CreatedUtc))
            .OrderBy(r => r.SentUtc).ToList();

        return Results.Ok(new FriendsResponse(friends, incoming, outgoing));
    }

    // ------------------------------------------------------------------ requests

    private static async Task<IResult> SendRequest(UsernameRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        string normalized = (request?.Username ?? "").Trim().ToUpperInvariant();

        if (normalized.Length == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["username"] = new[] { "Type the name of the player." } });
        }

        User? target = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancel);

        // Blocked in either direction looks just like "doesn't exist".
        if (target == null || await IsBlockedEitherWay(db, me, target.Id, cancel))
        {
            return Results.Problem("No player with that name.", statusCode: StatusCodes.Status404NotFound);
        }

        if (target.Id == me)
        {
            return Results.Problem("You can't add yourself.", statusCode: StatusCodes.Status400BadRequest);
        }

        (Guid a, Guid b) = Pair(me, target.Id);
        Friendship? existing = await db.Friendships.FirstOrDefaultAsync(f => f.UserAId == a && f.UserBId == b, cancel);

        if (existing != null)
        {
            if (existing.Status == FriendshipStatus.Accepted)
            {
                return Results.Problem($"You and {target.Username} are already friends.", statusCode: StatusCodes.Status409Conflict);
            }

            if (existing.RequesterId == me)
            {
                return Results.Problem("You already sent them a request.", statusCode: StatusCodes.Status409Conflict);
            }

            // They had already asked YOU: sending a request back means "yes".
            existing.Status = FriendshipStatus.Accepted;
            existing.AcceptedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancel);
            return Results.Ok(new { status = "friends", username = target.Username });
        }

        if (await db.Friendships.CountAsync(f => f.RequesterId == me && f.Status == FriendshipStatus.Pending, cancel) >= MaxPendingOutgoing)
        {
            return Results.Problem("You have too many requests waiting. Wait for some to be answered.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (await db.Friendships.CountAsync(f => (f.UserAId == me || f.UserBId == me) && f.Status == FriendshipStatus.Accepted, cancel) >= MaxFriends)
        {
            return Results.Problem($"Your friends list is full ({MaxFriends}).", statusCode: StatusCodes.Status409Conflict);
        }

        db.Friendships.Add(new Friendship { UserAId = a, UserBId = b, RequesterId = me, Status = FriendshipStatus.Pending });

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            // Both players sent a request at the very same moment; the database let only one row in.
            return Results.Problem("A request between you two already exists.", statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new { status = "requested", username = target.Username });
    }

    private static async Task<IResult> Accept(Guid userId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        Friendship? pending = await FindPendingFrom(db, userId, me, cancel);

        if (pending == null || await IsBlockedEitherWay(db, me, userId, cancel))
        {
            return Results.Problem("There is no request from that player.", statusCode: StatusCodes.Status404NotFound);
        }

        pending.Status = FriendshipStatus.Accepted;
        pending.AcceptedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static async Task<IResult> Decline(Guid userId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        Friendship? pending = await FindPendingFrom(db, userId, me, cancel);

        if (pending == null)
        {
            return Results.Problem("There is no request from that player.", statusCode: StatusCodes.Status404NotFound);
        }

        db.Friendships.Remove(pending);
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static async Task<IResult> CancelRequest(Guid userId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        Friendship? pending = await FindPendingFrom(db, me, userId, cancel);

        if (pending == null)
        {
            return Results.Problem("You have no request to that player.", statusCode: StatusCodes.Status404NotFound);
        }

        db.Friendships.Remove(pending);
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static async Task<IResult> Unfriend(Guid userId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        (Guid a, Guid b) = Pair(me, userId);
        Friendship? friendship = await db.Friendships.FirstOrDefaultAsync(f => f.UserAId == a && f.UserBId == b && f.Status == FriendshipStatus.Accepted, cancel);

        if (friendship == null)
        {
            return Results.Problem("You aren't friends with that player.", statusCode: StatusCodes.Status404NotFound);
        }

        db.Friendships.Remove(friendship);
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ blocking

    private static async Task<IResult> GetBlocks(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        List<BlockedDto> blocked = await (from b in db.Blocks.AsNoTracking()
                                          join u in db.Users.AsNoTracking() on b.BlockedId equals u.Id
                                          where b.BlockerId == me
                                          orderby u.Username
                                          select new BlockedDto(u.Id, u.Username)).ToListAsync(cancel);

        return Results.Ok(blocked);
    }

    private static async Task<IResult> Block(UsernameRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        string normalized = (request?.Username ?? "").Trim().ToUpperInvariant();
        User? target = normalized.Length == 0 ? null : await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancel);

        if (target == null)
        {
            return Results.Problem("No player with that name.", statusCode: StatusCodes.Status404NotFound);
        }

        if (target.Id == me)
        {
            return Results.Problem("You can't block yourself.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!await db.Blocks.AnyAsync(b => b.BlockerId == me && b.BlockedId == target.Id, cancel))
        {
            db.Blocks.Add(new UserBlock { BlockerId = me, BlockedId = target.Id });
        }

        // Blocking ends any friendship or pending request between you.
        (Guid a, Guid b2) = Pair(me, target.Id);
        Friendship? friendship = await db.Friendships.FirstOrDefaultAsync(f => f.UserAId == a && f.UserBId == b2, cancel);

        if (friendship != null)
        {
            db.Friendships.Remove(friendship);
        }

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            // Double-tapped: the block already exists. That's the result we wanted anyway.
        }

        return Results.Ok(new BlockedDto(target.Id, target.Username));
    }

    private static async Task<IResult> Unblock(Guid userId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        UserBlock? block = await db.Blocks.FirstOrDefaultAsync(b => b.BlockerId == me && b.BlockedId == userId, cancel);

        if (block == null)
        {
            return Results.Problem("You haven't blocked that player.", statusCode: StatusCodes.Status404NotFound);
        }

        db.Blocks.Remove(block);
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ helpers (shared with chat)

    public static (Guid A, Guid B) Pair(Guid x, Guid y) => x.CompareTo(y) < 0 ? (x, y) : (y, x);

    public static Task<bool> IsBlockedEitherWay(AppDbContext db, Guid x, Guid y, CancellationToken cancel)
        => db.Blocks.AnyAsync(b => (b.BlockerId == x && b.BlockedId == y) || (b.BlockerId == y && b.BlockedId == x), cancel);

    public static Task<bool> AreFriends(AppDbContext db, Guid x, Guid y, CancellationToken cancel)
    {
        (Guid a, Guid b) = Pair(x, y);
        return db.Friendships.AnyAsync(f => f.UserAId == a && f.UserBId == b && f.Status == FriendshipStatus.Accepted, cancel);
    }

    private static Task<Friendship?> FindPendingFrom(AppDbContext db, Guid requester, Guid addressee, CancellationToken cancel)
    {
        (Guid a, Guid b) = Pair(requester, addressee);
        return db.Friendships.FirstOrDefaultAsync(f => f.UserAId == a && f.UserBId == b && f.RequesterId == requester && f.Status == FriendshipStatus.Pending, cancel);
    }

    public static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out userId);
}

/// <summary>"Online" = the player's game talked to us in the last minute.</summary>
public static class Presence
{
    public static async Task Touch(AppDbContext db, Guid userId, CancellationToken cancel)
    {
        DateTime now = DateTime.UtcNow;

        // One cheap UPDATE, no loading the row first.
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenUtc, now), cancel);
    }
}
