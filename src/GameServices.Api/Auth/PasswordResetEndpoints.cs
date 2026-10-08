using System.Security.Cryptography;
using GameServices.Api.Data;
using GameServices.Api.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Auth;

public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(string Email, string Code, string NewPassword);

/// <summary>
/// FORGOT PASSWORD, the safe way.
///
///   POST /api/auth/forgot-password   "I forgot it": emails a short one-time CODE.
///   POST /api/auth/reset-password    send the email + the code + a new password.
///
/// Security decisions (each one is tested):
///   * The server always answers the same way, whether the email exists or not, so nobody can use
///     this to find out who has an account.
///   * Codes are random, last 15 minutes, work once, and die after 5 wrong guesses.
///   * Only a HASH of the code is stored, like a password.
///   * A successful reset signs the account out everywhere (all refresh tokens are revoked).
///   * At most 3 codes per hour per account, and the whole group is rate limited per IP.
/// </summary>
public static class PasswordResetEndpoints
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);
    public const int MaxWrongGuesses = 5;
    public const int MaxCodesPerHour = 3;

    // No 0/O or 1/I, so the code can't be misread.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 8;

    private static readonly string[] SameAnswer = { "If that email belongs to an account, we've sent it a code. It lasts 15 minutes." };

    public static RouteGroupBuilder MapPasswordReset(this RouteGroupBuilder auth)
    {
        auth.MapPost("/forgot-password", Forgot);
        auth.MapPost("/reset-password", Reset);
        return auth;
    }

    private static async Task<IResult> Forgot(ForgotPasswordRequest request, AppDbContext db, IEmailSender email, CancellationToken cancel)
    {
        string normalized = (request?.Email ?? "").Trim().ToUpperInvariant();

        if (normalized.Length == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = new[] { "Type the email address you signed up with." } });
        }

        User? user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancel);

        if (user?.Email != null)
        {
            DateTime now = DateTime.UtcNow;
            int recent = await db.PasswordResetCodes.CountAsync(c => c.UserId == user.Id && c.CreatedUtc > now.AddHours(-1), cancel);

            // Anti-spam: asking again and again doesn't send more emails.
            if (recent < MaxCodesPerHour)
            {
                string code = NewCode();

                db.PasswordResetCodes.Add(new PasswordResetCode
                {
                    UserId = user.Id,
                    CodeHash = HashCode(user.Id, code),
                    CreatedUtc = now,
                    ExpiresUtc = now + CodeLifetime
                });

                await db.SaveChangesAsync(cancel);

                await email.SendAsync(
                    user.Email,
                    "Your password reset code",
                    $"Hi {user.Username},\n\nYour code to reset your password is:\n\n    {code[..4]}-{code[4..]}\n\n" +
                    "It works for 15 minutes and can only be used once. If you didn't ask for this, you can ignore this email: your password hasn't changed.\n",
                    cancel);
            }
        }

        // The same words every time.
        return Results.Ok(new { message = SameAnswer[0] });
    }

    private static async Task<IResult> Reset(ResetPasswordRequest request, AppDbContext db, IPasswordHasher<User> hasher, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(request?.NewPassword) || request.NewPassword.Length < 8 || request.NewPassword.Length > 100)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = new[] { "Your new password must be 8-100 characters." } });
        }

        IResult invalid = Results.Problem("That code isn't valid, or it has expired. Ask for a new one.", statusCode: StatusCodes.Status400BadRequest);

        string normalizedEmail = (request.Email ?? "").Trim().ToUpperInvariant();
        string code = new string((request.Code ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        User? user = normalizedEmail.Length == 0 ? null : await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancel);

        if (user == null || code.Length != CodeLength)
        {
            return invalid;
        }

        DateTime now = DateTime.UtcNow;

        // Only the newest code counts.
        PasswordResetCode? stored = await db.PasswordResetCodes
            .Where(c => c.UserId == user.Id && c.UsedUtc == null && c.ExpiresUtc > now)
            .OrderByDescending(c => c.CreatedUtc)
            .FirstOrDefaultAsync(cancel);

        if (stored == null || stored.Attempts >= MaxWrongGuesses)
        {
            return invalid;
        }

        bool matches = CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(stored.CodeHash),
            System.Text.Encoding.UTF8.GetBytes(HashCode(user.Id, code)));

        if (!matches)
        {
            stored.Attempts += 1;
            await db.SaveChangesAsync(cancel);
            return invalid;
        }

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
        stored.UsedUtc = now;

        // Every code for this account is finished, and everyone signed in is signed out.
        await db.PasswordResetCodes.Where(c => c.UserId == user.Id && c.UsedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedUtc, now), cancel);
        await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, now), cancel);

        await db.SaveChangesAsync(cancel);

        return Results.Ok(new { message = "Your password has been changed. You can sign in now." });
    }

    private static string NewCode()
    {
        char[] letters = new char[CodeLength];

        for (int i = 0; i < letters.Length; i++)
        {
            letters[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(letters);
    }

    private static string HashCode(Guid userId, string code) => TokenService.Hash(userId.ToString("N") + ":" + code);
}

/// <summary>Admin commands you run on the server machine itself (never over the internet).</summary>
public static class AdminCommands
{
    /// <summary>
    /// dotnet run -- reset-password Username   (the new password comes from the NEW_PASSWORD
    /// environment variable, so it never appears in the command line history).
    /// </summary>
    public static async Task<bool> TryRun(IServiceProvider services, string[] args)
    {
        if (args.Length < 2 || !string.Equals(args[0], "reset-password", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? password = Environment.GetEnvironmentVariable("NEW_PASSWORD");

        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            Console.WriteLine("Set the NEW_PASSWORD environment variable (at least 8 characters) first.");
            return true;
        }

        using IServiceScope scope = services.CreateScope();
        bool done = await ResetPassword(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(), args[1], password);

        Console.WriteLine(done ? $"Password for '{args[1]}' has been reset and they were signed out everywhere." : $"No account called '{args[1]}'.");
        return true;
    }

    public static async Task<bool> ResetPassword(AppDbContext db, IPasswordHasher<User> hasher, string username, string newPassword)
    {
        string normalized = username.Trim().ToUpperInvariant();
        User? user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized);

        if (user == null)
        {
            return false;
        }

        user.PasswordHash = hasher.HashPassword(user, newPassword);
        DateTime now = DateTime.UtcNow;

        await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, now));
        await db.SaveChangesAsync();
        return true;
    }
}
