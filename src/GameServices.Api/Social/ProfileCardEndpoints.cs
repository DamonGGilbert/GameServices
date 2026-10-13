using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServices.Api.Catalog;
using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Social;

/// <summary>
/// What a player chooses to show on their PROFILE CARD besides the showcase (level, prestige, frame, title):
/// a greeting, a favourite card, a favourite deck and some stats. The game reports it; it is cosmetic only.
/// Deck format: "cardId:copies,cardId:copies|cardId:copies"  (Main Deck | Extra Deck).
/// </summary>
public record ProfileCardDto(string Greeting, string FavCard, string DeckName, string Deck, int Wins, int Losses, int Draws, int BestStreak, int CardsOwned, int CardsTotal);

public record GuildBrief(string Name, string Tag, string Role);

/// <summary>Everything the profile screen shows about one player.</summary>
public record ProfileViewDto(
    Guid UserId,
    string Username,
    ShowcaseDto Showcase,
    ProfileCardDto Card,
    DateTime MemberSinceUtc,
    DateTime? LastSeenUtc,
    bool IsOnline,
    GuildBrief? Guild,
    int OnlineWins,
    int OnlineLosses,
    int OnlineDraws,
    string Relationship);

/// <summary>The greetings a player may pick. The game decides which are unlocked; the server only accepts these.</summary>
public static class Greetings
{
    public static readonly string[] All =
    {
        "Hello there!",
        "Let's have a great duel!",
        "Good luck, have fun!",
        "Show me what you've got!",
        "May the best duelist win.",
        "Ready when you are.",
        "Time to duel!",
        "I won't hold back.",
        "Good game, well played!",
        "Nobody beats my deck.",
        "Veterans never fade.",
        "Bow before the champion.",
        "The prestige is real.",
        "Let the cards decide."
    };
}

/// <summary>
/// PROFILE CARDS:
///   PUT /api/profile/card                 set my card
///   GET /api/users/{username}/profile     anyone's profile (404 if they blocked you or you blocked them)
/// </summary>
public static partial class ProfileCardEndpoints
{
    public const int MaxJsonLength = 3000;
    public const int MaxMain = 60;
    public const int MaxExtra = 15;

    [GeneratedRegex("^[A-Za-z0-9 '_.!&-]{0,20}$")]
    private static partial Regex DeckNameRule();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ProfileCardDto Empty => new("", "", "", "", 0, 0, 0, 0, 0, 0);

    public static ProfileCardDto Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<ProfileCardDto>(json, Json) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static IEndpointRouteBuilder MapProfileCardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/profile/card", Put).WithTags("Profile").RequireAuthorization();
        app.MapGet("/api/users/{username}/profile", Get).WithTags("Profile").RequireAuthorization();
        return app;
    }

    // Checks the deck text and tidies it. A card the server doesn't know (a brand new one) is simply left out;
    // a badly shaped list, more than 3 copies, or too many cards is refused (returns null).
    public static string? CleanDeck(string deck, CardCatalog catalog)
    {
        if (deck.Length == 0)
        {
            return "";
        }

        string[] parts = deck.Split('|');

        if (parts.Length > 2)
        {
            return null;
        }

        List<string> cleanParts = new();

        for (int part = 0; part < parts.Length; part++)
        {
            int total = 0;
            List<string> kept = new();

            foreach (string entry in parts[part].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = entry.Split(':');

                if (pair.Length != 2 || !int.TryParse(pair[1], out int copies) || copies < 1 || copies > 3)
                {
                    return null;
                }

                total += copies;

                if (catalog.Exists(pair[0]))
                {
                    kept.Add(pair[0] + ":" + copies);
                }
            }

            if (total > (part == 0 ? MaxMain : MaxExtra))
            {
                return null;
            }

            cleanParts.Add(string.Join(",", kept));
        }

        return string.Join("|", cleanParts);
    }

    private static async Task<IResult> Put(ProfileCardDto request, ClaimsPrincipal principal, AppDbContext db, CardCatalog catalog, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (request == null)
        {
            return Results.Problem("That profile card isn't valid.", statusCode: StatusCodes.Status400BadRequest);
        }

        string greeting = request.Greeting ?? "";
        string favCard = request.FavCard ?? "";

        if (favCard.Length > 0 && !catalog.Exists(favCard))
        {
            favCard = "";   // a card the server doesn't know yet
        }

        string deckName = (request.DeckName ?? "").Trim();
        string deck = request.Deck ?? "";

        string? cleanDeck = deck.Length <= 1500 ? CleanDeck(deck, catalog) : null;

        bool valid = (greeting.Length == 0 || Greetings.All.Contains(greeting))
            && DeckNameRule().IsMatch(deckName)
            && cleanDeck != null
            && InRange(request.Wins) && InRange(request.Losses) && InRange(request.Draws) && InRange(request.BestStreak)
            && request.CardsOwned is >= 0 and <= 10000 && request.CardsTotal is >= 0 and <= 10000;

        if (!valid)
        {
            return Results.Problem("That profile card isn't valid.", statusCode: StatusCodes.Status400BadRequest);
        }

        ProfileCardDto clean = new(greeting, favCard, deckName, cleanDeck!, request.Wins, request.Losses, request.Draws, request.BestStreak, request.CardsOwned, request.CardsTotal);
        string json = JsonSerializer.Serialize(clean, Json);

        if (json.Length > MaxJsonLength)
        {
            return Results.Problem("That profile card is too big.", statusCode: StatusCodes.Status400BadRequest);
        }

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Id == me, cancel);

        if (user == null) return Results.Unauthorized();

        user.ProfileJson = json;
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static bool InRange(int value) => value is >= 0 and <= 1_000_000;

    private static async Task<IResult> Get(string username, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        string normalized = (username ?? "").Trim().ToUpperInvariant();
        User? user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancel);

        // Blocked in either direction looks just like "doesn't exist".
        if (user == null || (user.Id != me && await SocialEndpoints.IsBlockedEitherWay(db, me, user.Id, cancel)))
        {
            return Results.NotFound();
        }

        Guid id = user.Id;

        var guild = await (from m in db.GuildMembers.AsNoTracking()
                           join g in db.Guilds.AsNoTracking() on m.GuildId equals g.Id
                           where m.UserId == id
                           select new { g.Name, g.Tag, m.Role }).FirstOrDefaultAsync(cancel);

        int wins = await db.DuelMatches.AsNoTracking().CountAsync(m => (m.Player0Id == id && m.WinnerSeat == 0) || (m.Player1Id == id && m.WinnerSeat == 1), cancel);
        int losses = await db.DuelMatches.AsNoTracking().CountAsync(m => (m.Player0Id == id && m.WinnerSeat == 1) || (m.Player1Id == id && m.WinnerSeat == 0), cancel);
        int draws = await db.DuelMatches.AsNoTracking().CountAsync(m => (m.Player0Id == id || m.Player1Id == id) && m.WinnerSeat < 0, cancel);

        string relationship = "none";

        if (id == me)
        {
            relationship = "self";
        }
        else
        {
            (Guid a, Guid b) = SocialEndpoints.Pair(me, id);
            Friendship? friendship = await db.Friendships.AsNoTracking().FirstOrDefaultAsync(f => f.UserAId == a && f.UserBId == b, cancel);

            if (friendship != null)
            {
                relationship = friendship.Status == FriendshipStatus.Accepted ? "friend" : (friendship.RequesterId == me ? "requested" : "incoming");
            }
        }

        DateTime now = DateTime.UtcNow;
        bool online = user.LastSeenUtc != null && now - user.LastSeenUtc.Value < SocialEndpoints.OnlineWindow;

        return Results.Ok(new ProfileViewDto(
            user.Id,
            user.Username,
            ShowcaseEndpoints.Of(user),
            Read(user.ProfileJson),
            user.CreatedUtc,
            user.LastSeenUtc,
            online,
            guild == null ? null : new GuildBrief(guild.Name, guild.Tag, guild.Role.ToString()),
            wins,
            losses,
            draws,
            relationship));
    }
}
