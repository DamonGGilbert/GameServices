using System.Security.Claims;
using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace GameServices.Api.Social;

/// <summary>
/// PROFILE PICTURES.
///   PUT /api/profile/avatar              my picture (the game sends a small JPEG or PNG as the raw request body)
///   GET /api/users/{username}/avatar     anyone's picture (404 if they haven't set one)
///
/// The game shrinks the picture to 128 x 128 before sending, so a picture is a few KB. The server still checks
/// it: size limit, and the first bytes must really be a JPEG or a PNG (so nobody can park other files here).
/// </summary>
public static class AvatarEndpoints
{
    public const int MaxBytes = 150 * 1024;

    public static IEndpointRouteBuilder MapAvatarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/api/profile/avatar", Put).WithTags("Profile").RequireAuthorization();
        app.MapGet("/api/users/{username}/avatar", Get).WithTags("Profile").RequireAuthorization();
        return app;
    }

    public static string? Sniff(byte[] bytes)
    {
        if (bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        return null;
    }

    private static async Task<IResult> Put(HttpRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (request.ContentLength is > MaxBytes)
        {
            return Results.Problem("That picture is too big (the limit is 150 KB).", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int read;

        while ((read = await request.Body.ReadAsync(chunk, cancel)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > MaxBytes)
            {
                return Results.Problem("That picture is too big (the limit is 150 KB).", statusCode: StatusCodes.Status413PayloadTooLarge);
            }
        }

        byte[] bytes = buffer.ToArray();
        string? type = Sniff(bytes);

        if (type == null)
        {
            return Results.Problem("The picture must be a JPEG or PNG.", statusCode: StatusCodes.Status400BadRequest);
        }

        UserAvatar? existing = await db.Avatars.FirstOrDefaultAsync(a => a.UserId == me, cancel);

        if (existing == null)
        {
            db.Avatars.Add(new UserAvatar { UserId = me, Data = bytes, ContentType = type, UpdatedUtc = DateTime.UtcNow });
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

    private static async Task<IResult> Get(string username, HttpRequest request, HttpResponse response, AppDbContext db, CancellationToken cancel)
    {
        string normalized = (username ?? "").Trim().ToUpperInvariant();

        UserAvatar? avatar = await (from u in db.Users.AsNoTracking()
                                    join a in db.Avatars.AsNoTracking() on u.Id equals a.UserId
                                    where u.NormalizedUsername == normalized
                                    select a).FirstOrDefaultAsync(cancel);

        if (avatar == null)
        {
            return Results.NotFound();
        }

        // The picture rarely changes: let the game keep it and just ask "has it changed?".
        string etag = "\"" + avatar.UpdatedUtc.Ticks + "\"";
        response.Headers[HeaderNames.ETag] = etag;
        response.Headers[HeaderNames.CacheControl] = "private, max-age=60";

        if (request.Headers.IfNoneMatch == etag)
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.File(avatar.Data, avatar.ContentType);
    }
}
