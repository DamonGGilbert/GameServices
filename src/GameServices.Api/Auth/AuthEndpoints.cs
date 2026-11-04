using GameServices.Api.Moderation;
using GameServices.Api.Walking.Monsters;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.RegularExpressions;
using GameServices.Api.Data;
using GameServices.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace GameServices.Api.Auth;

/// <summary>
/// The HTTP endpoints for accounts:
///   POST /api/auth/register   create an account
///   POST /api/auth/login      sign in (username OR email)
///   POST /api/auth/refresh    swap a refresh token for a new access token
///   POST /api/auth/logout     sign out (revokes the refresh token)
///   POST /api/auth/delete-account   delete it for good (AccountDeletion)
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
        auth.MapPost("/delete-account", AccountDeletion.Delete).RequireAuthorization().RetriesOnConflict();
        auth.MapPasswordReset();

        app.MapGet("/api/me", Me).WithTags("Account").RequireAuthorization();

        return app;
    }

    // ------------------------------------------------------------
    // REGISTER
    // ------------------------------------------------------------
    private static async Task<IResult> Register(
        RegisterRequest request,
        HttpContext http,
        AppDbContext db,
        TokenService tokens,
        IPasswordHasher<User> hasher,
        IConfiguration configuration,
        CancellationToken cancel)
    {
        // 1. Never trust the client: check everything on the server.
        Dictionary<string, string[]> problems = Validate(request);

        if (problems.Count > 0)
        {
            return Results.ValidationProblem(problems);
        }

        // Steplings accounts have a minimum age. The birthday is only looked at here - it isn't kept.
        DateTime now = DateTime.UtcNow;
        bool walking = string.Equals(http.Request.Headers[GameIds.Header].ToString().Trim(), GameIds.Walking, StringComparison.OrdinalIgnoreCase);
        if (walking)
        {
            if (!AgeRules.IsRealBirthDate(request.BirthYear, request.BirthMonth, now))
            {
                return Problem("Please tell us the month and year you were born.", StatusCodes.Status400BadRequest, "birth_date_needed");
            }

            int minimum = configuration.GetValue("Accounts:MinimumAge", AgeRules.DefaultMinimumAge);
            if (AgeRules.AgeIn(request.BirthYear!.Value, request.BirthMonth!.Value, now) < minimum)
            {
                return Problem("Sorry - Steplings accounts are for players aged " + minimum + " and over.", StatusCodes.Status403Forbidden, "too_young");
            }
        }

        // Groundwork: the clan chosen for the starter, if the game asked (ClanChooser in the game: switched off for now).
        Clan? clan = null;
        if (walking && !string.IsNullOrWhiteSpace(request.Clan))
        {
            clan = Clans.Parse(request.Clan);
            if (clan == null) return Problem("Please choose one of the six clans.", StatusCodes.Status400BadRequest, "unknown_clan");
        }

        string username = request.Username.Trim();
        string normalizedUsername = username.ToUpperInvariant();
        string? email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        string? normalizedEmail = email?.ToUpperInvariant();

        // 2. Friendly duplicate check (the unique index below is the real safety net).
        if (await db.Users.AnyAsync(u => u.NormalizedUsername == normalizedUsername, cancel))
        {
            return Problem("That username is already taken. If it is yours, sign in instead - one account works in all our games.", StatusCodes.Status409Conflict, "username_taken");
        }

        if (normalizedEmail != null && await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancel))
        {
            return Problem("You already have an account. Sign in instead. (One account works in all our games - use the same username and password.)", StatusCodes.Status409Conflict, "email_already_registered");
        }

        // 3. Create the account. Only the HASH of the password is stored.
        User user = new()
        {
            Username = username,
            NormalizedUsername = normalizedUsername,
            Email = email,
            NormalizedEmail = normalizedEmail,
            LastLoginUtc = now,
            AgeConfirmedUtc = walking ? now : null
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
            return Problem("That username or email already has an account. Try signing in instead.", StatusCodes.Status409Conflict, "account_exists");
        }

        if (walking)
        {
            Walking.Progress.ActivityLog.Add(db, user.Id, "account", "Created");
            await db.SaveChangesAsync(cancel);
        }

        if (clan != null) await GiveStarter(http, db, user.Id, clan.Value, cancel);

        return Results.Created($"/api/me", response);
    }

    // ------------------------------------------------------------
    // LOGIN
    // ------------------------------------------------------------
    private static async Task<IResult> Login(
        LoginRequest request,
        HttpContext http,
        AppDbContext db,
        TokenService tokens,
        IPasswordHasher<User> hasher,
        CancellationToken cancel)
    {
        // Players can type either their username or their email address.
        string normalized = (request.Username ?? "").Trim().ToUpperInvariant();
        User? user = normalized.Contains('@')
            ? await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancel)
            : await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancel);

        // The SAME vague answer whether the username or the password was wrong,
        // so attackers can't use this to discover which usernames exist.
        IResult invalid = Problem("That username (or email) and password do not match. Check them, or use \"Forgot password\". One account works in all our games.", StatusCodes.Status401Unauthorized, "invalid_credentials");

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

        if (user.SuspendedUntilUtc > DateTime.UtcNow)
        {
            return Problem("This account is paused until " + user.SuspendedUntilUtc.Value.ToString("d MMMM yyyy") + " after a report from another player.", StatusCodes.Status403Forbidden, "suspended");
        }

        if (string.Equals(http.Request.Headers[GameIds.Header].ToString().Trim(), GameIds.Walking, StringComparison.OrdinalIgnoreCase))
        {
            Walking.Progress.ActivityLog.Add(db, user.Id, "sign-in", "");
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

    // The starter in the chosen clan, straight away (otherwise the first visit brings one in any clan). If two sign-ups
    // take the same serial number one has to try again; if it keeps failing the account is still fine, just without the choice.
    private static async Task GiveStarter(HttpContext http, AppDbContext db, Guid owner, Clan clan, CancellationToken cancel)
    {
        MonsterFinder finder = http.RequestServices.GetRequiredService<MonsterFinder>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            (string species, int tier, long seed, int potential) = finder.RollStarter(clan);
            await MonsterEndpoints.Add(db, owner, species, tier, seed, potential, finder.Catalog.RulesVersion, "starter", null, DateTime.UtcNow.ToString("yyyy-MM-dd"), cancel);
            try
            {
                await db.SaveChangesAsync(cancel);
                return;
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private static string DummyHash(IPasswordHasher<User> hasher)
    {
        return dummyHash ??= hasher.HashPassword(new User(), "not-a-real-password");
    }

    // Every problem carries a short "code" next to the readable message, so a game can react
    // (for example, flip to its sign-in form on "email_already_registered") without reading English.
    private static IResult Problem(string message, int status, string code)
    {
        return Results.Problem(message, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
    }

    private static Dictionary<string, string[]> Validate(RegisterRequest request)
    {
        Dictionary<string, string[]> problems = new();

        if (request == null || string.IsNullOrWhiteSpace(request.Username) || !UsernameRule().IsMatch(request.Username.Trim()))
        {
            problems["username"] = new[] { "Usernames are 3-20 characters long: letters, numbers or underscores only (no spaces)." };
        }
        else if (!ContentFilter.IsCleanName(request.Username.Trim()))
        {
            problems["username"] = new[] { "That username isn't allowed. Please pick a different one." };
        }

        if (string.IsNullOrEmpty(request?.Password) || request.Password.Length < 8)
        {
            problems["password"] = new[] { "Your password needs at least 8 characters." };
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
                problems["email"] = new[] { "That doesn't look like a valid email address. Check for typos, e.g. name@example.com." };
            }
        }

        return problems;
    }
}
