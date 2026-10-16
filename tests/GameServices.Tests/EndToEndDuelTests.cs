using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GameServices.Api.Auth;
using GameServices.Api.Economy;
using GameServices.Api.Online;
using Xunit;

namespace GameServices.Tests;

/// <summary>
/// THE WHOLE CHAIN, FOR REAL: the actual server (as its own program), the actual duel host (the built game),
/// and two bot "phones" that play a complete duel through real sockets.
///
/// It only runs when the built duel host exists: set DUELHOST_EXE to its path (or build it into the default place,
/// D:\GameServices\duelhost\DuelHost.exe). Otherwise the test is skipped.
/// </summary>
public class EndToEndDuelTests
{
    private static string? HostExe()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("DUELHOST_EXE");
        string[] candidates = { fromEnv ?? "", @"D:\GameServices\duelhost\DuelHost.exe" };
        return candidates.FirstOrDefault(c => c.Length > 0 && File.Exists(c));
    }

    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A bot that plays from the messages alone, like a phone would.</summary>
    private class BotPhone
    {
        private readonly ClientWebSocket socket = new();
        private readonly string name;
        private int seat = -1;
        private int turn = -1;
        private int intentId;
        private int sentForVersion = -1;
        private readonly HashSet<string> doneThisTurn = new();
        private JsonElement? actions;
        private int actionsVersion;

        public int Summons, Attacks, Spells, Answers, Rejections;
        public JsonElement? Ended;
        public JsonElement? Rewards;
        public int SnapshotsSeen;
        public int Turn => turn;
        public int SurrenderAtTurn { get; init; } = 11;
        private bool surrendered;

        public BotPhone(string name)
        {
            this.name = name;
        }

        public async Task RunAsync(Uri url, TimeSpan limit)
        {
            await socket.ConnectAsync(url, CancellationToken.None);
            DateTime until = DateTime.UtcNow + limit;

            // One reader for the whole run (cancelling a read would abort the socket).
            System.Collections.Concurrent.ConcurrentQueue<string> inbox = new();
            _ = Task.Run(async () =>
            {
                byte[] buffer = new byte[16384];

                try
                {
                    while (socket.State == WebSocketState.Open)
                    {
                        string? text = await ReadAsync(buffer, CancellationToken.None);

                        if (text == null)
                        {
                            return;
                        }

                        inbox.Enqueue(text);
                    }
                }
                catch (Exception)
                {
                }
            });

            while (DateTime.UtcNow < until && Rewards == null)
            {
                if (inbox.TryDequeue(out string? text))
                {
                    await HandleAsync(text);
                }
                else
                {
                    await Task.Delay(10);
                }
            }
        }

        private async Task<string?> ReadAsync(byte[] buffer, CancellationToken cancel)
        {
            using MemoryStream message = new();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(buffer, cancel);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(message.ToArray());
        }

        private async Task SendAsync(string type, object payload)
        {
            string text = Wire.ToText(type, seat < 0 ? 0 : seat, payload);
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private async Task HandleAsync(string text)
        {
            Envelope? e = Wire.Parse(text);

            if (e == null)
            {
                return;
            }

            JsonElement body = e.Json.Length > 0 ? JsonDocument.Parse(e.Json).RootElement : default;

            switch (e.T)
            {
                case MessageTypes.Joined:
                    seat = body.GetProperty("seat").GetInt32();
                    break;

                case MessageTypes.Snapshot:
                    SnapshotsSeen++;
                    turn = body.GetProperty("turn").GetInt32();
                    break;

                case MessageTypes.Actions:
                    actions = body.Clone();
                    actionsVersion++;
                    await ActAsync();
                    break;

                case MessageTypes.Prompt:
                    await AnswerAsync(body);
                    break;

                case MessageTypes.Rejected:
                    Rejections++;
                    sentForVersion = -1;
                    break;

                case MessageTypes.Ended:
                    Ended = body.Clone();
                    break;

                case MessageTypes.Rewards:
                    Rewards = body.Clone();
                    break;
            }
        }

        private async Task AnswerAsync(JsonElement prompt)
        {
            Answers++;
            string kind = prompt.GetProperty("kind").GetString() ?? "";
            int choice;

            if (kind == "Pick")
            {
                JsonElement cards = prompt.GetProperty("cards");
                choice = cards.GetArrayLength() > 0 ? cards[0].GetInt32() : 0;
            }
            else
            {
                choice = kind == "Respond" ? -1 : 0;
            }

            await SendAsync(MessageTypes.Answer, new { promptId = prompt.GetProperty("promptId").GetInt32(), choice });
        }

        private async Task ActAsync()
        {
            if (actions is not { } a || !a.GetProperty("yourTurn").GetBoolean())
            {
                return;
            }

            if (turn != lastTurn)
            {
                lastTurn = turn;
                doneThisTurn.Clear();
            }

            // Wait for the host to answer the last request (new actions) before sending another.
            if (sentForVersion == actionsVersion)
            {
                return;
            }

            if (turn >= SurrenderAtTurn && !surrendered)
            {
                surrendered = true;
                await SendAsync(MessageTypes.Intent, new { id = ++intentId, kind = "Surrender", card = 0, zone = -1, target = 0 });
                return;
            }

            List<JsonElement> list = a.GetProperty("actions").EnumerateArray().ToList();
            JsonElement? pick = null;

            foreach (string kind in new[] { "Summon", "TributeSummon", "PlaySpell", "Effect", "ActivateSet", "Attack", "EnterBattle", "EndTurn" })
            {
                pick = list.Where(x => x.GetProperty("kind").GetString() == kind && !doneThisTurn.Contains(kind + x.GetProperty("card").GetInt32()))
                           .Select(x => (JsonElement?)x).FirstOrDefault();

                if (pick != null)
                {
                    break;
                }
            }

            if (pick is not { } chosen)
            {
                return;
            }

            string chosenKind = chosen.GetProperty("kind").GetString()!;
            int card = chosen.GetProperty("card").GetInt32();
            doneThisTurn.Add(chosenKind + card);
            int target = 0;

            if (chosenKind == "Attack")
            {
                Attacks++;
                JsonElement targets = chosen.GetProperty("targets");
                target = targets.EnumerateArray().Any(t => t.GetInt32() == 0) ? 0 : targets[0].GetInt32();
            }

            if (chosenKind == "Summon" || chosenKind == "TributeSummon") Summons++;
            if (chosenKind == "PlaySpell") Spells++;

            int[] tributes = Array.Empty<int>();

            if (chosenKind == "TributeSummon")
            {
                // The bot does not track its field; it skips tribute summons.
                Summons--;
                return;
            }

            sentForVersion = actionsVersion;
            await SendAsync(MessageTypes.Intent, new { id = ++intentId, kind = chosenKind, card, zone = -1, target, tributes });
        }

        private int lastTurn = -2;
    }

    [Fact]
    public async Task Two_bot_phones_play_a_whole_duel_through_the_real_server_and_the_real_duel_host()
    {
        string? exe = HostExe();

        if (exe == null)
        {
            // Not built on this machine: nothing to test.
            return;
        }

        int port = FreePort();
        string dbPath = Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid():N}.db");
        string apiDll = typeof(Program).Assembly.Location;
        string logs = Path.Combine(Path.GetTempPath(), $"e2e-host-logs-{Guid.NewGuid():N}");

        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(apiDll)!
        };

        start.ArgumentList.Add(apiDll);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["Jwt__Key"] = "TEST-ONLY-key-0123456789abcdef0123456789abcdef";
        start.Environment["ConnectionStrings__Default"] = $"Data Source={dbPath}";
        start.Environment["Online__HostExecutable"] = exe;
        start.Environment["Online__HostApiUrl"] = $"ws://127.0.0.1:{port}";
        start.Environment["Online__HostLogDirectory"] = logs;
        start.Environment["Online__HostStartSeconds"] = "120";
        start.Environment["RateLimit__AuthRequestsPerMinute"] = "1000";

        using Process api = Process.Start(start)!;
        StringBuilder apiLog = new();
        api.OutputDataReceived += (_, e) => { if (e.Data != null) lock (apiLog) apiLog.AppendLine(e.Data); };
        api.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (apiLog) apiLog.AppendLine(e.Data); };
        api.BeginOutputReadLine();
        api.BeginErrorReadLine();

        try
        {
            using HttpClient anonymous = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            DateTime until = DateTime.UtcNow.AddSeconds(30);
            bool up = false;

            while (DateTime.UtcNow < until && !up)
            {
                try
                {
                    up = (await anonymous.GetAsync("/health")).IsSuccessStatusCode;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(300);
                }
            }

            Assert.True(up, "the server did not start:\n" + apiLog);

            async Task<(HttpClient Http, string Token, string Name)> Player()
            {
                HttpClient http = new() { BaseAddress = anonymous.BaseAddress };
                string name = "e" + Guid.NewGuid().ToString("N")[..10];
                AuthResponse auth = (await (await http.PostAsJsonAsync("/api/auth/register", new RegisterRequest(name, "CorrectHorse1", null)))
                    .Content.ReadFromJsonAsync<AuthResponse>())!;
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
                return (http, auth.AccessToken, name);
            }

            var a = await Player();
            var b = await Player();

            RoomDto room = (await (await a.Http.PostAsJsonAsync("/api/rooms", new CreateRoomRequest("End to end", false))).Content.ReadFromJsonAsync<RoomDto>(Wire.Options))!;
            Assert.Equal(HttpStatusCode.OK, (await b.Http.PostAsync($"/api/rooms/{room.Id}/join", null)).StatusCode);

            async Task<DeckRequest> Starter(HttpClient http)
            {
                ProfileResponse profile = (await http.GetFromJsonAsync<ProfileResponse>("/api/card-game/profile", Wire.Options))!;
                return new DeckRequest(profile.Collection.SelectMany(c => Enumerable.Repeat(c.CardId, c.Quantity)).ToList(), new List<string>());
            }

            Assert.Equal(HttpStatusCode.OK, (await a.Http.PostAsJsonAsync("/api/rooms/mine/deck", await Starter(a.Http))).StatusCode);
            HttpResponseMessage second = await b.Http.PostAsJsonAsync("/api/rooms/mine/deck", await Starter(b.Http));
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            RoomDto started = (await second.Content.ReadFromJsonAsync<RoomDto>(Wire.Options))!;
            Assert.True(started.Error == "", "the duel could not start: " + started.Error);

            BotPhone botA = new("Alice");
            BotPhone botB = new("Bob");
            TimeSpan limit = TimeSpan.FromMinutes(8);

            Task runA = botA.RunAsync(new Uri($"ws://127.0.0.1:{port}/ws/duel?access_token={a.Token}"), limit);
            Task runB = botB.RunAsync(new Uri($"ws://127.0.0.1:{port}/ws/duel?access_token={b.Token}"), limit);
            await Task.WhenAll(runA, runB);

            string detail = $"A: snapshots {botA.SnapshotsSeen}, summons {botA.Summons}, turn {botA.Turn}; B: snapshots {botB.SnapshotsSeen}, summons {botB.Summons}, turn {botB.Turn}\nServer log:\n{apiLog}";

            Assert.True(botA.Ended != null && botB.Ended != null, "the duel never ended. " + detail);
            Assert.True(botA.Rewards != null && botB.Rewards != null, "no rewards arrived. " + detail);
            Assert.True(botA.Summons + botB.Summons >= 2, "the bots barely did anything. " + detail);

            int winner = botA.Ended!.Value.GetProperty("winnerSeat").GetInt32();
            Assert.Equal(winner, botB.Ended!.Value.GetProperty("winnerSeat").GetInt32());

            int paidA = botA.Rewards!.Value.GetProperty("awarded").GetInt32();
            int paidB = botB.Rewards!.Value.GetProperty("awarded").GetInt32();
            int turns = botA.Ended!.Value.GetProperty("turns").GetInt32();

            if (turns >= MatchResults.MinTurnsForRewards)
            {
                Assert.Equal(0, paidA);   // online duels pay no Duel Points
                Assert.Equal(0, paidB);
            }

            // The wallet and the history agree.
            ProfileResponse walletA = (await a.Http.GetFromJsonAsync<ProfileResponse>("/api/card-game/profile", Wire.Options))!;
            Assert.Equal(900 + paidA, walletA.DuelPoints);
            Assert.Single((await b.Http.GetFromJsonAsync<List<MatchHistoryDto>>("/api/matches/mine", Wire.Options))!);
        }
        finally
        {
            try
            {
                api.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
