using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Guilds;

/// <summary>
/// A BACKGROUND JOB. It runs for as long as the server runs, with no request to trigger it:
/// once a minute it closes bosses whose time ran out and sweeps away old guild invites.
/// This is how real servers do "things that happen on a schedule" (daily resets, expiry, cleanups).
/// </summary>
public class BossLifecycleService(IServiceScopeFactory scopes, ILogger<BossLifecycleService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using PeriodicTimer timer = new(Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stopping))
            {
                try
                {
                    // Each run gets its own short-lived database connection ("scope").
                    using IServiceScope scope = scopes.CreateScope();
                    AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                    int closed = await RunOnce(db, DateTime.UtcNow, stopping);

                    // Guild wars start and end on weekends: the same once-a-minute job takes care of them.
                    int wars = await GuildWars.RunOnce(db, DateTime.UtcNow, stopping);

                    if (wars > 0)
                    {
                        logger.LogInformation("Guild wars changed: {Count}.", wars);
                    }

                    if (closed > 0)
                    {
                        logger.LogInformation("Closed {Count} expired guild boss(es).", closed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failed run must never kill the job: log it and try again next minute.
                    logger.LogError(ex, "Guild boss maintenance failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down. Normal.
        }
    }

    /// <summary>One pass of the job. Public so tests can run it directly with a pretend clock.</summary>
    public static async Task<int> RunOnce(AppDbContext db, DateTime now, CancellationToken cancel = default)
    {
        int expired = await db.GuildBosses
            .Where(b => b.Status == BossStatus.Active && b.EndsUtc <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, BossStatus.Expired), cancel);

        await db.GuildInvites.Where(i => i.ExpiresUtc <= now).ExecuteDeleteAsync(cancel);

        return expired;
    }
}
