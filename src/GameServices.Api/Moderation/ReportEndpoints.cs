using System.Security.Claims;
using System.Text;
using GameServices.Api.Data;
using GameServices.Api.Social;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Moderation;

public record ReportRequest(string Username, string Reason, string? Details = null);

/// <summary>
/// REPORT A PLAYER:  POST /api/reports  { username, reason, details }
///
/// Anyone can report anyone they meet in the game. A copy of what's needed to look into it is kept with the report - the
/// last messages they sent the reporter, their title and their monsters' names - so it's still there if it gets deleted.
/// The person reported is never told who reported them. Reports are read with ReportAdmin on the server.
/// </summary>
public static class ReportEndpoints
{
    /// <summary>The reasons a player can choose from (the game shows them in plain words).</summary>
    public static readonly string[] Reasons = { "name", "bullying", "personal", "grownup", "cheating", "other" };

    public const int MaxPerDay = 20;
    private const int MessagesKept = 10;
    private const int DetailsLength = 500;

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/reports", Report).WithTags("Moderation").RequireAuthorization();
        return app;
    }

    private static IResult Problem(string message, int status = StatusCodes.Status400BadRequest) => Results.Problem(message, statusCode: status);

    private static async Task<IResult> Report(ReportRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (request == null || !Reasons.Contains(request.Reason)) return Problem("Pick a reason for your report.");

        string normalized = (request.Username ?? "").Trim().ToUpperInvariant();
        User? reported = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUsername == normalized, cancel);
        if (reported == null) return Problem("We couldn't find that player.", StatusCodes.Status404NotFound);
        if (reported.Id == me) return Problem("You can't report yourself.");

        DateTime now = DateTime.UtcNow;
        DateTime since = now.AddDays(-1);
        if (await db.PlayerReports.CountAsync(r => r.ReporterId == me && r.CreatedUtc > since, cancel) >= MaxPerDay)
        {
            return Problem("You've sent a lot of reports today - we'll read those first. Try again tomorrow.", StatusCodes.Status429TooManyRequests);
        }

        string details = (request.Details ?? "").Trim();
        db.PlayerReports.Add(new PlayerReport
        {
            ReporterId = me,
            ReportedUserId = reported.Id,
            ReportedName = reported.Username,
            Reason = request.Reason,
            Details = details.Length > DetailsLength ? details[..DetailsLength] : details,
            Evidence = await Evidence(db, reported.Id, me, cancel),
            CreatedUtc = now,
        });
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static async Task<string> Evidence(AppDbContext db, Guid reported, Guid reporter, CancellationToken cancel)
    {
        var text = new StringBuilder();

        string? title = await db.WalkingProfiles.AsNoTracking().Where(p => p.UserId == reported).Select(p => p.Title).FirstOrDefaultAsync(cancel);
        if (!string.IsNullOrEmpty(title)) text.AppendLine("Title: " + title);

        List<string> names = await db.WalkingMonsters.AsNoTracking().Where(m => m.OwnerId == reported && m.Nickname != "").Select(m => m.Nickname).ToListAsync(cancel);
        if (names.Count > 0) text.AppendLine("Monster names: " + string.Join(", ", names));

        List<ChatMessage> messages = await db.ChatMessages.AsNoTracking()
            .Where(m => m.SenderId == reported && m.RecipientId == reporter)
            .OrderByDescending(m => m.Id).Take(MessagesKept).ToListAsync(cancel);
        foreach (ChatMessage m in Enumerable.Reverse(messages)) text.AppendLine(m.SentUtc.ToString("yyyy-MM-dd HH:mm") + ": " + m.Text);

        return text.ToString();
    }
}

/// <summary>
/// READING REPORTS, on the server machine:
///     dotnet GameServices.Api.dll reports                        (the open ones, oldest first)
///     dotnet GameServices.Api.dll close-report abc123 Spoke to them
///     dotnet GameServices.Api.dll suspend SomeUser 7             (can't sign in for 7 days; signed out everywhere)
///     dotnet GameServices.Api.dll unsuspend SomeUser
/// </summary>
public static class ReportAdmin
{
    public static async Task<bool> TryRun(IServiceProvider services, string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (command is not ("reports" or "close-report" or "suspend" or "unsuspend")) return false;

        using IServiceScope scope = services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;

        switch (command)
        {
            case "reports":
                List<string> open = await OpenReports(db);
                Console.WriteLine(open.Count == 0 ? "No open reports." : string.Join(Environment.NewLine + Environment.NewLine, open));
                break;
            case "close-report" when args.Length >= 2:
                Console.WriteLine(await CloseReport(db, args[1], string.Join(' ', args.Skip(2)), now) ? "Closed." : "No open report starts with " + args[1] + ".");
                break;
            case "suspend" when args.Length >= 3 && int.TryParse(args[2], out int days) && days > 0:
                Console.WriteLine(await Suspend(db, args[1], now.AddDays(days), now) ? args[1] + " can't sign in for " + days + " days." : "No account called " + args[1] + ".");
                break;
            case "unsuspend" when args.Length >= 2:
                Console.WriteLine(await Suspend(db, args[1], null, now) ? args[1] + " can sign in again." : "No account called " + args[1] + ".");
                break;
            default:
                Console.WriteLine("Use: reports | close-report <id> [note] | suspend <username> <days> | unsuspend <username>");
                break;
        }

        return true;
    }

    public static async Task<List<string>> OpenReports(AppDbContext db)
    {
        List<PlayerReport> reports = await db.PlayerReports.AsNoTracking().Where(r => r.ClosedUtc == null).OrderBy(r => r.CreatedUtc).ToListAsync();
        return reports.Select(r => r.Id.ToString("N")[..8] + "  " + r.CreatedUtc.ToString("yyyy-MM-dd HH:mm") + "  " + r.ReportedName + "  (" + r.Reason + ")" +
                                   (r.Details.Length > 0 ? Environment.NewLine + "Note: " + r.Details : "") +
                                   (r.Evidence.Length > 0 ? Environment.NewLine + r.Evidence.TrimEnd() : "")).ToList();
    }

    public static async Task<bool> CloseReport(AppDbContext db, string idStart, string note, DateTime now)
    {
        List<PlayerReport> open = await db.PlayerReports.Where(r => r.ClosedUtc == null).ToListAsync();
        PlayerReport? report = open.FirstOrDefault(r => r.Id.ToString("N").StartsWith(idStart, StringComparison.OrdinalIgnoreCase));
        if (report == null) return false;
        report.ClosedUtc = now;
        report.ClosedNote = note;
        await db.SaveChangesAsync();
        return true;
    }

    /// <param name="until">Paused until then (null = not paused).</param>
    public static async Task<bool> Suspend(AppDbContext db, string username, DateTime? until, DateTime now)
    {
        string normalized = username.Trim().ToUpperInvariant();
        User? user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalized);
        if (user == null) return false;
        user.SuspendedUntilUtc = until;
        if (until != null) await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedUtc == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, now));
        await db.SaveChangesAsync();
        return true;
    }
}
