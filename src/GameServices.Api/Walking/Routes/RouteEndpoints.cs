using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using GameServices.Api.Data;
using GameServices.Api.Map;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Routes;

public record RouteOptionDto(string Kind, string Name, int Metres, string Reward, bool Available, string Note);

public record RoutePlanRequest(string Kind, double Lat, double Lon, int UtcOffsetMinutes, string? Biome = null);

public record RouteDto(Guid Id, string Kind, string Name, int Metres, double QuietShare, int BusyStretches,
    List<double[]> Path, List<double[]> Checkpoints, int NextCheckpoint, string Status, DateTime ExpiresUtc, string Summary,
    double[] PathFlat, double[] CheckpointsFlat);   // the same points as lat, lon, lat, lon... (for Unity's simple JSON reader)

public record CheckpointRequest(int Index, double Lat, double Lon);

public record CheckpointResult(int NextCheckpoint, int Total, bool Completed, string Message, int RewardGlimmer, bool Monster, int Glimmer);

/// <summary>
/// SAFE ROUTES: walking loops planned by the server along the safest, nicest ways nearby, with bonus rewards.
///
///   GET  /api/walking/routes/options          what's on offer today (and why something isn't)
///   POST /api/walking/routes                  plan a loop from where you are (replaces any route in progress)
///   GET  /api/walking/routes/active           the route you're on (204 if none)
///   POST /api/walking/routes/{id}/checkpoint  "I've reached checkpoint N" (checked: close enough, in order, at walking pace)
///   POST /api/walking/routes/{id}/abandon
///
/// The routes and their rewards:
///   Short      1 km    40 Glimmer
///   Medium     3 km   120 Glimmer + a 15% chance of a monster
///   Long       5 km   220 Glimmer + a 30% chance of a monster (Uncommon or rarer)
///   Challenge  5 km   300 Glimmer + a GUARANTEED monster (Rare or rarer) - once a week, in daylight only
/// Two Short/Medium/Long rewards a day. A route is open for 4 hours.
///
/// Rewards are only given for a route really walked: every checkpoint reached in order, no faster than a jog between
/// them, and the walker's steps must add up to the distance. The routes avoid main roads without pavements, private
/// paths and anywhere marked no walking - but the game still reminds walkers to keep their eyes up and cross with care.
/// </summary>
public static class RouteEndpoints
{
    public const int RewardedRoutesPerDay = 2;
    public static readonly TimeSpan RouteLifetime = TimeSpan.FromHours(4);

    private sealed record Kind(string Id, string Name, int Metres, int Glimmer, double MonsterChance, int MonsterMinTier, bool Weekly);

    private static readonly Kind[] Kinds =
    {
        new("short", "Short loop", 1000, 40, 0, 0, false),
        new("medium", "Medium loop", 3000, 120, 0.15, 0, false),
        new("long", "Long loop", 5000, 220, 0.30, 1, false),
        new("challenge", "Weekly challenge", 5000, 300, 1.0, 2, true),
    };

    public static RouteGroupBuilder MapRouteEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/routes/options", Options);
        group.MapPost("/routes", PlanRoute);
        group.MapGet("/routes/active", Active);
        group.MapPost("/routes/{routeId:guid}/checkpoint", Checkpoint);
        group.MapPost("/routes/{routeId:guid}/abandon", Abandon);
        return group;
    }

    // ------------------------------------------------------------------ what's on offer

    private static async Task<IResult> Options(int? utcOffsetMinutes, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        int rewardedToday = await RewardedToday(db, me, now, cancel);
        bool challengeDone = await ChallengeDoneThisWeek(db, me, now, cancel);
        bool daylight = IsDaylight(now, utcOffsetMinutes ?? 0);

        return Results.Ok(Kinds.Select(k =>
        {
            string reward = k.Glimmer + " Glimmer" + (k.MonsterChance >= 1 ? " + a guaranteed monster (Rare or rarer)"
                : k.MonsterChance > 0 ? " + a " + (k.MonsterChance * 100).ToString("0") + "% chance of a monster" + (k.MonsterMinTier > 0 ? " (Uncommon or rarer)" : "") : "");
            if (k.Weekly)
            {
                return new RouteOptionDto(k.Id, k.Name, k.Metres, reward, !challengeDone && daylight,
                    challengeDone ? "Done this week - a new challenge on Monday!" : !daylight ? "Challenges are for daylight hours (7am to 7pm)." : "Once a week.");
            }
            return new RouteOptionDto(k.Id, k.Name, k.Metres, reward, rewardedToday < RewardedRoutesPerDay,
                rewardedToday < RewardedRoutesPerDay ? (RewardedRoutesPerDay - rewardedToday) + " rewarded route" + (RewardedRoutesPerDay - rewardedToday == 1 ? "" : "s") + " left today."
                    : "That's today's two rewarded routes - well walked! More tomorrow.");
        }).ToList());
    }

    // ------------------------------------------------------------------ planning

    private static async Task<IResult> PlanRoute(RoutePlanRequest request, ClaimsPrincipal principal, AppDbContext db, MapTileStore tiles, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        Kind? kind = Kinds.FirstOrDefault(k => k.Id == request?.Kind);
        if (kind == null || Math.Abs(request!.Lat) > 85 || Math.Abs(request.Lon) > 180) return Problem("Pick a route length.");

        DateTime now = DateTime.UtcNow;
        if (kind.Weekly)
        {
            if (await ChallengeDoneThisWeek(db, me, now, cancel)) return Problem("You've done this week's challenge - a new one opens on Monday!");
            if (!IsDaylight(now, request.UtcOffsetMinutes)) return Problem("Challenge routes are for daylight hours (7am to 7pm) - safer and more fun!");
        }
        else if (await RewardedToday(db, me, now, cancel) >= RewardedRoutesPerDay)
        {
            return Problem("That's today's two rewarded routes. Walk on for fun, or come back tomorrow!");
        }

        // The map squares around you, as the map relay keeps them. Missing ones start downloading now.
        double radius = kind.Metres / 3.9 * 1.3 + 400;
        List<(int X, int Y)> squares = SquaresAround(request.Lat, request.Lon, radius);
        var answers = new List<string>();
        int missing = 0;
        foreach ((int x, int y) in squares)
        {
            string? json = await tiles.ReadCachedJsonAsync(x, y);
            if (json != null) answers.Add(json);
            else { missing++; tiles.Warm(x, y); }
        }

        // The public map servers are run by volunteers and are often slow or busy, so a route doesn't wait for every last
        // square: once the squares right around you and most of the rest are here, it plans with what there is (the
        // missing ones keep downloading for next time). A loop only ever uses paths the map really has.
        (int startX, int startY) = Square(request.Lat, request.Lon);
        int nearbyMissing = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (squares.Contains((startX + dx, startY + dy)) && await tiles.ReadCachedJsonAsync(startX + dx, startY + dy) == null) nearbyMissing++;

        if (nearbyMissing > 0 || missing > squares.Count * MaxMissingShare)
        {
            int ready = squares.Count - missing;
            return Results.Problem($"Getting the map for this area ready ({ready} of {squares.Count} pieces so far) - try again in a minute.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?> { ["retryAfterSeconds"] = 30 });
        }

        RouteGraph graph = RouteGraph.FromOverpass(answers);
        PlannedRoute? planned = RoutePlanner.Plan(graph, request.Lat, request.Lon, kind.Metres, Random.Shared.Next());
        if (planned == null)
        {
            return Problem("There isn't a good walking loop from here - try a different length, or start from a park or quieter street.");
        }

        // Only one route at a time.
        foreach (WalkingRoute old in await db.WalkingRoutes.Where(r => r.UserId == me && r.Status == RouteStatus.Active).ToListAsync(cancel))
        {
            old.Status = RouteStatus.Abandoned;
        }

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        var route = new WalkingRoute
        {
            UserId = me,
            Kind = kind.Id,
            TargetMetres = kind.Metres,
            Metres = (int)Math.Round(planned.Metres),
            QuietShare = planned.QuietShare,
            BusyStretches = planned.BusyStretches,
            PathJson = JsonSerializer.Serialize(planned.Path.Select(p => new[] { Math.Round(p.Lat, 6), Math.Round(p.Lon, 6) })),
            CheckpointsJson = JsonSerializer.Serialize(planned.Checkpoints.Select(p => new[] { Math.Round(p.Lat, 6), Math.Round(p.Lon, 6) })),
            StartedUtc = now,
            LastCheckpointUtc = now,
            ExpiresUtc = now + RouteLifetime,
            StepsAtStart = profile.LifetimeSteps,
            Biome = string.IsNullOrWhiteSpace(request.Biome) ? null : request.Biome.Trim().ToLowerInvariant(),
        };
        db.WalkingRoutes.Add(route);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(ToDto(route));
    }

    private static async Task<IResult> Active(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingRoute? route = await CurrentRoute(db, me, cancel);
        return route == null ? Results.NoContent() : Results.Ok(ToDto(route));
    }

    // ------------------------------------------------------------------ walking it

    private static async Task<IResult> Checkpoint(Guid routeId, CheckpointRequest request, ClaimsPrincipal principal, AppDbContext db,
        MonsterFinder finder, IConfiguration config, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingRoute? route = await CurrentRoute(db, me, cancel);
        if (route == null || route.Id != routeId) return Problem("That route isn't open any more.");

        List<double[]> checkpoints = JsonSerializer.Deserialize<List<double[]>>(route.CheckpointsJson) ?? new();
        if (request.Index != route.NextCheckpoint || request.Index >= checkpoints.Count) return Problem("That's not the next checkpoint.");

        double radius = config.GetValue("Routes:CheckpointRadiusMetres", 40.0);
        double[] target = checkpoints[request.Index];
        if (RouteGraph.Distance(request.Lat, request.Lon, target[0], target[1]) > radius) return Problem("Not quite there yet - keep going to the checkpoint.");

        // No faster than a jog between checkpoints: routes are for walking (and nobody should be racing traffic).
        double maxSpeed = config.GetValue("Routes:MaxMetresPerSecond", 3.0);
        double apart = route.Metres / (double)checkpoints.Count;
        DateTime now = DateTime.UtcNow;
        if ((now - route.LastCheckpointUtc).TotalSeconds < apart / maxSpeed) return Problem("That was very quick! Routes are for walking - take your time.");

        bool last = request.Index == checkpoints.Count - 1;
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);

        if (last && profile.LifetimeSteps - route.StepsAtStart < route.Metres / 2.0)
        {
            // Steps arrive about once a minute - the finish waits for them to catch up.
            return Results.Problem("Nearly there! Your steps haven't caught up yet - keep the game open a moment and it'll finish.", statusCode: StatusCodes.Status409Conflict);
        }

        route.NextCheckpoint++;
        route.LastCheckpointUtc = now;
        int glimmer = 0;
        bool monster = false;

        if (last)
        {
            Kind kind = Kinds.First(k => k.Id == route.Kind);
            route.Status = RouteStatus.Completed;
            route.CompletedUtc = now;
            glimmer = kind.Glimmer;
            WalkingWallet.Change(db, profile, glimmer, "route", kind.Name + " completed");

            if (kind.MonsterChance > 0 && (kind.MonsterChance >= 1 || Random.Shared.NextDouble() < kind.MonsterChance))
            {
                (string species, int tier, long seed, int potential) = finder.Roll(route.Biome, kind.MonsterMinTier);
                await MonsterEndpoints.Add(db, me, species, tier, seed, potential, finder.Catalog.RulesVersion, "route", route.Biome, now.ToString("yyyy-MM-dd"), cancel);
                monster = true;
            }

            route.RewardGlimmer = glimmer;
            route.RewardMonster = monster;
        }

        await db.SaveChangesAsync(cancel);

        string message = last
            ? "Route complete! +" + glimmer + " Glimmer" + (monster ? " - and a monster was waiting at the finish!" : ".")
            : "Checkpoint " + (request.Index + 1) + " of " + checkpoints.Count + "!";
        return Results.Ok(new CheckpointResult(route.NextCheckpoint, checkpoints.Count, last, message, glimmer, monster, profile.Glimmer));
    }

    private static async Task<IResult> Abandon(Guid routeId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingRoute? route = await db.WalkingRoutes.FirstOrDefaultAsync(r => r.Id == routeId && r.UserId == me && r.Status == RouteStatus.Active, cancel);
        if (route == null) return Results.NotFound();
        route.Status = RouteStatus.Abandoned;
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<WalkingRoute?> CurrentRoute(AppDbContext db, Guid me, CancellationToken cancel)
    {
        WalkingRoute? route = await db.WalkingRoutes.FirstOrDefaultAsync(r => r.UserId == me && r.Status == RouteStatus.Active, cancel);
        if (route != null && route.ExpiresUtc < DateTime.UtcNow)
        {
            route.Status = RouteStatus.Expired;
            await db.SaveChangesAsync(cancel);
            return null;
        }
        return route;
    }

    private static Task<int> RewardedToday(AppDbContext db, Guid me, DateTime now, CancellationToken cancel)
    {
        DateTime since = now.Date;
        return db.WalkingRoutes.CountAsync(r => r.UserId == me && r.Status == RouteStatus.Completed && r.Kind != "challenge" && r.CompletedUtc >= since, cancel);
    }

    private static Task<bool> ChallengeDoneThisWeek(AppDbContext db, Guid me, DateTime now, CancellationToken cancel)
    {
        DateTime monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
        return db.WalkingRoutes.AnyAsync(r => r.UserId == me && r.Status == RouteStatus.Completed && r.Kind == "challenge" && r.CompletedUtc >= monday, cancel);
    }

    private static bool IsDaylight(DateTime utcNow, int utcOffsetMinutes)
    {
        int hour = utcNow.AddMinutes(Math.Clamp(utcOffsetMinutes, -840, 840)).Hour;
        return hour >= 7 && hour < 19;
    }

    // Zoom-15 squares covering a circle around a point.
    /// <summary>A route is planned once at most this share of the map around you is still downloading.</summary>
    public const double MaxMissingShare = 0.25;

    private static List<(int X, int Y)> SquaresAround(double lat, double lon, double radiusMetres)
    {
        double dLat = radiusMetres / 110540.0;
        double dLon = radiusMetres / (111320.0 * Math.Cos(lat * Math.PI / 180.0));
        (int x0, int y0) = Square(lat + dLat, lon - dLon);
        (int x1, int y1) = Square(lat - dLat, lon + dLon);
        var list = new List<(int, int)>();
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                list.Add((x, y));
        return list;
    }

    private static (int X, int Y) Square(double lat, double lon)
    {
        double n = 1 << MapQuery.Zoom;
        int x = (int)Math.Floor((lon + 180.0) / 360.0 * n);
        double latRad = lat * Math.PI / 180.0;
        int y = (int)Math.Floor((1 - Math.Log(Math.Tan(latRad) + 1 / Math.Cos(latRad)) / Math.PI) / 2 * n);
        return (x, y);
    }

    private static RouteDto ToDto(WalkingRoute r)
    {
        Kind kind = Kinds.First(k => k.Id == r.Kind);
        string summary = (r.QuietShare * 100).ToString("0", CultureInfo.InvariantCulture) + "% on footpaths and quiet streets" +
            (r.BusyStretches == 0 ? ", no busy roads." : ", " + r.BusyStretches + " stretch" + (r.BusyStretches == 1 ? "" : "es") + " beside a busier road (with a pavement where mapped).");
        List<double[]> path = JsonSerializer.Deserialize<List<double[]>>(r.PathJson) ?? new();
        List<double[]> checkpoints = JsonSerializer.Deserialize<List<double[]>>(r.CheckpointsJson) ?? new();
        return new RouteDto(r.Id, r.Kind, kind.Name, r.Metres, r.QuietShare, r.BusyStretches, path, checkpoints,
            r.NextCheckpoint, r.Status.ToString(), r.ExpiresUtc, summary, path.SelectMany(p => p).ToArray(), checkpoints.SelectMany(p => p).ToArray());
    }

    private static IResult Problem(string message) => Results.Problem(message, statusCode: StatusCodes.Status400BadRequest);
}
