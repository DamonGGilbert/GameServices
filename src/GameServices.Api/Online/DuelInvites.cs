using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Online;

public record DuelInviteRequest(string Username);

/// <param name="RoomId">The room to join. Accepting is just "join this room".</param>
public record DuelInviteDto(Guid RoomId, string FromUsername, DateTime SentUtc, int SecondsLeft);

/// <summary>
/// DUEL INVITES: "Come and duel me!" from a friend.
///
/// An invite is a note that points at a room. The sender's room is created (or reused) when they invite, so
/// accepting needs nothing new: the friend simply joins that room with the normal join call. Invites live in
/// memory, like rooms, and are forgotten after three minutes, or when the room fills up, closes or is left.
/// </summary>
public class DuelInviteRegistry
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);
    public const int MaxPendingPerPlayer = 10;

    private record Invite(Guid RoomId, Guid FromUserId, string FromUsername, Guid ToUserId, DateTime SentUtc);

    private readonly object gate = new();
    private readonly List<Invite> invites = new();

    /// <summary>Records an invite. Inviting the same friend again just refreshes the old one.</summary>
    public void Add(Guid roomId, Guid fromUserId, string fromUsername, Guid toUserId)
    {
        lock (gate)
        {
            invites.RemoveAll(i => i.FromUserId == fromUserId && i.ToUserId == toUserId);

            // Nobody gets flooded: when they already have plenty waiting, the oldest goes.
            while (invites.Count(i => i.ToUserId == toUserId) >= MaxPendingPerPlayer)
            {
                invites.Remove(invites.Where(i => i.ToUserId == toUserId).OrderBy(i => i.SentUtc).First());
            }

            invites.Add(new Invite(roomId, fromUserId, fromUsername, toUserId, DateTime.UtcNow));
        }
    }

    /// <summary>The invites that can still be accepted: not too old, and the room is still waiting for a second player.</summary>
    public List<DuelInviteDto> For(Guid userId, RoomRegistry rooms)
    {
        lock (gate)
        {
            DateTime now = DateTime.UtcNow;

            invites.RemoveAll(i => now - i.SentUtc > Lifetime || !Usable(i, rooms));

            return invites.Where(i => i.ToUserId == userId)
                .OrderByDescending(i => i.SentUtc)
                .Select(i => new DuelInviteDto(i.RoomId, i.FromUsername, i.SentUtc, Math.Max(0, (int)(Lifetime - (now - i.SentUtc)).TotalSeconds)))
                .ToList();
        }
    }

    public void Remove(Guid userId, Guid roomId)
    {
        lock (gate)
        {
            invites.RemoveAll(i => i.ToUserId == userId && i.RoomId == roomId);
        }
    }

    private static bool Usable(Invite invite, RoomRegistry rooms)
    {
        Room? room = rooms.Get(invite.RoomId);

        return room != null
            && room.Status == RoomStatus.Waiting
            && !room.IsFull
            && room.Seats[0]?.UserId == invite.FromUserId;
    }
}

public static partial class OnlineEndpoints
{
    private static void MapDuelInvites(IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/duel-invites").WithTags("Online").RequireAuthorization();
        group.MapPost("", SendDuelInvite);
        group.MapGet("", MyDuelInvites);
        group.MapPost("/{roomId:guid}/decline", DeclineDuelInvite);
    }

    private static async Task<IResult> SendDuelInvite(DuelInviteRequest request, ClaimsPrincipal principal, AppDbContext db, RoomRegistry registry, DuelInviteRegistry invites, CancellationToken cancel)
    {
        if (!TryUser(principal, out Guid me)) return Results.Unauthorized();

        string normalized = (request?.Username ?? "").Trim().ToUpperInvariant();
        User? target = normalized.Length == 0 ? null : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancel);

        // Same answer whether they are not a friend, do not exist or have blocked you, so nothing leaks.
        if (target == null || target.Id == me
            || !await SocialEndpoints.AreFriends(db, me, target.Id, cancel)
            || await SocialEndpoints.IsBlockedEitherWay(db, me, target.Id, cancel))
        {
            return Results.Problem("You can only invite your friends.", statusCode: StatusCodes.Status404NotFound);
        }

        await Presence.Touch(db, me, cancel);

        bool online = target.LastSeenUtc != null && DateTime.UtcNow - target.LastSeenUtc.Value < SocialEndpoints.OnlineWindow;

        if (!online)
        {
            return Results.Problem(target.Username + " isn't online right now.", statusCode: StatusCodes.Status409Conflict);
        }

        if (registry.ByUser(target.Id) != null)
        {
            return Results.Problem(target.Username + " is already in a duel.", statusCode: StatusCodes.Status409Conflict);
        }

        RoomSeat? seat = await MakeSeat(db, me, cancel);

        if (seat == null) return Results.Unauthorized();

        // Use the room you are already waiting in, or make a fresh one.
        Room? room = registry.ByUser(me);

        if (room == null)
        {
            room = registry.Create(seat, "Friend duel", false, out string? problem);

            if (room == null)
            {
                return Results.Problem(problem, statusCode: StatusCodes.Status409Conflict);
            }
        }
        else if (room.Status != RoomStatus.Waiting || room.IsFull || room.GuildMatch || room.Seats[0]?.UserId != me)
        {
            return Results.Problem("Finish or leave your current room before you invite someone.", statusCode: StatusCodes.Status409Conflict);
        }

        invites.Add(room.Id, me, seat.Username, target.Id);
        return Results.Ok(RoomRegistry.ToDto(room, me));
    }

    private static async Task<IResult> MyDuelInvites(ClaimsPrincipal principal, AppDbContext db, RoomRegistry registry, DuelInviteRegistry invites, CancellationToken cancel)
    {
        if (!TryUser(principal, out Guid me)) return Results.Unauthorized();

        await Presence.Touch(db, me, cancel);
        return Results.Ok(invites.For(me, registry));
    }

    private static IResult DeclineDuelInvite(Guid roomId, ClaimsPrincipal principal, DuelInviteRegistry invites)
    {
        if (!TryUser(principal, out Guid me)) return Results.Unauthorized();

        invites.Remove(me, roomId);
        return Results.NoContent();
    }
}
