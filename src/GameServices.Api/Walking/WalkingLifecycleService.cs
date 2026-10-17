using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking;

/// <summary>
/// THE WALKING GAME'S BACKGROUND JOB (the same pattern as BossLifecycleService): once a minute, with no request to
/// trigger it, it
///   * finishes step races whose time is up (works out the winner and updates everyone's race record), and
///   * expires race challenges nobody answered within a day, and
///   * starts and finishes the weekend guild step races (WalkingGuilds.RunOnce).
/// (The weekly Guild Journey needs no job: it's worked out from the step reports whenever someone looks.)
/// </summary>
public class WalkingLifecycleService(IServiceScopeFactory scopes, ILogger<WalkingLifecycleService> logger) : BackgroundService
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

                    int changed = await RunOnce(db, DateTime.UtcNow, stopping);

                    if (changed > 0)
                    {
                        logger.LogInformation("Walking: {Count} race(s) started, finished or expired.", changed);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One failed run must never kill the job: log it and try again next minute.
                    logger.LogError(ex, "Walking maintenance failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down. Normal.
        }
    }

    /// <summary>One pass of the job. Public so tests (and the race endpoints) can run it with any clock.</summary>
    public static async Task<int> RunOnce(AppDbContext db, DateTime now, CancellationToken cancel = default)
    {
        // Challenges nobody answered.
        DateTime staleBefore = now - WalkingEndpoints.ChallengeLifetime;
        int expired = await db.StepRaces
            .Where(r => r.Status == RaceStatus.Pending && r.CreatedUtc <= staleBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RaceStatus.Expired), cancel);

        // Races whose time is up.
        List<StepRace> over = await db.StepRaces.Where(r => r.Status == RaceStatus.Active && r.EndsUtc <= now).ToListAsync(cancel);

        foreach (StepRace race in over)
        {
            WalkingProfile challenger = await WalkingEndpoints.GetOrCreateProfile(db, race.ChallengerId, cancel);
            WalkingProfile opponent = await WalkingEndpoints.GetOrCreateProfile(db, race.OpponentId, cancel);

            race.ChallengerSteps = (int)Math.Clamp(challenger.LifetimeSteps - race.ChallengerBaseline, 0, int.MaxValue);
            race.OpponentSteps = (int)Math.Clamp(opponent.LifetimeSteps - race.OpponentBaseline, 0, int.MaxValue);
            race.Status = RaceStatus.Finished;

            if (race.ChallengerSteps != race.OpponentSteps)
            {
                bool challengerWon = race.ChallengerSteps > race.OpponentSteps;
                race.WinnerId = challengerWon ? race.ChallengerId : race.OpponentId;
                (challengerWon ? challenger : opponent).RacesWon++;
                (challengerWon ? opponent : challenger).RacesLost++;
            }
        }

        if (over.Count > 0)
        {
            await db.SaveChangesAsync(cancel);
        }

        int guildRaces = await WalkingGuilds.RunOnce(db, now, cancel);

        return expired + over.Count + guildRaces;
    }
}
