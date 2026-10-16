using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.RegularExpressions;
using GameServices.Api.Catalog;
using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace GameServices.Api.Online;

public record CreateRoomRequest(string? Name, bool GuildMatch);

public record DeckRequest(List<string>? Main, List<string>? Extra);

public record GuildRankDto(int Rank, Guid GuildId, string Name, string Tag, int Rating, int Wins, int Losses, int Draws, int Members, int Tier = 0, string TierName = "Rookie", int WarWins = 0, int BannerStyle = 0);

public record GuildLeaderboard(List<GuildRankDto> Items, int MyRank, Guid MyGuildId);

public record MatchHistoryDto(string Opponent, bool GuildMatch, string Result, string Reason, int Turns, int GuildDelta, DateTime EndedUtc);

/// <summary>
/// THE ROOM LIST AND THE DUEL SOCKETS.
///
///   GET  /api/rooms                 every open room (create one, or join one)
///   POST /api/rooms                 make a room      POST /api/rooms/{id}/join      sit down
///   GET  /api/rooms/mine            my room (poll this while waiting)
///   POST /api/rooms/mine/deck       choose a deck: it is checked against the cards you OWN
///   POST /api/rooms/leave           stand up (in a running duel this is a surrender)
///
///   WS   /ws/duel                   a phone plays its duel here
///   WS   /ws/host                   a duel host (this PC only) runs the duel here
///
///   GET  /api/guilds/leaderboard    guilds ranked by their Guild Match rating
///   GET  /api/matches/mine          my last duels
/// </summary>
public static partial class OnlineEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9 '!._-]{3,24}$")]
    private static partial Regex RoomNameRule();

    public static IEndpointRouteBuilder MapOnlineEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder rooms = app.MapGroup("/api/rooms").WithTags("Online").RequireAuthorization();
        rooms.MapGet("", ListRooms);
        rooms.MapPost("", CreateRoom);
        rooms.MapGet("/mine", MyRoom);
        rooms.MapPost("/{roomId:guid}/join", JoinRoom);
        rooms.MapPost("/leave", LeaveRoom);
        rooms.MapPost("/mine/deck", SetDeck);

        app.MapGet("/api/guilds/leaderboard", Leaderboard).WithTags("Online").RequireAuthorization();
        app.MapGet("/api/matches/mine", MyMatches).WithTags("Online").RequireAuthorization();

        app.Map("/ws/duel", PhoneSocket).RequireAuthorization();
        app.Map("/ws/host", HostSocket).AllowAnonymous();
        return app;
    }

    private static bool TryUser(ClaimsPrincipal principal, out Guid userId)
        => Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out userId);

    // ------------------------------------------------------------------ rooms

    private static IResult ListRooms(ClaimsPrincipal principal, RoomRegistry registry)
    {
        TryUser(principal, out Guid me);
        return Results.Ok(registry.Snapshot().Select(r => RoomRegistry.ToDto(r, me)).ToList());
    }

    private static async Task<RoomSeat?> MakeSeat(AppDbContext db, Guid userId, CancellationToken cancel)
    {
        var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Username, u.ShowLevel, u.ShowPrestige, u.ShowEmblem, u.ShowFrame, u.ShowTitle, u.CosmeticsJson, u.ProfileJson }).FirstOrDefaultAsync(cancel);

        if (user == null)
        {
            return null;
        }

        string name = user.Username;

        var guild = await (from m in db.GuildMembers.AsNoTracking()
                           join g in db.Guilds.AsNoTracking() on m.GuildId equals g.Id
                           where m.UserId == userId
                           select new { g.Id, g.Tag, g.Name }).FirstOrDefaultAsync(cancel);

        return new RoomSeat { UserId = userId, Username = name, GuildId = guild?.Id, GuildTag = guild?.Tag ?? "", GuildName = guild?.Name ?? "", Showcase = new GameServices.Api.Social.ShowcaseDto(user.ShowLevel, user.ShowPrestige, user.ShowEmblem, user.ShowFrame, user.ShowTitle), Cosmetics = user.CosmeticsJson, Greeting = GameServices.Api.Social.ProfileCardEndpoints.Read(user.ProfileJson).Greeting };
    }

    private static async Task<IResult> CreateRoom(CreateRoomRequest request, ClaimsPrincipal principal, AppDbContext db, RoomRegistry registry, CancellationToken cancel)
    {
        if (!TryUser(principal, out Guid me)) return Results.Unauthorized();

        RoomSeat? seat = await MakeSeat(db, me, cancel);

        if (seat == null) return Results.Unauthorized();

        string name = (request?.Name ?? "").Trim();

        if (name.Length == 0)
        {
            name = seat.Username + "'s room";
        }

        if (!RoomNameRule().IsMatch(name))
        {
            return Results.Problem("Room names are 3-24 letters, numbers and spaces.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request?.GuildMatch == true && seat.GuildId == null)
        {
            return Results.Problem("Guild Matches are only for players in a guild.", statusCode: StatusCodes.Status400BadRequest);
        }

        Room? room = registry.Create(seat, name, request?.GuildMatch == true, out string? problem);

        return room == null
            ? Results.Problem(problem, statusCode: StatusCodes.Status409Conflict)
            : Results.Ok(RoomRegistry.ToDto(room, me));
    }

    private static IResult MyRoom(ClaimsPrincipal principal, RoomRegistry registry)
    {
        if (!TryUser(principal, out Guid me)) return Results.Unauthorized();

        Room? room = registry.ByUser(me);

        return room == null
            ? Results.Problem("You aren't in a room.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(RoomRegistry.ToDto(room, me));
    }

    private static async Task<IResult> JoinRoom(Guid roomId, ClaimsPrincipal principal, AppDbContext db, RoomRegistry registry, CancellationToken cancel)
    {
        if (!TryUser(principal, out Guid me)) return Results.Unauthorized();

        RoomSeat? seat = await MakeSeat(db, me, cancel);

        if (seat == null) return Results.Unauthorized();

        if (!registry.Join(roomId, seat, out string? problem, out Room? room))
        {
            return Results.Problem(problem, statusCode: room == null ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict);
        }

        return Results.Ok(RoomRegistry.ToDto(room!, me));
    }

    private static async Task<IResult> LeaveRoom(ClaimsPrincipal principal, RoomRegistry registry, MatchCoordinator coordinator)
    {
        if (!TryUser(principal, out Guid me)) return Results.Unauthorized();

        Room? room = registry.ByUser(me);

        if (room == null)
        {
            return Results.NoContent();
        }

        // Leaving a duel that has started means giving up.
        if (room.Status is RoomStatus.Starting or RoomStatus.InMatch)
        {
            await coordinator.SurrenderAsync(room, room.SeatOf(me));
            return Results.NoContent();
        }

        registry.Leave(me);
        return Results.NoContent();
    }

    private static async Task<IResult> SetDeck(DeckRequest request, ClaimsPrincipal principal, AppDbContext db, RoomRegistry registry, CardCatalog catalog, MatchCoordinator coordinator, Microsoft.Extensions.Options.IOptions<OnlineOptions> options, CancellationToken cancel)
    {
        if (!TryUser(principal, out Guid me)) return Results.Unauthorized();

        Room? room = registry.ByUser(me);

        if (room == null)
        {
            return Results.Problem("You aren't in a room.", statusCode: StatusCodes.Status404NotFound);
        }

        if (room.Status != RoomStatus.Waiting)
        {
            return Results.Problem("The duel has already started.", statusCode: StatusCodes.Status409Conflict);
        }

        DeckList deck = new(request?.Main ?? new List<string>(), request?.Extra ?? new List<string>());

        Dictionary<string, int> owned = await db.OwnedCards.AsNoTracking()
            .Where(c => c.UserId == me)
            .ToDictionaryAsync(c => c.CardId, c => c.Quantity, cancel);

        if (!options.Value.RequireOwnedCards)
        {
            owned = deck.Main.Concat(deck.Extra).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        }

        List<string> problems = DeckValidator.Check(deck, catalog, owned);

        if (problems.Count > 0)
        {
            return Results.Problem(string.Join(" ", problems), statusCode: StatusCodes.Status400BadRequest);
        }

        RoomSeat seat = room.Seats[room.SeatOf(me)]!;
        seat.Deck = deck;
        room.LastActivityUtc = DateTime.UtcNow;
        room.Error = null;

        if (room.BothReady && room.GuildMatch && room.Seats[0]!.GuildId == room.Seats[1]!.GuildId)
        {
            seat.Deck = null;
            return Results.Problem("A Guild Match is played against ANOTHER guild.", statusCode: StatusCodes.Status409Conflict);
        }

        if (room.BothReady)
        {
            // The room's Error field says what went wrong if the duel could not start.
            await coordinator.StartAsync(room);
        }

        return Results.Ok(RoomRegistry.ToDto(room, me));
    }

    // ------------------------------------------------------------------ guild leaderboard and history

    private static async Task<IResult> Leaderboard(int? top, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        TryUser(principal, out Guid me);
        int count = Math.Clamp(top ?? 50, 1, 200);

        var rows = await db.Guilds.AsNoTracking()
            .Select(g => new
            {
                g.Id, g.Name, g.Tag, g.Rating, g.Wins, g.Losses, g.Draws, g.WarWins, g.BannerStyle,
                Members = db.GuildMembers.Count(m => m.GuildId == g.Id)
            })
            .ToListAsync(cancel);

        // Best first: rating, then wins, then fewer losses, then name (so the order never jumps around).
        var ranked = rows
            .OrderByDescending(g => g.Rating).ThenByDescending(g => g.Wins).ThenBy(g => g.Losses).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select((g, i) => new GuildRankDto(i + 1, g.Id, g.Name, g.Tag, g.Rating, g.Wins, g.Losses, g.Draws, g.Members,
                GameServices.Api.Guilds.GuildTiers.TierOf(g.Rating, g.WarWins), GameServices.Api.Guilds.GuildTiers.NameOf(GameServices.Api.Guilds.GuildTiers.TierOf(g.Rating, g.WarWins)), g.WarWins, g.BannerStyle))
            .ToList();

        Guid myGuild = await db.GuildMembers.AsNoTracking().Where(m => m.UserId == me).Select(m => m.GuildId).FirstOrDefaultAsync(cancel);
        int myRank = myGuild == Guid.Empty ? 0 : ranked.FirstOrDefault(g => g.GuildId == myGuild)?.Rank ?? 0;

        return Results.Ok(new GuildLeaderboard(ranked.Take(count).ToList(), myRank, myGuild));
    }

    private static async Task<IResult> MyMatches(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!TryUser(principal, out Guid me)) return Results.Unauthorized();

        List<DuelMatch> rows = await db.DuelMatches.AsNoTracking()
            .Where(m => m.Player0Id == me || m.Player1Id == me)
            .OrderByDescending(m => m.EndedUtc)
            .Take(20)
            .ToListAsync(cancel);

        List<MatchHistoryDto> list = rows.Select(m =>
        {
            int seat = m.Player0Id == me ? 0 : 1;
            string result = m.WinnerSeat < 0 ? "draw" : m.WinnerSeat == seat ? "win" : "loss";
            return new MatchHistoryDto(seat == 0 ? m.Player1Name : m.Player0Name, m.GuildMatch, result, m.Reason, m.Turns, seat == 0 ? m.Guild0Delta : m.Guild1Delta, m.EndedUtc);
        }).ToList();

        return Results.Ok(list);
    }

    // ------------------------------------------------------------------ sockets

    private static async Task PhoneSocket(HttpContext http, MatchCoordinator coordinator)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!TryUser(http.User, out Guid me))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync();
        await coordinator.RunPhoneAsync(socket, me);
    }

    private static async Task HostSocket(HttpContext http, MatchCoordinator coordinator, Microsoft.Extensions.Options.IOptions<OnlineOptions> options)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Only a program on THIS machine may be a duel host, and never through a tunnel / proxy.
        bool local = !options.Value.HostMustBeLocal || (http.Connection.RemoteIpAddress != null && System.Net.IPAddress.IsLoopback(http.Connection.RemoteIpAddress));
        bool proxied = http.Request.Headers.ContainsKey("X-Forwarded-For") || http.Request.Headers.ContainsKey("Forwarded");

        if (!local || proxied)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        string matchId = http.Request.Query["matchId"].ToString();
        string secret = http.Request.Query["secret"].ToString();

        using WebSocket socket = await http.WebSockets.AcceptWebSocketAsync();
        await coordinator.RunHostAsync(socket, matchId, secret);
    }
}
