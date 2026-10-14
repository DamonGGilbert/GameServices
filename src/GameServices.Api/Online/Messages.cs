using System.Text.Json;

namespace GameServices.Api.Online;

/// <summary>
/// The envelope every message travels in (see the game's OnlineProtocol.cs). The server only reads
/// <c>T</c>, <c>Seat</c> and <c>Seq</c>; the payload (<c>Json</c>) is passed along untouched, so the
/// duel rules can change without this server changing.
/// </summary>
public class Envelope
{
    public string T { get; set; } = "";
    public int Seat { get; set; }
    public int Seq { get; set; }
    public string Json { get; set; } = "";
}

/// <summary>What the host reports when the duel is over.</summary>
public class DuelResultMessage
{
    public int WinnerSeat { get; set; } = -1;
    public string Reason { get; set; } = "";
    public int Turns { get; set; }
    public int Lp0 { get; set; }
    public int Lp1 { get; set; }
}

/// <summary>What the server tells each phone after paying out.</summary>
public record RewardsMessage(int Awarded, int DuelPoints, int GuildDelta, int GuildRating, string GuildName, bool GuildMatch);

public static class MessageTypes
{
    // host -> phone (passed through)
    public const string Snapshot = "snapshot";
    public const string Actions = "actions";
    public const string Prompt = "prompt";
    public const string Rejected = "rejected";
    public const string Ended = "ended";

    // phone -> host
    public const string Intent = "intent";
    public const string Answer = "answer";
    public const string Resync = "resync";
    public const string Settings = "settings";   // how the player wants to be asked to respond, and how fast the duel is
    public const string Timeout = "timeout";     // "I call my timeout" (the duel host adds time to the clock that is running)

    // server <-> host
    public const string Start = "start";
    public const string Ready = "ready";
    public const string Left = "left";

    // server -> phone
    public const string Joined = "joined";
    public const string Started = "started";
    public const string Rewards = "rewards";
    public const string Error = "error";
}

public static class Wire
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string ToText(string type, int seat, string json = "")
        => JsonSerializer.Serialize(new Envelope { T = type, Seat = seat, Json = json }, Options);

    public static string ToText(string type, int seat, object payload)
        => ToText(type, seat, JsonSerializer.Serialize(payload, Options));

    public static Envelope? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<Envelope>(text, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
