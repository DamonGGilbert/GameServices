using GameServices.Api.Moderation;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.RegularExpressions;
using GameServices.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace GameServices.Api.Auth;

/// <summary>
/// The HTTP endpoints for accounts:
///   POST /api/auth/register   create an account
///   POST /api/auth/login      sign in
///   POST /api/auth/refresh    swap a refresh token for a new access token
///   POST /api/auth/logout     sign out (revokes the refresh token)
///   GET  /api/me              who am I? (needs a valid access token)
/// </summary>
public static partial class AuthEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9_]{3,20}$")]
    private static partial Regex UsernameRule();

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // "auth" rate limit: stops someone trying thousands of passwords per minute.
        RouteGroupBuilder auth = app.MapGroup("/api/auth").WithTags("Auth").RequireRateLimiting("auth");

        auth.MapPost("/register", Register);
        auth.MapPost("/login", Login);
        auth.MapPost("/refresh", Refresh);
        auth.MapPost("/logout", Logout);
        auth.MapPasswordReset();

        app.MapGet("/api/me", Me).WithTags("Account").RequireAuthorization();

        return app;
    }

    // ------------------------------------------------------------
    // REGISTER
    // ------------------------------------------------------------
    private static async Task<IResult> Register(
        RegisterRequest request,
        AppDbContext db,
        TokenService tokens,
        IPasswordHasher<User> hasher,
        CancellationToken cancel)
    {
        // 1. Never trust the client: check everything on the server.
        Dictionary<string, string[]> problems = Validate(request);

        if (problems.Count > 0)
        {
            return Results.ValidationProblem(problems);
        }

        string username = request.Username.Trim();
        string normalizedUsername = username.ToUpperInvariant();
        string? email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        string? normalizedEmail = email?.ToUpperInvariant();

        // 2. Friendly duplicate check (the unique index below is the real safety net).
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalizedUsername, cancel))
        {
            return Results.Problem("That username is already taken.", statusCode: StatusCodes.Status409Conflict);
        }

        if (normalizedEmail != null && await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancel))
        {
            return Results.Problem("That email is already registered.", statusCode: StatusCodes.Status409Conflict);
        }

        // 3. Create the account. Only the HASH of the password is stored.
        User user = new()
        {
            Username = username,
            NormalizedUsername = normalizedUsername,
            Email = email,
            NormalizedEmail = normalizedEmail,
            LastLoginUtc = DateTime.UtcNow
        };

        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);

        AuthResponse response = IssueTokens(user, tokens, db);

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            // Two people registered the same name at the same instant: the database
            // unique index rejected the second one.
            return Results.Problem("That username or email is already taken.", statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Created($"/api/me", response);
    }

    // ------------------------------------------------------------
    // LOGIN
    // ------------------------------------------------------------
    private static async Task<IResult> Login(
        LoginRequest request,
        AppDbContext db,
        TokenService tokens,
        IPasswordHasher<User> hasher,
        CancellationToken cancel)
    {
        string normalized = (request.Username ?? "").Trim().ToUpperInvariant();
        User? user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancel);

        // The SAME vague answer whether the username or the password was wrong,
        // so attackers can't use this to discover which usernames exist.
        IResult invalid = Results.Problem("Invalid username or password.", statusCode: StatusCodes.Status401Unauthorized);

        if (user == null)
        {
            // Do a fake password check so a missing user takes as long as a real one
            // (otherwise the response time leaks whether the username exists).
            hasher.VerifyHashedPassword(new User(), DummyHash(hasher), request.Password ?? "");
            return invalid;
        }

        PasswordVerificationResult result = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password ?? "");

        if (result == PasswordVerificationResult.Failed)
        {
            return invalid;
        }

        // If we've since made the hashing stronger, upgrade this player's hash silently.
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password!);
        }

        user.LastLoginUtc = DateTime.UtcNow;

        AuthResponse response = IssueTokens(user, tokens, db);
        await db.SaveChangesAsync(cancel);

        return Results.Ok(response);
    }

    // ------------------------------------------------------------
    // REFRESH  (token rotation)
    // ------------------------------------------------------------
    private static async Task<IResult> Refresh(
        RefreshRequest request,
        AppDbContext db,
        TokenService tokens,
        CancellationToken cancel)
    {
        IResult invalid = Results.Problem("Invalid refresh token.", statusCode: StatusCodes.Status401Unauthorized);

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return invalid;
        }

        string hash = TokenService.Hash(request.RefreshToken);

        RefreshToken? stored = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancel);

        if (stored == null)
        {
            return invalid;
        }

        // REUSE DETECTION: a revoked token is being presented again. Either the
        // player's device or a thief has an old copy -- we can't tell which, so
        // we sign this account out everywhere. The real player just logs in again.
        if (stored.RevokedUtc != null)
        {
            List<RefreshToken> all = await db.RefreshTokens
                .Where(t => t.UserId == stored.UserId && t.RevokedUtc == null)
                .ToListAsync(cancel);

            foreach (RefreshToken token in all)
            {
                token.RevokedUtc = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(cancel);
            return invalid;
        }

        if (!stored.IsActive)
        {
            return invalid;
        }

        // ROTATION: each refresh token works exactly once. Using it gives a brand
        // new pair and retires the old one.
        (string raw, RefreshToken fresh) = tokens.CreateRefreshToken(stored.User);

        stored.RevokedUtc = DateTime.UtcNow;
        stored.ReplacedByTokenHash = fresh.TokenHash;
        db.RefreshTokens.Add(fresh);

        AccessToken access = tokens.CreateAccessToken(stored.User);

        await db.SaveChangesAsync(cancel);

        return Results.Ok(new AuthResponse(ToDto(stored.User), access.Value, access.ExpiresUtc, raw));
    }

    // ------------------------------------------------------------
    // LOGOUT
    // ------------------------------------------------------------
    private static async Task<IResult> Logout(RefreshRequest request, AppDbContext db, CancellationToken cancel)
    {
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            string hash = TokenService.Hash(request.RefreshToken);
            RefreshToken? stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancel);

            if (stored != null && stored.RevokedUtc == null)
            {
                stored.RevokedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancel);
            }
        }

        // Always "OK": logging out twice is not an error.
        return Results.NoContent();
    }

    // ------------------------------------------------------------
    // ME
    // ------------------------------------------------------------
    private static async Task<IResult> Me(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        string? id = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

        if (!Guid.TryParse(id, out Guid userId))
        {
            return Results.Unauthorized();
        }

        User? user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancel);

        return user == null ? Results.Unauthorized() : Results.Ok(ToDto(user));
    }

    // ------------------------------------------------------------
    // HELPERS
    // ------------------------------------------------------------
    private static AuthResponse IssueTokens(User user, TokenService tokens, AppDbContext db)
    {
        AccessToken access = tokens.CreateAccessToken(user);
        (string raw, RefreshToken row) = tokens.CreateRefreshToken(user);

        // Adding to the user's collection also links the foreign key.
        user.RefreshTokens.Add(row);
        db.Entry(row).State = EntityState.Added;

        return new AuthResponse(ToDto(user), access.Value, access.ExpiresUtc, raw);
    }

    private static UserDto ToDto(User user) => new(user.Id, user.Username, user.CreatedUtc);

    private static string? dummyHash;

    private static string DummyHash(IPasswordHasher<User> hasher)
    {
        return dummyHash ??= hasher.HashPassword(new User(), "not-a-real-password");
    }

    private static Dictionary<string, string[]> Validate(RegisterRequest request)
    {
        Dictionary<string, string[]> problems = new();

        if (request == null || string.IsNullOrWhiteSpace(request.Username) || !UsernameRule().IsMatch(request.Username.Trim()))
        {
            problems["username"] = new[] { "Username must be 3-20 characters: letters, numbers or underscore." };
        }
        else if (!ContentFilter.IsCleanName(request.Username.Trim()))
        {
            problems["username"] = new[] { "That username isn't allowed - please pick another." };
        }

        if (string.IsNullOrEmpty(request?.Password) || request.Password.Length < 8)
        {
            problems["password"] = new[] { "Password must be at least 8 characters." };
        }
        else if (request.Password.Length > 100)
        {
            problems["password"] = new[] { "Password can be at most 100 characters." };
        }

        if (!string.IsNullOrWhiteSpace(request?.Email))
        {
            bool valid = MailAddress.TryCreate(request.Email.Trim(), out MailAddress? address) &&
                         address.Address == request.Email.Trim() &&
                         request.Email.Length <= 254;

            if (!valid)
            {
                problems["email"] = new[] { "That doesn't look like a valid email address." };
            }
        }

        return problems;
    }
}
