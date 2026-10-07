using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Chat;

public record SendMessageRequest(string Text);

public record ChatMessageDto(long Id, Guid FromUserId, Guid ToUserId, string Text, DateTime SentUtc);

/// <summary>
/// REAL-TIME PUSH. A game that connects to /hubs/chat (SignalR: WebSockets with fallbacks) is told
/// the instant a message arrives, instead of having to keep asking "anything new?".
/// The hub only listens: messages are SENT through the normal REST endpoint below, so every
/// message goes through the same validation, whoever sends it.
/// </summary>
[Authorize]
public class ChatHub : Hub
{
    public static string Group(Guid userId) => "user:" + userId;

    public override async Task OnConnectedAsync()
    {
        // Each connection joins a private "room" named after its owner. The server pushes to the room.
        if (SocialEndpoints.TryGetUserId(Context.User!, out Guid me))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Group(me));
        }

        await base.OnConnectedAsync();
    }
}

/// <summary>
/// DIRECT MESSAGES between friends.
///   POST /api/chat/{friendId}               send a message
///   GET  /api/chat/{friendId}?afterId=N     messages after id N (omit for the latest 50); marks them read
/// </summary>
public static class ChatEndpoints
{
    public const int MaxLength = 500;
    public const int MaxMessagesPer10Seconds = 10;

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/chat/{friendId:guid}").WithTags("Chat").RequireAuthorization();
        group.MapPost("", Send);
        group.MapGet("", History);
        return app;
    }

    private static async Task<IResult> Send(Guid friendId, SendMessageRequest request, ClaimsPrincipal principal, AppDbContext db, IHubContext<ChatHub> hub, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        string text = (request?.Text ?? "").Trim();

        if (text.Length == 0 || text.Length > MaxLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = new[] { $"A message must be 1-{MaxLength} characters." } });
        }

        // Only friends can talk. (Blocking removes the friendship, so blocked players can't either.)
        if (!await SocialEndpoints.AreFriends(db, me, friendId, cancel) || await SocialEndpoints.IsBlockedEitherWay(db, me, friendId, cancel))
        {
            return Results.Problem("You can only message your friends.", statusCode: StatusCodes.Status403Forbidden);
        }

        // Anti-spam: a person can't type 10 messages in 10 seconds, a script can.
        DateTime windowStart = DateTime.UtcNow.AddSeconds(-10);

        if (await db.ChatMessages.CountAsync(m => m.SenderId == me && m.SentUtc > windowStart, cancel) >= MaxMessagesPer10Seconds)
        {
            return Results.Problem("Slow down! You're sending messages too fast.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        ChatMessage message = new() { SenderId = me, RecipientId = friendId, Text = text };
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync(cancel);
        await Presence.Touch(db, me, cancel);

        ChatMessageDto dto = ToDto(message);

        // Push to every connected device of the recipient.
        await hub.Clients.Group(ChatHub.Group(friendId)).SendAsync("ReceiveMessage", dto, cancel);

        return Results.Ok(dto);
    }

    private static async Task<IResult> History(Guid friendId, long? afterId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!await SocialEndpoints.AreFriends(db, me, friendId, cancel))
        {
            return Results.Problem("You can only read chats with your friends.", statusCode: StatusCodes.Status403Forbidden);
        }

        await Presence.Touch(db, me, cancel);

        IQueryable<ChatMessage> conversation = db.ChatMessages
            .Where(m => (m.SenderId == me && m.RecipientId == friendId) || (m.SenderId == friendId && m.RecipientId == me));

        List<ChatMessage> messages;

        if (afterId.HasValue)
        {
            messages = await conversation.Where(m => m.Id > afterId.Value).OrderBy(m => m.Id).Take(100).ToListAsync(cancel);
        }
        else
        {
            messages = (await conversation.OrderByDescending(m => m.Id).Take(50).ToListAsync(cancel)).OrderBy(m => m.Id).ToList();
        }

        // Reading the chat marks their messages as read.
        DateTime now = DateTime.UtcNow;
        bool changed = false;

        foreach (ChatMessage message in messages.Where(m => m.RecipientId == me && m.ReadUtc == null))
        {
            message.ReadUtc = now;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancel);
        }

        return Results.Ok(messages.Select(ToDto).ToList());
    }

    private static ChatMessageDto ToDto(ChatMessage m) => new(m.Id, m.SenderId, m.RecipientId, m.Text, m.SentUtc);
}
