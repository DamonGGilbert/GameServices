using System.Security.Claims;
using System.Text.RegularExpressions;
using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Social;

/// <summary>What a player shows off: level, prestige rank, emblem, avatar frame and a title.</summary>
public record ShowcaseDto(int Level, int Prestige, int Emblem, int Frame, string Title);

/// <summary>The look of a player's side of the table, as JSON text.</summary>
public record CosmeticsRequest(string Json);

/// <summary>
/// SHOWCASE:  PUT /api/profile/showcase
///
/// Prestige and level live in the game (single player progress), so the game tells the server what to show.
/// It is cosmetic only -- it never affects rewards -- so the server just keeps it tidy: values must be in
/// range, the emblem and frame can't be higher than the prestige rank, and the title is plain letters.
/// </summary>
public static partial class ShowcaseEndpoints
{
    public const int MaxLevel = 50;
    public const int MaxPrestige = 10;
    public const int MaxTitleLength = 24;

    [GeneratedRegex("^[A-Za-z' -]{0,24}$")]
    private static partial Regex TitleRule();

    public static ShowcaseDto Of(User user) => new(user.ShowLevel, user.ShowPrestige, user.ShowEmblem, user.ShowFrame, user.ShowTitle);

    public static IEndpointRouteBuilder MapShowcaseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/profile/showcase", Set).WithTags("Profile").RequireAuthorization();
        app.MapPut("/api/profile/cosmetics", SetCosmetics).WithTags("Profile").RequireAuthorization();
        return app;
    }

    public const int MaxCosmeticsLength = 2000;

    // The look of your side of the table, as the game's own JSON. Kept as text; it must be one JSON object and small.
    private static async Task<IResult> SetCosmetics(CosmeticsRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        string json = (request?.Json ?? "").Trim();

        bool valid = json.Length <= MaxCosmeticsLength;

        if (valid && json.Length > 0)
        {
            try
            {
                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
                valid = document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
            }
            catch (System.Text.Json.JsonException)
            {
                valid = false;
            }
        }

        if (!valid)
        {
            return Results.Problem("That look isn't valid.", statusCode: StatusCodes.Status400BadRequest);
        }

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Id == me, cancel);

        if (user == null) return Results.Unauthorized();

        user.CosmeticsJson = json;
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static async Task<IResult> Set(ShowcaseDto request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        string title = (request?.Title ?? "").Trim();

        if (request == null
            || request.Level < 1 || request.Level > MaxLevel
            || request.Prestige < 0 || request.Prestige > MaxPrestige
            || request.Emblem < 0 || request.Emblem > request.Prestige
            || request.Frame < 0 || request.Frame > request.Prestige
            || !TitleRule().IsMatch(title))
        {
            return Results.Problem("That showcase isn't valid.", statusCode: StatusCodes.Status400BadRequest);
        }

        User? user = await db.Users.FirstOrDefaultAsync(u => u.Id == me, cancel);

        if (user == null) return Results.Unauthorized();

        user.ShowLevel = request.Level;
        user.ShowPrestige = request.Prestige;
        user.ShowEmblem = request.Emblem;
        user.ShowFrame = request.Frame;
        user.ShowTitle = title;
        await db.SaveChangesAsync(cancel);

        return Results.NoContent();
    }
}
