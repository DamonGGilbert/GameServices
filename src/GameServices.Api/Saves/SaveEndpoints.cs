using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace GameServices.Api.Saves;

public record SaveRequest(string Json, int ExpectedVersion);

public record SaveResponse(string GameId, int Version, DateTime UpdatedUtc, string Json);

/// <summary>
/// CLOUD SAVES. Each player has one save per game, so the card game and the
/// walking game each keep their own, all under the same account.
///
///   GET /api/games/{gameId}/save    download my save (404 if I have none yet)
///   PUT /api/games/{gameId}/save    upload my save
///
/// The server stores the save as an opaque JSON document: each game decides what
/// goes in it. That keeps this service game-agnostic.
///
/// OPTIMISTIC CONCURRENCY: every save has a version number. To upload you say
/// "I'm replacing version N". If someone (say, your phone) saved in the meantime,
/// the version is no longer N and the server answers 409 Conflict instead of
/// silently overwriting their progress. This is how real systems avoid lost updates.
/// </summary>
public static partial class SaveEndpoints
{
    private const int MaxJsonLength = 256 * 1024;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex GameIdRule();

    public static IEndpointRouteBuilder MapSaveEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/games/{gameId}/save")
            .WithTags("Cloud saves")
            .RequireAuthorization();

        group.MapGet("", Get);
        group.MapPut("", Put);

        return app;
    }

    private static async Task<IResult> Get(string gameId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!GameIdRule().IsMatch(gameId))
        {
            return BadGameId();
        }

        if (!TryGetUserId(principal, out Guid userId))
        {
            return Results.Unauthorized();
        }

        // Always filtered by the SIGNED-IN user's id (taken from the token, never from the request),
        // so nobody can read someone else's save by guessing.
        PlayerSave? save = await db.PlayerSaves.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.GameId == gameId, cancel);

        return save == null
            ? Results.Problem("No save yet for this game.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(new SaveResponse(save.GameId, save.Version, save.UpdatedUtc, save.Json));
    }

    private static async Task<IResult> Put(string gameId, SaveRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!GameIdRule().IsMatch(gameId))
        {
            return BadGameId();
        }

        if (!TryGetUserId(principal, out Guid userId))
        {
            return Results.Unauthorized();
        }

        Dictionary<string, string[]> problems = new();

        if (string.IsNullOrWhiteSpace(request?.Json))
        {
            problems["json"] = new[] { "The save can't be empty." };
        }
        else if (request.Json.Length > MaxJsonLength)
        {
            problems["json"] = new[] { $"The save is too big (limit {MaxJsonLength / 1024} KB)." };
        }
        else if (!IsValidJson(request.Json))
        {
            problems["json"] = new[] { "The save must be valid JSON." };
        }

        if (problems.Count > 0)
        {
            return Results.ValidationProblem(problems);
        }

        // Validation above guarantees the request and its JSON exist.
        string json = request!.Json;

        PlayerSave? save = await db.PlayerSaves.FirstOrDefaultAsync(s => s.UserId == userId && s.GameId == gameId, cancel);

        if (save == null)
        {
            // First ever save: the client must say "version 0" (it has never seen one).
            if (request.ExpectedVersion != 0)
            {
                return Conflict(0);
            }

            save = new PlayerSave { UserId = userId, GameId = gameId, Json = json, Version = 1, UpdatedUtc = DateTime.UtcNow };
            db.PlayerSaves.Add(save);
        }
        else
        {
            if (save.Version != request.ExpectedVersion)
            {
                return Conflict(save.Version);
            }

            save.Json = json;
            save.Version += 1;
            save.UpdatedUtc = DateTime.UtcNow;
        }

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            // DbUpdateConcurrencyException (a subclass) lands here too: two uploads arrived at
            // the same moment and the database let only one win.
            return Conflict(save.Version);
        }

        return Results.Ok(new SaveResponse(save.GameId, save.Version, save.UpdatedUtc, save.Json));
    }

    private static IResult Conflict(int currentVersion)
    {
        return Results.Problem(
            "Your save is out of date: it was changed from another device. Download the latest version first.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["currentVersion"] = currentVersion });
    }

    private static IResult BadGameId()
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["gameId"] = new[] { "Game id must be 2-40 characters: lowercase letters, numbers or hyphens." }
        });
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId)
    {
        return Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub), out userId);
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
