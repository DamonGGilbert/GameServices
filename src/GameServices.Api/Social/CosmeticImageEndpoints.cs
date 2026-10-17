using System.Security.Claims;
using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace GameServices.Api.Social;

/// <summary>
/// CUSTOM PLAYMAT AND SLEEVE PICTURES (for players who have reached the level cap or prestiged).
///   PUT    /api/profile/cosmetic-image/{kind}            my picture (kind = playmat or sleeve; the raw JPEG/PNG bytes)
///   DELETE /api/profile/cosmetic-image/{kind}            remove it
///   GET    /api/users/{username}/cosmetic-image/{kind}   someone's picture
///
/// WHO CAN SEE IT: only the owner, their friends and their guildmates. Anyone else gets "not found" and the game shows
/// a normal sleeve or mat instead. A picture a player chose themselves is never forced on a stranger.
/// </summary>
public static class CosmeticImageEndpoints
{
    public const int MaxBytes = 300 * 1024;
    public const int RequiredLevel = 50;

    public static IEndpointRouteBuilder MapCosmeticImageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/profile/cosmetic-image/{kind}", Put).WithTags("Profile").RequireAuthorization();
        app.MapDelete("/api/profile/cosmetic-image/{kind}", Delete).WithTags("Profile").RequireAuthorization();
        app.MapGet("/api/users/{username}/cosmetic-image/{kind}", Get).WithTags("Profile").RequireAuthorization();
        return app;
    }

    public static bool TryKind(string kind, out int value)
    {
        value = kind.ToLowerInvariant() switch { "playmat" => 0, "sleeve" => 1, _ => -1 };
        return value >= 0;
    }

    private static async Task<IResult> Put(string kind, HttpRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!TryKind(kind, out int kindValue))
        {
            return Results.Problem("The kind must be playmat or sleeve.", statusCode: StatusCodes.Status400BadRequest);
        }

        // The level and prestige are what the game last told the server (they live in the game's own progress), so this is
        // a courtesy gate, not security: all it unlocks is how YOUR table looks to your friends.
        User user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == me, cancel);

        if (user.ShowPrestige < 1 && user.ShowLevel < RequiredLevel)
        {
            return Results.Problem($"Custom images unlock at level {RequiredLevel} or Prestige 1.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (request.ContentLength is > MaxBytes)
        {
            return Results.Problem("That picture is too big (the limit is 300 KB).", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, cancel)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > MaxBytes)
            {
                return Results.Problem("That picture is too big (the limit is 300 KB).", statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        }

        byte[] bytes = buffer.ToArray();
        string? type = AvatarEndpoints.Sniff(bytes);

        if (type == null)
        {
            return Results.Problem("The picture must be a JPEG or PNG.", statusCode: StatusCodes.Status400BadRequest);
        }

        UserCosmeticImage? existing = await db.CosmeticImages.FirstOrDefaultAsync(i => i.UserId == me && i.Kind == kindValue, cancel);

        if (existing == null)
        {
            db.CosmeticImages.Add(new UserCosmeticImage { UserId = me, Kind = kindValue, Data = bytes, ContentType = type, UpdatedUtc = DateTime.UtcNow });
        }
        else
        {
            existing.Data = bytes;
            existing.ContentType = type;
            existing.UpdatedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static async Task<IResult> Delete(string kind, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!TryKind(kind, out int kindValue))
        {
            return Results.Problem("The kind must be playmat or sleeve.", statusCode: StatusCodes.Status400BadRequest);
        }

        await db.CosmeticImages.Where(i => i.UserId == me && i.Kind == kindValue).ExecuteDeleteAsync(cancel);
        return Results.NoContent();
    }

    private static async Task<IResult> Get(string username, string kind, ClaimsPrincipal principal, HttpRequest request, HttpResponse response, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (!TryKind(kind, out int kindValue))
        {
            return Results.NotFound();
        }

        string normalized = (username ?? "").Trim().ToUpperInvariant();
        User? owner = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancel);

        if (owner == null)
        {
            return Results.NotFound();
        }

        // Only the owner, friends and guildmates may see it (everyone else is told "not found").
        if (owner.Id != me && !await MaySee(db, me, owner.Id, cancel))
        {
            return Results.NotFound();
        }

        UserCosmeticImage? image = await db.CosmeticImages.AsNoTracking().FirstOrDefaultAsync(i => i.UserId == owner.Id && i.Kind == kindValue, cancel);

        if (image == null)
        {
            return Results.NotFound();
        }

        string etag = "\"" + image.UpdatedUtc.Ticks + "\"";
        response.Headers[HeaderNames.ETag] = etag;
        response.Headers[HeaderNames.CacheControl] = "private, max-age=60";

        if (request.Headers.IfNoneMatch == etag)
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.File(image.Data, image.ContentType);
    }

    private static async Task<bool> MaySee(AppDbContext db, Guid viewer, Guid owner, CancellationToken cancel)
    {
        if (await SocialEndpoints.IsBlockedEitherWay(db, viewer, owner, cancel))
        {
            return false;
        }

        if (await SocialEndpoints.AreFriends(db, viewer, owner, cancel))
        {
            return true;
        }

        // Guildmates may see it too - in any game, now that a player can be in one guild per game.
        IQueryable<Guid> viewerGuilds = db.GuildMembers.AsNoTracking().Where(m => m.UserId == viewer).Select(m => m.GuildId);

        return await db.GuildMembers.AsNoTracking().AnyAsync(m => m.UserId == owner && viewerGuilds.Contains(m.GuildId), cancel);
    }
}
