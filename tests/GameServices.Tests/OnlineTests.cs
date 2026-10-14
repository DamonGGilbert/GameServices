using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GameServices.Api.Auth;
using GameServices.Api.Catalog;
using GameServices.Api.Economy;
using GameServices.Api.Guilds;
using GameServices.Api.Online;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameServices.Tests;

// ------------------------------------------------------------------
// Test helpers: a fake duel host (so no Unity is needed) and a small
// WebSocket wrapper that reads our message envelopes.
// ------------------------------------------------------------------

/// <summary>Stands in for the program that starts the duel host. Tests decide what the "host" does.</summary>
public class FakeLauncher : IHostLauncher
{
    public Func<string, string, Task>? OnLaunch { get; set; }
    public string? Fail { get; set; }
    public List<string> Stopped { get; } = new();

    public Task<string?> LaunchAsync(string matchId, string secret, string hostSocketUrl)
    {
        if (Fail != null)
        {
            return Task.FromResult<string?>(Fail);
        }

        _ = Task.Run(() => OnLaunch?.Invoke(matchId, secret));
        return Task.FromResult<string?>(null);
    }

    public void Stop(string matchId) => Stopped.Add(matchId);
}

public class OnlineFactory : ApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("Online:HostMustBeLocal", "false");
        builder.UseSetting("Online:PhoneGraceSeconds", "1");
        builder.UseSetting("Online:HostStartSeconds", "6");
        builder.UseSetting("Online:MaxMatches", "50");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostLauncher>();
            services.AddSingleton<FakeLauncher>();
            services.AddSingleton<IHostLauncher>(provider => provider.GetRequiredService<FakeLauncher>());
        });
    }
}

/// <summary>One end of a WebSocket that speaks in Envelopes.</summary>
public class Peer : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly List<Envelope> inbox = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task reader;

    public bool Closed { get; private set; }
    public IReadOnlyList<Envelope> All { get { lock (inbox) { return inbox.ToList(); } } }

    public Peer(WebSocket socket)
    {
        this.socket = socket;
        reader = Task.Run(ReadLoop);
    }

    private async Task ReadLoop()
    {
        byte[] buffer = new byte[16384];

        try
        {
            while (!stop.IsCancellationRequested)
            {
                using MemoryStream message = new();
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(buffer, stop.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Closed = true;
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                Envelope? envelope = Wire.Parse(Encoding.UTF8.GetString(message.ToArray()));

                if (envelope != null)
                {
                    lock (inbox)
                    {
                        inbox.Add(envelope);
                    }
                }
            }
        }
        catch (Exception)
        {
            Closed = true;
        }
    }

    public async Task SendAsync(string type, string json = "", int seat = 0)
    {
        string text = Wire.ToText(type, seat, json);
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task<Envelope> WaitAsync(string type, int timeoutMs = 6000, Func<Envelope, bool>? where = null)
    {
        DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        int from = 0;

        while (DateTime.UtcNow < until)
        {
            lock (inbox)
            {
                for (; from < inbox.Count; from++)
                {
                    if (inbox[from].T == type && (where == null || where(inbox[from])))
                    {
                        return inbox[from++];
                    }
                }
            }

            await Task.Delay(15);
        }

        throw new TimeoutException($"No '{type}' message arrived. Got: {string.Join(", ", All.Select(e => e.T))}");
    }

    public async Task<bool> WaitClosedAsync(int timeoutMs = 4000)
    {
        DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < until)
        {
            if (Closed)
            {
                return true;
            }

            await Task.Delay(15);
        }

        return Closed;
    }

    public async Task CloseAsync()
    {
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        socket.Abort();

        // Never wait forever for the reader (a test server socket may ignore cancellation).
        await Task.WhenAny(reader, Task.Delay(500));
        socket.Dispose();
    }
}

/// <summary>
/// A pretend duel host. Every EndTurn intent counts a turn; after <see cref="TurnsToPlay"/> turns the player
/// who ended the last turn loses (or whoever <see cref="WinnerSeat"/> says). A Surrender intent ends it at once.
/// </summary>
public class FakeHost
{
    public int TurnsToPlay { get; init; } = 3;
    public int WinnerSeat { get; init; } = 0;
    public int FakeTurnsOnSurrender { get; init; } = 5;
    public Peer? Peer { get; private set; }

    public List<(int Seat, string Type, string Json)> Got { get; } = new();
    public Envelope? Start { get; private set; }

    private int turn;
    private bool ended;

    public async Task RunAsync(WebSocket socket)
    {
        Peer = new Peer(socket);
        Start = await Peer.WaitAsync(MessageTypes.Start);
        await Peer.SendAsync(MessageTypes.Ready);

        int seen = 0;

        DateTime giveUp = DateTime.UtcNow.AddMinutes(2);

        while (!Peer.Closed && DateTime.UtcNow < giveUp)
        {
            List<Envelope> all = Peer.All.ToList();

            for (; seen < all.Count; seen++)
            {
                Envelope e = all[seen];

                if (e.T == MessageTypes.Start)
                {
                    continue;
                }

                lock (Got)
                {
                    Got.Add((e.Seat, e.T, e.Json));
                }

                await HandleAsync(e);
            }

            await Task.Delay(10);
        }
    }

    private async Task HandleAsync(Envelope e)
    {
        if (e.T == MessageTypes.Resync)
        {
            await Peer!.SendAsync(MessageTypes.Snapshot, $"{{\"turn\":{turn},\"youSeat\":{e.Seat}}}", e.Seat);
            return;
        }

        if (e.T != MessageTypes.Intent || ended)
        {
            return;
        }

        string kind = JsonDocument.Parse(e.Json).RootElement.GetProperty("kind").GetString() ?? "";

        if (kind == "EndTurn")
        {
            turn++;

            for (int seat = 0; seat < 2; seat++)
            {
                await Peer!.SendAsync(MessageTypes.Snapshot, $"{{\"turn\":{turn},\"youSeat\":{seat}}}", seat);
            }

            if (turn >= TurnsToPlay)
            {
                await EndAsync(WinnerSeat, "Life Points hit 0.", turn);
            }
        }
        else if (kind == "Surrender")
        {
            await EndAsync(1 - e.Seat, $"seat {e.Seat} surrendered.", FakeTurnsOnSurrender);
        }
    }

    private async Task EndAsync(int winner, string reason, int turns)
    {
        ended = true;
        string result = JsonSerializer.Serialize(new { winnerSeat = winner, reason, turns, lp0 = 0, lp1 = 8000 });

        for (int seat = 0; seat < 2; seat++)
        {
            await Peer!.SendAsync(MessageTypes.Ended, result, seat);
        }
    }
}

public class OnlineTests : IClassFixture<OnlineFactory>
{
    private readonly OnlineFactory factory;
    private readonly FakeLauncher launcher;

    public OnlineTests(OnlineFactory factory)
    {
        this.factory = factory;
        launcher = factory.Services.GetRequiredService<FakeLauncher>();
        launcher.Fail = null;
        launcher.OnLaunch = null;
    }

    private record Player(HttpClient Http, Guid Id, string Name, string Token);

    private async Task<Player> NewPlayer()
    {
        HttpClient client = factory.CreateClient();
        string name = "o" + Guid.NewGuid().ToString("N")[..10];

        AuthResponse auth = (await (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
            .Content.ReadFromJsonAsync<AuthResponse>())!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Player(client, auth.User.Id, name, auth.AccessToken);
    }

    private static async Task<RoomDto> Create(Player p, string? name = null, bool guildMatch = false)
    {
        HttpResponseMessage response = await p.Http.PostAsJsonAsync("/api/rooms", new CreateRoomRequest(name, guildMatch));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RoomDto>(Wire.Options))!;
    }

    private static async Task<RoomDto> Mine(Player p)
        => (await p.Http.GetFromJsonAsync<RoomDto>("/api/rooms/mine", Wire.Options))!;

    private static Task<HttpResponseMessage> Join(Player p, Guid roomId) => p.Http.PostAsync($"/api/rooms/{roomId}/join", null);

    /// <summary>The starter collection is a legal 40-card deck.</summary>
    private static async Task<DeckRequest> StarterDeck(Player p)
    {
        ProfileResponse profile = (await p.Http.GetFromJsonAsync<ProfileResponse>("/api/card-game/profile", Wire.Options))!;
        List<string> main = profile.Collection.SelectMany(c => Enumerable.Repeat(c.CardId, c.Quantity)).ToList();
        return new DeckRequest(main, new List<string>());
    }

    private static async Task<HttpResponseMessage> SendDeck(Player p, DeckRequest? deck = null)
        => await p.Http.PostAsJsonAsync("/api/rooms/mine/deck", deck ?? await StarterDeck(p));

    private async Task<Peer> ConnectPhone(Player p)
    {
        WebSocketClient client = factory.Server.CreateWebSocketClient();
        WebSocket socket = await client.ConnectAsync(new Uri("ws://localhost/ws/duel?access_token=" + p.Token), CancellationToken.None);
        return new Peer(socket);
    }

    private async Task<WebSocket> ConnectHost(string matchId, string secret)
    {
        WebSocketClient client = factory.Server.CreateWebSocketClient();
        return await client.ConnectAsync(new Uri($"ws://localhost/ws/host?matchId={matchId}&secret={secret}"), CancellationToken.None);
    }

    /// <summary>Two players in a room, decks sent, a fake host started. Returns what the tests need.</summary>
    private async Task<(Player A, Player B, FakeHost Host, Peer PhoneA, Peer PhoneB)> StartDuel(FakeHost? host = null, bool guildMatch = false, Player? a = null, Player? b = null)
    {
        a ??= await NewPlayer();
        b ??= await NewPlayer();
        host ??= new FakeHost();

        launcher.OnLaunch = async (matchId, secret) =>
        {
            WebSocket socket = await ConnectHost(matchId, secret);
            await host.RunAsync(socket);
        };

        RoomDto room = await Create(a, guildMatch: guildMatch);
        Assert.Equal(HttpStatusCode.OK, (await Join(b, room.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendDeck(a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendDeck(b)).StatusCode);

        Peer phoneA = await ConnectPhone(a);
        Peer phoneB = await ConnectPhone(b);
        await phoneA.WaitAsync(MessageTypes.Joined);
        await phoneB.WaitAsync(MessageTypes.Joined);
        await phoneA.WaitAsync(MessageTypes.Started);
        await phoneB.WaitAsync(MessageTypes.Started);

        return (a, b, host, phoneA, phoneB);
    }

    // ------------------------------------------------------------------ rooms

    [Fact]
    public async Task Rooms_require_sign_in()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/rooms")).StatusCode);
    }

    [Fact]
    public async Task A_room_can_be_created_listed_and_joined()
    {
        Player a = await NewPlayer();
        Player b = await NewPlayer();
        Player c = await NewPlayer();

        RoomDto room = await Create(a, "Friendly duel");
        Assert.Equal("Waiting", room.Status);
        Assert.Equal(a.Name, room.Seats[0].Username);
        Assert.Equal(0, room.MySeat);

        List<RoomDto> list = (await b.Http.GetFromJsonAsync<List<RoomDto>>("/api/rooms", Wire.Options))!;
        RoomDto seen = Assert.Single(list, r => r.Id == room.Id);
        Assert.True(seen.CanJoin);
        Assert.False(seen.Mine);

        Assert.Equal(HttpStatusCode.OK, (await Join(b, room.Id)).StatusCode);

        // It is full now.
        Assert.Equal(HttpStatusCode.Conflict, (await Join(c, room.Id)).StatusCode);
        RoomDto full = (await c.Http.GetFromJsonAsync<List<RoomDto>>("/api/rooms", Wire.Options))!.Single(r => r.Id == room.Id);
        Assert.False(full.CanJoin);
        Assert.Equal(b.Name, full.Seats[1].Username);
    }

    [Fact]
    public async Task A_player_can_only_be_in_one_room()
    {
        Player a = await NewPlayer();
        Player b = await NewPlayer();
        RoomDto first = await Create(a);
        RoomDto other = await Create(b);

        Assert.Equal(HttpStatusCode.Conflict, (await a.Http.PostAsJsonAsync("/api/rooms", new CreateRoomRequest("Another", false))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Join(a, other.Id)).StatusCode);
        Assert.Equal(first.Id, (await Mine(a)).Id);
    }

    [Fact]
    public async Task Leaving_a_waiting_room_moves_the_other_player_up_and_an_empty_room_vanishes()
    {
        Player a = await NewPlayer();
        Player b = await NewPlayer();
        RoomDto room = await Create(a);
        await Join(b, room.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await a.Http.PostAsync("/api/rooms/leave", null)).StatusCode);

        RoomDto now = await Mine(b);
        Assert.Equal(room.Id, now.Id);
        Assert.Equal(0, now.MySeat);
        Assert.Equal(b.Name, now.Seats[0].Username);
        Assert.Equal("", now.Seats[1].Username);

        Assert.Equal(HttpStatusCode.NoContent, (await b.Http.PostAsync("/api/rooms/leave", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Http.GetAsync("/api/rooms/mine")).StatusCode);
        Assert.DoesNotContain((await b.Http.GetFromJsonAsync<List<RoomDto>>("/api/rooms", Wire.Options))!, r => r.Id == room.Id);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("a name that is much much too long for a room")]
    public async Task Room_names_are_checked(string name)
    {
        Player a = await NewPlayer();

        Assert.Equal(HttpStatusCode.BadRequest, (await a.Http.PostAsJsonAsync("/api/rooms", new CreateRoomRequest(name, false))).StatusCode);
    }

    // ------------------------------------------------------------------ decks

    [Fact]
    public async Task Decks_are_checked_against_the_cards_the_player_owns()
    {
        Player a = await NewPlayer();
        await Create(a);
        DeckRequest starter = await StarterDeck(a);

        // Too small.
        HttpResponseMessage small = await SendDeck(a, new DeckRequest(starter.Main!.Take(10).ToList(), new List<string>()));
        Assert.Equal(HttpStatusCode.BadRequest, small.StatusCode);
        Assert.Contains("at least 40", await small.Content.ReadAsStringAsync());

        // A card the player does not own (and that does not exist).
        List<string> withFake = starter.Main!.Take(39).Append("zzz-not-a-card").ToList();
        HttpResponseMessage fake = await SendDeck(a, new DeckRequest(withFake, new List<string>()));
        Assert.Equal(HttpStatusCode.BadRequest, fake.StatusCode);

        // More copies than owned.
        string first = starter.Main!.First();
        List<string> tooMany = starter.Main!.Where(c => c != first).Concat(Enumerable.Repeat(first, 4)).Take(45).ToList();
        HttpResponseMessage many = await SendDeck(a, new DeckRequest(tooMany, new List<string>()));
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);

        // The real starter deck is fine (and the room waits for the other player).
        HttpResponseMessage ok = await SendDeck(a, starter);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        RoomDto room = (await ok.Content.ReadFromJsonAsync<RoomDto>(Wire.Options))!;
        Assert.True(room.Seats[0].Ready);
        Assert.Equal("Waiting", room.Status);
    }

    [Fact]
    public void The_deck_checker_knows_extra_deck_and_ban_list_rules()
    {
        CardCatalog catalog = new(900,
            new List<CatalogCard>
            {
                new("a", "Alpha", "C", 0),
                new("fusion", "Fusion Thing", "R", 0, 0, Extra: true),
                new("banned", "Banned Thing", "R", 0, 0, Limit: 0),
                new("limited", "Limited Thing", "R", 0, 0, Limit: 1)
            },
            new List<StarterEntry>());

        Dictionary<string, int> owned = new() { ["a"] = 60, ["fusion"] = 3, ["banned"] = 3, ["limited"] = 3 };

        List<string> main = Enumerable.Repeat("a", 3).Concat(Enumerable.Range(0, 37).Select(_ => "a")).ToList();

        // 40 copies of one card breaks the 3-copy rule.
        Assert.Contains(DeckValidator.Check(new DeckList(main, new List<string>()), catalog, owned), p => p.Contains("only use 3"));

        // A Fusion monster in the Main Deck, a banned card, two of a limited card.
        List<string> bad = new() { "fusion", "banned", "limited", "limited" };
        List<string> problems = DeckValidator.Check(new DeckList(bad, new List<string> { "a" }), catalog, owned);
        Assert.Contains(problems, p => p.Contains("belongs in the Extra Deck"));
        Assert.Contains(problems, p => p.Contains("forbidden"));
        Assert.Contains(problems, p => p.Contains("only use 1 of Limited Thing"));
        Assert.Contains(problems, p => p.Contains("can't go in the Extra Deck"));
    }

    // ------------------------------------------------------------------ guild rooms

    private async Task<Guid> MakeGuild(Player leader)
    {
        string tag = "T" + Guid.NewGuid().ToString("N")[..3].ToUpperInvariant();
        HttpResponseMessage response = await leader.Http.PostAsJsonAsync("/api/guilds", new CreateGuildRequest("Guild " + Guid.NewGuid().ToString("N")[..8], tag, "tests", true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<GuildSummary>(Wire.Options))!.Id;
    }

    [Fact]
    public async Task Guild_matches_are_for_players_in_different_guilds()
    {
        Player alone = await NewPlayer();
        Player a1 = await NewPlayer();
        Player a2 = await NewPlayer();
        Player b1 = await NewPlayer();
        Guid guildA = await MakeGuild(a1);
        await MakeGuild(b1);
        await a2.Http.PostAsync($"/api/guilds/{guildA}/join", null);

        // No guild = no guild match.
        Assert.Equal(HttpStatusCode.BadRequest, (await alone.Http.PostAsJsonAsync("/api/rooms", new CreateRoomRequest("Solo", true))).StatusCode);

        RoomDto room = await Create(a1, guildMatch: true);
        Assert.True(room.GuildMatch);

        Assert.Equal(HttpStatusCode.Conflict, (await Join(a2, room.Id)).StatusCode);   // same guild
        Assert.Equal(HttpStatusCode.Conflict, (await Join(alone, room.Id)).StatusCode); // no guild
        Assert.Equal(HttpStatusCode.OK, (await Join(b1, room.Id)).StatusCode);          // another guild
    }

    // ------------------------------------------------------------------ the match

    [Fact]
    public async Task Two_ready_players_start_a_duel_and_messages_flow_with_the_seat_stamped_by_the_server()
    {
        (Player a, Player b, FakeHost host, Peer phoneA, Peer phoneB) = await StartDuel();

        // The host was told who plays with which decks.
        JsonElement start = JsonDocument.Parse(host.Start!.Json).RootElement;
        Assert.Equal(a.Name, start.GetProperty("names")[0].GetString());
        Assert.Equal(b.Name, start.GetProperty("names")[1].GetString());
        Assert.Equal(40, start.GetProperty("decks")[0].GetProperty("main").GetArrayLength());

        Envelope joinedA = await phoneA.WaitAsync(MessageTypes.Joined);
        Envelope joinedB = await phoneB.WaitAsync(MessageTypes.Joined);
        Assert.Equal(0, joinedA.Seat);
        Assert.Equal(1, joinedB.Seat);

        Assert.Equal("InMatch", (await Mine(a)).Status);

        // Phone B lies about its seat: the host still sees seat 1.
        await phoneB.SendAsync(MessageTypes.Intent, "{\"id\":1,\"kind\":\"EndTurn\"}", seat: 0);
        Envelope snapshotA = await phoneA.WaitAsync(MessageTypes.Snapshot);
        Envelope snapshotB = await phoneB.WaitAsync(MessageTypes.Snapshot);
        Assert.Contains("\"youSeat\":0", snapshotA.Json);
        Assert.Contains("\"youSeat\":1", snapshotB.Json);

        Assert.Contains(host.Got, g => g.Type == MessageTypes.Intent && g.Seat == 1);
        Assert.DoesNotContain(host.Got, g => g.Type == MessageTypes.Intent && g.Seat == 0);
    }

    [Fact]
    public async Task A_phone_cannot_send_host_only_messages()
    {
        (_, _, FakeHost host, Peer phoneA, _) = await StartDuel();

        await phoneA.SendAsync(MessageTypes.Ended, "{\"winnerSeat\":0,\"reason\":\"I win\",\"turns\":50}");
        await phoneA.SendAsync(MessageTypes.Start, "{}");
        await phoneA.SendAsync("nonsense", "{}");
        await phoneA.SendAsync(MessageTypes.Intent, "{\"id\":2,\"kind\":\"EndTurn\"}");
        await phoneA.WaitAsync(MessageTypes.Snapshot);

        Assert.DoesNotContain(host.Got, g => g.Type is MessageTypes.Ended or MessageTypes.Start or "nonsense");
        Assert.Contains(host.Got, g => g.Type == MessageTypes.Intent);
    }

    private static bool Received(FakeHost host, string type, int seat)
    {
        lock (host.Got)
        {
            return host.Got.Any(g => g.Type == type && g.Seat == seat);
        }
    }

    [Fact]
    public async Task A_phone_can_send_its_settings_and_the_server_stamps_the_seat()
    {
        (_, _, FakeHost host, _, Peer phoneB) = await StartDuel();

        // Phone B (seat 1) lies about its seat. The host must still see seat 1.
        await phoneB.SendAsync(MessageTypes.Settings, "{\"respond\":\"Off\",\"speed\":\"Fast\"}", seat: 0);

        // Wait (a moment at most) for the host to receive it.
        for (int i = 0; i < 100 && !Received(host, MessageTypes.Settings, 1); i++)
        {
            await Task.Delay(30);
        }

        Assert.True(Received(host, MessageTypes.Settings, 1), "the host should have got phone B's settings, stamped seat 1");
        Assert.False(Received(host, MessageTypes.Settings, 0), "seat 0 never sent any");
    }

    [Fact]
    public async Task A_phone_can_call_a_timeout_and_the_server_stamps_the_seat()
    {
        (_, _, FakeHost host, Peer phoneA, _) = await StartDuel();

        // Phone A (seat 0) lies about its seat. The host must still see seat 0 ... and it must be the only one asking.
        await phoneA.SendAsync(MessageTypes.Timeout, "", seat: 1);

        for (int i = 0; i < 100 && !Received(host, MessageTypes.Timeout, 0); i++)
        {
            await Task.Delay(30);
        }

        Assert.True(Received(host, MessageTypes.Timeout, 0), "the host should have got phone A's timeout, stamped seat 0");
        Assert.False(Received(host, MessageTypes.Timeout, 1), "seat 1 never asked for one");
    }

    [Fact]
    public async Task A_finished_duel_pays_rewards_and_is_recorded()
    {
        (Player a, Player b, _, Peer phoneA, Peer phoneB) = await StartDuel(new FakeHost { TurnsToPlay = 3, WinnerSeat = 0 });

        for (int i = 0; i < 3; i++)
        {
            await phoneA.SendAsync(MessageTypes.Intent, "{\"id\":" + (i + 1) + ",\"kind\":\"EndTurn\"}");
            await Task.Delay(30);
        }

        Envelope endedA = await phoneA.WaitAsync(MessageTypes.Ended);
        Assert.Contains("\"winnerSeat\":0", endedA.Json);

        RewardsMessage rewardsA = JsonSerializer.Deserialize<RewardsMessage>((await phoneA.WaitAsync(MessageTypes.Rewards)).Json, Wire.Options)!;
        RewardsMessage rewardsB = JsonSerializer.Deserialize<RewardsMessage>((await phoneB.WaitAsync(MessageTypes.Rewards)).Json, Wire.Options)!;

        Assert.Equal(EconomyEndpoints.WinReward, rewardsA.Awarded);
        Assert.Equal(EconomyEndpoints.LossReward, rewardsB.Awarded);
        Assert.Equal(900 + EconomyEndpoints.WinReward, rewardsA.DuelPoints);
        Assert.Equal(900 + EconomyEndpoints.LossReward, rewardsB.DuelPoints);

        // The wallet really changed, and the history shows it.
        Assert.Equal(900 + EconomyEndpoints.WinReward, (await a.Http.GetFromJsonAsync<ProfileResponse>("/api/card-game/profile", Wire.Options))!.DuelPoints);

        List<MatchHistoryDto> historyB = (await b.Http.GetFromJsonAsync<List<MatchHistoryDto>>("/api/matches/mine", Wire.Options))!;
        MatchHistoryDto line = Assert.Single(historyB);
        Assert.Equal("loss", line.Result);
        Assert.Equal(a.Name, line.Opponent);
        Assert.Equal(3, line.Turns);

        // The room is finished and a new one can be made.
        await Task.Delay(300);
        Assert.Equal(HttpStatusCode.OK, (await b.Http.PostAsJsonAsync("/api/rooms", new CreateRoomRequest("Round two", false))).StatusCode);
    }

    [Fact]
    public async Task A_duel_that_ends_very_early_pays_nothing()
    {
        (_, _, _, Peer phoneA, _) = await StartDuel(new FakeHost { FakeTurnsOnSurrender = 1 });

        await phoneA.SendAsync(MessageTypes.Intent, "{\"id\":1,\"kind\":\"Surrender\"}");

        await phoneA.WaitAsync(MessageTypes.Ended);
        RewardsMessage rewards = JsonSerializer.Deserialize<RewardsMessage>((await phoneA.WaitAsync(MessageTypes.Rewards)).Json, Wire.Options)!;
        Assert.Equal(0, rewards.Awarded);
        Assert.Equal(900, rewards.DuelPoints);
    }

    [Fact]
    public async Task Guild_matches_move_the_guild_ratings_and_the_leaderboard_ranks_guilds()
    {
        Player a = await NewPlayer();
        Player b = await NewPlayer();
        Guid guildA = await MakeGuild(a);
        Guid guildB = await MakeGuild(b);

        (_, _, _, Peer phoneA, Peer phoneB) = await StartDuel(new FakeHost { TurnsToPlay = 3, WinnerSeat = 0 }, guildMatch: true, a: a, b: b);

        for (int i = 0; i < 3; i++)
        {
            await phoneA.SendAsync(MessageTypes.Intent, "{\"id\":" + (i + 1) + ",\"kind\":\"EndTurn\"}");
            await Task.Delay(30);
        }

        RewardsMessage winner = JsonSerializer.Deserialize<RewardsMessage>((await phoneA.WaitAsync(MessageTypes.Rewards)).Json, Wire.Options)!;
        RewardsMessage loser = JsonSerializer.Deserialize<RewardsMessage>((await phoneB.WaitAsync(MessageTypes.Rewards)).Json, Wire.Options)!;

        // Two guilds at 1000: the winner takes half of K (16) from the loser.
        Assert.Equal(16, winner.GuildDelta);
        Assert.Equal(-16, loser.GuildDelta);
        Assert.Equal(1016, winner.GuildRating);
        Assert.Equal(984, loser.GuildRating);
        Assert.True(winner.GuildMatch);

        GuildLeaderboard board = (await a.Http.GetFromJsonAsync<GuildLeaderboard>("/api/guilds/leaderboard?top=200", Wire.Options))!;
        GuildRankDto rankA = board.Items.Single(g => g.GuildId == guildA);
        GuildRankDto rankB = board.Items.Single(g => g.GuildId == guildB);

        Assert.True(rankA.Rank < rankB.Rank);
        Assert.Equal(1, rankA.Wins);
        Assert.Equal(1, rankB.Losses);
        Assert.Equal(rankA.Rank, board.MyRank);

        // The list is ordered best first.
        Assert.Equal(board.Items.OrderByDescending(g => g.Rating).Select(g => g.Rating), board.Items.Select(g => g.Rating));
        Assert.Equal(Enumerable.Range(1, board.Items.Count), board.Items.Select(g => g.Rank));
    }

    [Fact]
    public void Elo_ratings_move_more_for_an_upset_and_by_zero_for_an_even_draw()
    {
        Assert.Equal(16, MatchResults.EloDelta(1000, 1000, 0));
        Assert.Equal(-16, MatchResults.EloDelta(1000, 1000, 1));
        Assert.Equal(0, MatchResults.EloDelta(1000, 1000, -1));
        Assert.True(MatchResults.EloDelta(900, 1300, 0) > 16);   // the underdog wins big
        Assert.True(MatchResults.EloDelta(1300, 900, 0) < 16);   // the favourite wins small
    }

    [Fact]
    public async Task A_wrong_host_secret_is_refused()
    {
        Player a = await NewPlayer();
        Player b = await NewPlayer();
        string? matchId = null;
        string? secret = null;
        TaskCompletionSource launched = new();

        launcher.OnLaunch = (id, s) =>
        {
            matchId = id;
            secret = s;
            launched.TrySetResult();
            return Task.CompletedTask;
        };

        RoomDto room = await Create(a);
        await Join(b, room.Id);
        await SendDeck(a);
        await SendDeck(b);
        await launched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using WebSocket wrong = await ConnectHost(matchId!, "not-the-secret");
        await using Peer peer = new(wrong);
        Assert.True(await peer.WaitClosedAsync());

        // The right secret works (and only once).
        using WebSocket right = await ConnectHost(matchId!, secret!);
        await using Peer good = new(right);
        await good.WaitAsync(MessageTypes.Start);

        using WebSocket second = await ConnectHost(matchId!, secret!);
        await using Peer duplicate = new(second);
        Assert.True(await duplicate.WaitClosedAsync());
    }

    [Fact]
    public async Task A_player_who_disconnects_forfeits_after_the_grace_period()
    {
        (_, _, FakeHost host, Peer phoneA, _) = await StartDuel();

        await phoneA.CloseAsync();

        // (The test server is set to a 1 second grace period.)
        DateTime until = DateTime.UtcNow.AddSeconds(6);

        while (DateTime.UtcNow < until && !host.Got.Any(g => g.Type == MessageTypes.Left))
        {
            await Task.Delay(50);
        }

        Assert.Contains(host.Got, g => g.Type == MessageTypes.Left && g.Seat == 0);
    }

    [Fact]
    public async Task A_player_who_reconnects_in_time_is_brought_up_to_date_and_does_not_forfeit()
    {
        (Player a, _, FakeHost host, Peer phoneA, _) = await StartDuel();

        await phoneA.CloseAsync();
        await Task.Delay(200);

        await using Peer again = await ConnectPhone(a);
        await again.WaitAsync(MessageTypes.Joined);

        // The host is asked to resend what this player may see.
        Envelope snapshot = await again.WaitAsync(MessageTypes.Snapshot);
        Assert.Contains("\"youSeat\":0", snapshot.Json);

        await Task.Delay(1800);
        Assert.DoesNotContain(host.Got, g => g.Type == MessageTypes.Left);
    }

    [Fact]
    public async Task Leaving_a_running_duel_is_a_surrender()
    {
        (Player a, _, FakeHost host, Peer phoneA, Peer phoneB) = await StartDuel(new FakeHost { FakeTurnsOnSurrender = 6 });

        Assert.Equal(HttpStatusCode.NoContent, (await a.Http.PostAsync("/api/rooms/leave", null)).StatusCode);

        Envelope ended = await phoneB.WaitAsync(MessageTypes.Ended);
        Assert.Contains("\"winnerSeat\":1", ended.Json);
        Assert.Contains(host.Got, g => g.Type == MessageTypes.Intent && g.Seat == 0 && g.Json.Contains("Surrender"));
        await phoneA.WaitAsync(MessageTypes.Ended);
    }

    [Fact]
    public async Task A_duel_that_cannot_start_leaves_the_room_waiting_with_an_error()
    {
        launcher.Fail = "Online duels aren't set up on this server yet.";

        Player a = await NewPlayer();
        Player b = await NewPlayer();
        RoomDto room = await Create(a);
        await Join(b, room.Id);
        await SendDeck(a);
        HttpResponseMessage response = await SendDeck(b);

        RoomDto after = (await response.Content.ReadFromJsonAsync<RoomDto>(Wire.Options))!;
        Assert.Equal("Waiting", after.Status);
        Assert.Contains("aren't set up", after.Error);
        Assert.False(after.Seats[0].Ready);
        Assert.False(after.Seats[1].Ready);
    }

    [Fact]
    public async Task The_server_refuses_to_run_more_duels_than_it_is_set_up_for()
    {
        MatchCoordinator coordinator = factory.Services.GetRequiredService<MatchCoordinator>();
        OnlineOptions options = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<OnlineOptions>>().Value;
        int original = options.MaxMatches;

        try
        {
            // Room for exactly one more duel than are running now.
            options.MaxMatches = coordinator.ActiveMatches + 1;

            (_, _, _, Peer phoneA, _) = await StartDuel();

            Player a = await NewPlayer();
            Player b = await NewPlayer();
            RoomDto room = await Create(a);
            await Join(b, room.Id);
            await SendDeck(a);
            RoomDto second = (await (await SendDeck(b)).Content.ReadFromJsonAsync<RoomDto>(Wire.Options))!;

            Assert.Equal("Waiting", second.Status);
            Assert.Contains("busy", second.Error);

            // When the first duel ends there is room again.
            await phoneA.SendAsync(MessageTypes.Intent, "{\"id\":9,\"kind\":\"Surrender\"}");
            await phoneA.WaitAsync(MessageTypes.Ended);
            await Task.Delay(2200);
        }
        finally
        {
            options.MaxMatches = original;
        }
    }
}
