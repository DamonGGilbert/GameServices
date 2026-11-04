using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Infrastructure;

/// <summary>
/// WHEN TWO REQUESTS TOUCH THE SAME THING AT ONCE.
///
/// Anything Glimmer or a monster's owner hangs on has a Version number (IVersioned), and a save only works if the
/// version hasn't changed since it was read. So when two requests race - two taps on Buy, two walkers after the same
/// monster - the database lets one through and the other's save fails. This runs that other one again from the top
/// with fresh data: it now sees the Glimmer has gone, or the monster's been sold, and says so.
///
/// Safe because a handler's changes all land in one save at its end (the few earlier saves return straight after).
/// </summary>
public static class RetryOnConflict
{
    public const int Attempts = 6;

    public static TBuilder RetriesOnConflict<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            AppDbContext db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await next(context);
                }
                catch (DbUpdateConcurrencyException) when (attempt < Attempts)
                {
                    db.ChangeTracker.Clear();   // forget what was read: the next go reads it all again
                    await Task.Delay(Random.Shared.Next(10, 40 * attempt), context.HttpContext.RequestAborted);
                }
                catch (DbUpdateConcurrencyException)
                {
                    return Results.Problem("Lots is happening at once - please try that again.", statusCode: StatusCodes.Status409Conflict);
                }
                catch (DbUpdateException) when (attempt < Attempts)
                {
                    // The same again for something only one can have: two requests starting a new day's row, or two finds
                    // taking the same serial number. Any other problem fails the same way every time, and comes out in the end.
                    db.ChangeTracker.Clear();
                    await Task.Delay(Random.Shared.Next(10, 40 * attempt), context.HttpContext.RequestAborted);
                }
            }
        });
}
