using GameServices.Api.Data;
using GameServices.Api.Social;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Progress;

/// <summary>
/// THE ACTIVITY LOG: what every walker has done, in order, so anything can be looked into later (a strange balance, a
/// report about a player, a question from a parent). On the server machine:
///
///     dotnet GameServices.Api.dll player SomeName 7
///
/// shows that walker's last 7 days, merged with their Glimmer ledger. Two kinds of line go in:
///   * things that happen: a level, a medal, a monster arriving, a find on the map (written with the change itself, so a
///     line can't exist without the change, or the other way round)
///   * every request that changes something (see UseActivityLog), including ones the server refused, which is how a
///     cheat attempt shows up
///
/// WHAT NEVER GOES IN: passwords, emails, messages between players, and where anyone is. Only the address of a request
/// is written down, never what was sent with it. Steps aren't written here either: WalkingDay already keeps each day's.
///
/// It's kept for KeptFor and then removed (WalkingLifecycleService), and it goes with an account when that's deleted.
/// The privacy policy says all this: change them together.
/// </summary>
public static class ActivityLog
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(90);
    private const int MostDetail = 200;

    /// <summary>Writes a line, to be saved along with whatever else is being saved.</summary>
    public static void Add(AppDbContext db, Guid user, string kind, string detail)
    {
        db.WalkingActivity.Add(new WalkingActivity
        {
            UserId = user, Kind = kind, Detail = detail.Length <= MostDetail ? detail : detail[..MostDetail],
        });
    }

    // Reports every minute or so, and "I've seen that": nothing to learn from them, and a great many of them.
    private static bool WorthWriting(HttpContext http)
    {
        string method = http.Request.Method;
        if (method != "POST" && method != "PUT" && method != "PATCH" && method != "DELETE") return false;

        string path = http.Request.Path.Value ?? "";
        return path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
               && !path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)
               && !path.Equals("/api/walking/steps", StringComparison.OrdinalIgnoreCase)
               && !path.EndsWith("/seen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes down every request that changes something, once it's finished. A problem here never spoils the request.</summary>
    public static IApplicationBuilder UseActivityLog(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        await next();

        if (!WorthWriting(http) || !SocialEndpoints.TryGetUserId(http.User, out Guid me)) return;

        try
        {
            using IServiceScope scope = http.RequestServices.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Add(db, me, "request", http.Request.Method + " " + http.Request.Path + " -> " + http.Response.StatusCode);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ActivityLog").LogWarning(ex, "Couldn't write to the activity log.");
        }
    });

    /// <summary>A walker's activity and Glimmer, oldest first. Null if there's nobody of that name.</summary>
    public static async Task<List<string>?> TimelineAsync(AppDbContext db, string username, int days, DateTime now, CancellationToken cancel = default)
    {
        string name = username.Trim().ToUpperInvariant();
        Guid? user = await db.Users.AsNoTracking().Where(u => u.NormalizedUsername == name).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancel);
        if (user == null) return null;

        DateTime since = now.AddDays(-Math.Max(1, days));
        var activity = await db.WalkingActivity.AsNoTracking().Where(a => a.UserId == user && a.AtUtc >= since).ToListAsync(cancel);
        var ledger = await db.WalkingLedger.AsNoTracking().Where(l => l.UserId == user && l.CreatedUtc >= since).ToListAsync(cancel);

        return activity.Select(a => (At: a.AtUtc, Line: a.Kind + "  " + a.Detail))
            .Concat(ledger.Select(l => (At: l.CreatedUtc, Line: "glimmer  " + (l.Amount >= 0 ? "+" : "") + l.Amount + " = " + l.BalanceAfter + "  " + l.Kind + ": " + l.Detail)))
            .OrderBy(x => x.At)
            .Select(x => x.At.ToString("yyyy-MM-dd HH:mm:ss") + "  " + x.Line)
            .ToList();
    }

    public static async Task<bool> TryRunAdmin(IServiceProvider services, string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "player", StringComparison.OrdinalIgnoreCase)) return false;

        if (args.Length < 2)
        {
            Console.WriteLine("Usage: player <username> [days]");
            return true;
        }

        int days = args.Length > 2 && int.TryParse(args[2], out int d) ? d : 7;
        using IServiceScope scope = services.CreateScope();
        List<string>? lines = await TimelineAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), args[1], days, DateTime.UtcNow);
        if (lines == null) Console.WriteLine("Nobody is called " + args[1] + ".");
        else if (lines.Count == 0) Console.WriteLine("Nothing in the last " + days + " days.");
        else lines.ForEach(Console.WriteLine);
        return true;
    }
}
