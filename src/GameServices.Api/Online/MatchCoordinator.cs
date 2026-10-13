using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GameServices.Api.Catalog;
using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GameServices.Api.Online;

public class OnlineOptions
{
    public const string SectionName = "Online";

    /// <summary>Where the duel host (on this same machine) reaches this server.</summary>
    public string HostApiUrl { get; set; } = "ws://127.0.0.1:5080";

    /// <summary>The built duel host program. Empty = duels can't start (the game shows a clear message).</summary>
    public string HostExecutable { get; set; } = "";

    public string HostLogDirectory { get; set; } = "logs/duelhost";
    public int MaxMatches { get; set; } = 3;

    /// <summary>A player whose connection drops has this long to come back before they forfeit.</summary>
    public int PhoneGraceSeconds { get; set; } = 120;

    /// <summary>How long a host may take to start up and report ready.</summary>
    public int HostStartSeconds { get; set; } = 90;

    /// <summary>A match nobody finishes is ended after this long.</summary>
    public int MatchMaxMinutes { get; set; } = 90;

    /// <summary>The host socket only accepts programs on this PC (switched off in tests only).</summary>
    public bool HostMustBeLocal { get; set; } = true;
}

/// <summary>Starts (and stops) the duel host programs. A fake one is used in tests.</summary>
public interface IHostLauncher
{
    /// <summary>Returns null when started, or a reason it could not be.</summary>
    Task<string?> LaunchAsync(string matchId, string secret, string hostSocketUrl);
    void Stop(string matchId);
}

/// <summary>One WebSocket with a send lock (a socket allows only one send at a time).</summary>
public class Conn
{
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public WebSocket Socket { get; }
    public Guid UserId { get; init; }

    public Conn(WebSocket socket)
    {
        Socket = socket;
    }

    public bool Open => Socket.State == WebSocketState.Open;

    public async Task SendAsync(string text)
    {
        if (!Open)
        {
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(text);

        await sendLock.WaitAsync();

        try
        {
            if (Open)
            {
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The other end vanished; the receive loop notices and cleans up.
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>Reads one whole text message (or null if the socket closed / the message is too big).</summary>
    public async Task<string?> ReceiveAsync(int maxBytes, CancellationToken cancel)
    {
        byte[] buffer = new byte[8192];
        using MemoryStream message = new();

        while (true)
        {
            WebSocketReceiveResult result;

            try
            {
                result = await Socket.ReceiveAsync(buffer, cancel);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);

            if (message.Length > maxBytes)
            {
                return null;
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    public async Task CloseAsync(string reason)
    {
        try
        {
            if (Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}

public class Match
{
    public string Id { get; init; } = "";
    public Guid RoomId { get; init; }
    public string Secret { get; init; } = "";
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public Conn? Host { get; set; }
    public Conn?[] Phones { get; } = new Conn?[2];
    public DateTime?[] DisconnectedAt { get; } = new DateTime?[2];
    public bool HostReady { get; set; }
    public bool Finished { get; set; }
    public CancellationTokenSource Cancel { get; } = new();
    public DateTime[] LastMessage { get; } = new DateTime[2];
    public int[] Burst { get; } = new int[2];
}

/// <summary>
/// RUNS THE MATCHES. When two players are ready it starts a duel host, then passes messages between the
/// two phones and the host: phone intents go to the host (stamped with the seat the SERVER knows the phone
/// has, never the one the phone claims), and the host's snapshots go to the right phone. When the host
/// reports the result, the server records it, pays the rewards and updates the guild ratings.
/// </summary>
public class MatchCoordinator
{
    public const int MaxMessageBytes = 64 * 1024;

    private readonly RoomRegistry rooms;
    private readonly IHostLauncher launcher;
    private readonly OnlineOptions options;
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<MatchCoordinator> log;
    private readonly ConcurrentDictionary<string, Match> matches = new();

    public MatchCoordinator(RoomRegistry rooms, IHostLauncher launcher, IOptions<OnlineOptions> options, IServiceScopeFactory scopes, ILogger<MatchCoordinator> log)
    {
        this.rooms = rooms;
        this.launcher = launcher;
        this.options = options.Value;
        this.scopes = scopes;
        this.log = log;
    }

    public int ActiveMatches => matches.Count(m => !m.Value.Finished);

    public Match? Find(string matchId) => matches.GetValueOrDefault(matchId);

    // ============================================================
    // STARTING
    // ============================================================

    /// <summary>Both players are ready: start the duel host. On failure the room goes back to waiting with an error.</summary>
    public async Task<string?> StartAsync(Room room)
    {
        if (ActiveMatches >= options.MaxMatches)
        {
            return ResetRoom(room, "The server is busy with other duels. Try again in a minute.");
        }

        Match match = new()
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            RoomId = room.Id,
            Secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
        };

        matches[match.Id] = match;
        room.MatchId = match.Id;
        room.Status = RoomStatus.Starting;
        room.Error = null;

        string? problem = await launcher.LaunchAsync(match.Id, match.Secret, options.HostApiUrl.TrimEnd('/') + "/ws/host");

        if (problem != null)
        {
            matches.TryRemove(match.Id, out _);
            room.MatchId = null;
            return ResetRoom(room, problem);
        }

        // Watchdogs: the host must report ready in time, and no match may run forever.
        _ = Task.Run(() => Watch(match));
        return null;
    }

    // The duel could not start: back to waiting, decks un-chosen, with the reason shown to the players.
    private static string ResetRoom(Room room, string problem)
    {
        room.Status = RoomStatus.Waiting;
        room.Error = problem;

        foreach (RoomSeat? seat in room.Seats)
        {
            if (seat != null)
            {
                seat.Deck = null;
            }
        }

        return problem;
    }

    private async Task Watch(Match match)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.HostStartSeconds), match.Cancel.Token);

            if (!match.HostReady && !match.Finished)
            {
                log.LogWarning("Duel host for match {Id} never reported ready.", match.Id);
                await FailAsync(match, "The duel host didn't start. Please try again.");
                return;
            }

            await Task.Delay(TimeSpan.FromMinutes(options.MatchMaxMinutes), match.Cancel.Token);

            if (!match.Finished)
            {
                await FailAsync(match, "This duel went on too long and was stopped.");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ============================================================
    // THE DUEL HOST'S SOCKET
    // ============================================================

    public async Task RunHostAsync(WebSocket socket, string matchId, string secret)
    {
        Match? match = Find(matchId);
        Conn host = new(socket);

        if (match == null || match.Finished || !FixedTimeEquals(match.Secret, secret) || match.Host != null)
        {
            await host.CloseAsync("not allowed");
            return;
        }

        Room? room = rooms.Get(match.RoomId);

        if (room == null || room.Seats[0] == null || room.Seats[1] == null)
        {
            await host.CloseAsync("no room");
            return;
        }

        match.Host = host;

        // Tell the host who is playing, with which decks.
        object start = new
        {
            matchId = match.Id,
            names = new[] { room.Seats[0]!.Username, room.Seats[1]!.Username },
            decks = new[]
            {
                new { main = room.Seats[0]!.Deck!.Main, extra = room.Seats[0]!.Deck!.Extra },
                new { main = room.Seats[1]!.Deck!.Main, extra = room.Seats[1]!.Deck!.Extra }
            },
            firstSeat = -1
        };

        await host.SendAsync(Wire.ToText(MessageTypes.Start, 0, start));

        while (true)
        {
            string? text = await host.ReceiveAsync(MaxMessageBytes * 4, match.Cancel.Token);

            if (text == null)
            {
                break;
            }

            Envelope? message = Wire.Parse(text);

            if (message == null)
            {
                continue;
            }

            await OnHostMessageAsync(match, message, text);
        }

        // The host went away.
        if (!match.Finished)
        {
            await FailAsync(match, "The duel host stopped unexpectedly.");
        }

        await host.CloseAsync("bye");
    }

    private async Task OnHostMessageAsync(Match match, Envelope message, string raw)
    {
        switch (message.T)
        {
            case MessageTypes.Ready:
                match.HostReady = true;

                if (rooms.Get(match.RoomId) is { } room)
                {
                    room.Status = RoomStatus.InMatch;
                    room.LastActivityUtc = DateTime.UtcNow;
                }

                foreach (Conn? phone in match.Phones)
                {
                    if (phone != null)
                    {
                        await phone.SendAsync(Wire.ToText(MessageTypes.Started, 0));
                    }
                }

                return;

            case MessageTypes.Ended:
                // Both seats are told; record the result once.
                await ForwardToPhoneAsync(match, message.Seat, raw);

                if (!match.Finished && message.Seat == 0)
                {
                    DuelResultMessage? result = JsonSerializer.Deserialize<DuelResultMessage>(message.Json, Wire.Options);

                    if (result != null)
                    {
                        _ = Task.Run(() => FinishAsync(match, result));
                    }
                }

                return;

            default:
                await ForwardToPhoneAsync(match, message.Seat, raw);
                return;
        }
    }

    private static async Task ForwardToPhoneAsync(Match match, int seat, string raw)
    {
        if (seat is 0 or 1 && match.Phones[seat] is { } phone)
        {
            await phone.SendAsync(raw);
        }
    }

    // ============================================================
    // A PHONE'S SOCKET
    // ============================================================

    public async Task RunPhoneAsync(WebSocket socket, Guid userId)
    {
        Conn phone = new(socket) { UserId = userId };
        Room? room = rooms.ByUser(userId);
        Match? match = room?.MatchId != null ? Find(room.MatchId) : null;

        if (room == null || match == null || match.Finished)
        {
            await phone.SendAsync(Wire.ToText(MessageTypes.Error, 0, "You have no duel to join. Join a room and get ready first."));
            await phone.CloseAsync("no match");
            return;
        }

        int seat = room.SeatOf(userId);

        // A second connection (a reconnect, or a second device) replaces the first.
        Conn? old = match.Phones[seat];
        match.Phones[seat] = phone;
        match.DisconnectedAt[seat] = null;

        if (old != null)
        {
            await old.CloseAsync("replaced");
        }

        await phone.SendAsync(Wire.ToText(MessageTypes.Joined, seat, new { matchId = match.Id, seat, hostReady = match.HostReady }));

        // A phone that joins late (or comes back) is brought up to date by the host.
        if (match.HostReady && match.Host != null)
        {
            await match.Host.SendAsync(Wire.ToText(MessageTypes.Resync, seat));
        }

        while (true)
        {
            string? text = await phone.ReceiveAsync(MaxMessageBytes, match.Cancel.Token);

            if (text == null)
            {
                break;
            }

            Envelope? message = Wire.Parse(text);

            if (message == null || !AllowedFromPhone(message.T) || match.Host == null)
            {
                continue;
            }

            if (TooFast(match, seat))
            {
                continue;
            }

            // The seat comes from the server's own records, never from what the phone says.
            message.Seat = seat;
            await match.Host.SendAsync(JsonSerializer.Serialize(message, Wire.Options));
        }

        if (match.Phones[seat] == phone)
        {
            match.Phones[seat] = null;
            match.DisconnectedAt[seat] = DateTime.UtcNow;
            _ = Task.Run(() => ForfeitIfStillGone(match, seat));
        }

        // Finish the closing handshake so the phone sees a clean close.
        await phone.CloseAsync("bye");
    }

    private static bool AllowedFromPhone(string type) => type is MessageTypes.Intent or MessageTypes.Answer or MessageTypes.Resync;

    // A phone may send a burst, but not flood the host.
    private static bool TooFast(Match match, int seat)
    {
        DateTime now = DateTime.UtcNow;

        if ((now - match.LastMessage[seat]).TotalSeconds >= 1)
        {
            match.LastMessage[seat] = now;
            match.Burst[seat] = 0;
        }

        return ++match.Burst[seat] > 40;
    }

    private async Task ForfeitIfStillGone(Match match, int seat)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.PhoneGraceSeconds), match.Cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!match.Finished && match.Phones[seat] == null && match.Host != null)
        {
            await match.Host.SendAsync(Wire.ToText(MessageTypes.Left, seat));
        }
    }

    // ============================================================
    // THE END
    // ============================================================

    private async Task FinishAsync(Match match, DuelResultMessage result)
    {
        if (match.Finished)
        {
            return;
        }

        match.Finished = true;
        Room? room = rooms.Get(match.RoomId);

        if (room != null)
        {
            room.Status = RoomStatus.Finished;
            room.LastActivityUtc = DateTime.UtcNow;

            try
            {
                using IServiceScope scope = scopes.CreateScope();
                MatchResults service = scope.ServiceProvider.GetRequiredService<MatchResults>();
                Dictionary<int, RewardsMessage> rewards = await service.RecordAsync(match, room, result, CancellationToken.None);

                foreach ((int seat, RewardsMessage reward) in rewards)
                {
                    await ForwardToPhoneAsync(match, seat, Wire.ToText(MessageTypes.Rewards, seat, reward));
                }
            }
            catch (Exception e)
            {
                log.LogError(e, "Could not record the result of match {Id}.", match.Id);
            }
        }

        await Task.Delay(1500);
        await ShutDownAsync(match);
    }

    /// <summary>The match can't continue (the host crashed, never started, ...): nobody wins, nothing is paid.</summary>
    public async Task FailAsync(Match match, string reason)
    {
        if (match.Finished)
        {
            return;
        }

        match.Finished = true;

        if (rooms.Get(match.RoomId) is { } room)
        {
            room.Status = RoomStatus.Finished;
            room.Error = reason;
            room.LastActivityUtc = DateTime.UtcNow;
        }

        for (int seat = 0; seat < 2; seat++)
        {
            await ForwardToPhoneAsync(match, seat, Wire.ToText(MessageTypes.Ended, seat, new DuelResultMessage { WinnerSeat = -1, Reason = reason }));
        }

        await ShutDownAsync(match);
    }

    /// <summary>A player leaves a duel that has started: they give up.</summary>
    public async Task SurrenderAsync(Room room, int seat)
    {
        Match? match = room.MatchId != null ? Find(room.MatchId) : null;

        if (match == null || match.Finished || seat < 0)
        {
            return;
        }

        if (match.Host != null && match.HostReady)
        {
            string intent = System.Text.Json.JsonSerializer.Serialize(new { id = 0, kind = "Surrender", card = 0, zone = -1, target = 0 });
            await match.Host.SendAsync(Wire.ToText(MessageTypes.Intent, seat, intent));
        }
        else
        {
            await FailAsync(match, "A player left before the duel started.");
        }
    }

    private async Task ShutDownAsync(Match match)
    {
        match.Cancel.Cancel();
        launcher.Stop(match.Id);

        foreach (Conn? phone in match.Phones)
        {
            if (phone != null)
            {
                await phone.CloseAsync("match over");
            }
        }

        if (match.Host != null)
        {
            await match.Host.CloseAsync("match over");
        }

        matches.TryRemove(match.Id, out _);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b ?? ""));
    }
}
