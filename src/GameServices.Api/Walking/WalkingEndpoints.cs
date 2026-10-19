using System.Security.Claims;
using GameServices.Api.Chat;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Routes;
using GameServices.Api.Walking.Wallet;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking;

/// <summary>Biome: the kind of place the walker is mostly in ("park", "coastal"...), so a find can suit it. Optional.</summary>
public record StepReportRequest(string Day, int Steps, int UtcOffsetMinutes, string? Biome = null);

/// <summary>NewFinds: monsters found on this walk (fetch them from /api/walking/monsters and reveal them).</summary>
public record StepReportResult(int AcceptedToday, int Added, int Rejected, long LifetimeSteps, bool Flagged, string Message, int NewFinds = 0);

public record WalkingMeDto(int Today, int Week, long Lifetime, bool ShareSteps, int RacesWon, int RacesLost);

public record PrivacyRequest(bool ShareSteps);

public record FriendStepsDto(Guid UserId, string Username, bool IsOnline, bool Shared, int Today, int Week, int RacesWon);

public record CheerRequest(int Kind);

public record CheerDto(long Id, Guid FromUserId, string FromUsername, int Kind, string Text, DateTime SentUtc);

public record CheerPhraseDto(int Kind, string Text);

public record SeenRequest(long UpToId);

public record RaceRequest(Guid FriendId, int Hours);

public record RaceDto(Guid Id, string Status, Guid OpponentId, string OpponentName, bool IChallenged, int Hours, DateTime CreatedUtc,
    DateTime? StartsUtc, DateTime? EndsUtc, int MySteps, int TheirSteps, string Result);

/// <summary>
/// THE WALKING GAME: steps, comparing with friends, cheers and friendly step races.
///
///   POST /api/walking/steps             report today's step total (the server decides what counts)
///   GET  /api/walking/me                my today / week / lifetime and race record
///   PUT  /api/walking/privacy           may friends see my steps?
///   GET  /api/walking/friends           my friends' steps (only those who share them)
///   GET  /api/walking/cheers/phrases    the cheers you can send
///   POST /api/walking/cheers/{friendId} send a cheer      GET /api/walking/cheers   new cheers for me
///   POST /api/walking/cheers/seen       mark cheers as seen
///   GET/POST /api/walking/races         my races / challenge a friend
///   POST /api/walking/races/{id}/accept | decline | cancel
///   /api/walking/guild/...              Guild Journey, weekend step race, ranking (see WalkingGuilds)
///
/// Every rule is checked here (friends only, no blocked players, limits per day, believable steps). The game only
/// asks and shows the answer.
/// </summary>
public static class WalkingEndpoints
{
    public const int MaxCheersPerFriendPerDay = 3;
    public const int MaxOpenRaces = 3;
    public static readonly int[] RaceLengthsHours = { 24, 72 };
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromHours(24);

    /// <summary>A fixed list (UK English), so a cheer can never be rude: the game only ever sends a number.</summary>
    public static readonly string[] CheerPhrases =
    {
        "Keep going!",
        "You've got this!",
        "Great walk today!",
        "Nearly there - one more lap!",
        "Race you tomorrow!",
    };

    public static IEndpointRouteBuilder MapWalkingEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/walking").WithTags("Walking").RequireAuthorization();

        group.MapPost("/steps", ReportSteps);
        group.MapGet("/me", Me);
        group.MapPut("/privacy", SetPrivacy);
        group.MapGet("/friends", FriendsSteps);

        group.MapGet("/cheers/phrases", () => Results.Ok(CheerPhrases.Select((text, i) => new CheerPhraseDto(i, text)).ToList()));
        group.MapPost("/cheers/seen", MarkCheersSeen);
        group.MapPost("/cheers/{friendId:guid}", SendCheer);
        group.MapGet("/cheers", NewCheers);

        // Walking guilds: the weekly Guild Journey, the weekend step race and the ranking (WalkingGuilds.cs).
        group.MapWalkingGuildEndpoints();

        // Monsters, made on the server and found (very rarely) by walking (Walking/Monsters), packs, offers between
        // friends, and the Glimmer that pays for them (Walking/Wallet).
        group.MapWalkingMonsterEndpoints();
        group.MapWalkingPackEndpoints();
        group.MapMonsterOfferEndpoints();
        group.MapWalkingWalletEndpoints();

        // Safe walking loops with bonus rewards (Walking/Routes).
        group.MapRouteEndpoints();

        group.MapGet("/races", MyRaces);
        group.MapPost("/races", Challenge);
        group.MapPost("/races/{raceId:guid}/accept", AcceptRace);
        group.MapPost("/races/{raceId:guid}/decline", (Guid raceId, ClaimsPrincipal p, AppDbContext db, CancellationToken c) => CloseRace(raceId, decline: true, p, db, c));
        group.MapPost("/races/{raceId:guid}/cancel", (Guid raceId, ClaimsPrincipal p, AppDbContext db, CancellationToken c) => CloseRace(raceId, decline: false, p, db, c));

        return app;
    }

    // ------------------------------------------------------------------ steps

    private static async Task<IResult> ReportSteps(StepReportRequest request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        DateTime now = DateTime.UtcNow;
        DateTime? dayStart = StepRules.DayStartUtc(request?.Day, request?.UtcOffsetMinutes ?? 0, now);

        if (request == null || dayStart == null)
        {
            return Results.Problem("Steps can only be reported for today.", statusCode: StatusCodes.Status400BadRequest);
        }

        WalkingProfile profile = await GetOrCreateProfile(db, me, cancel);

        if (profile.LastReportUtc != null && now - profile.LastReportUtc.Value < StepRules.MinTimeBetweenReports)
        {
            return Results.Problem("Steps were reported a moment ago. Try again shortly.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        string day = request.Day.Trim();
        WalkingDay? row = await db.WalkingDays.FirstOrDefaultAsync(d => d.UserId == me && d.Day == day, cancel);
        DateTime? dayLastReported = row?.UpdatedUtc;

        if (row == null)
        {
            row = new WalkingDay { UserId = me, Day = day };
            db.WalkingDays.Add(row);
        }

        // Each day is judged on its own time: from when THIS day was last reported (so finishing yesterday after
        // today's reports still works) up to its midnight.
        StepVerdict verdict = StepRules.Evaluate(row.Steps, dayLastReported, request.Steps, now, dayStart.Value);

        row.Steps = verdict.AcceptedToday;
        row.RejectedSteps += verdict.Rejected;
        row.UpdatedUtc = now;
        profile.LifetimeSteps += verdict.Added;
        profile.LastReportUtc = now;

        if (verdict.Flagged)
        {
            profile.FlaggedReports++;
        }

        // Walking earns Glimmer (1 per 100 steps)...
        WalkingWallet.CreditSteps(db, profile, verdict.Added);

        // ...and every 5,000 accepted steps is a (small) chance of finding a monster - saved together with the steps.
        string? biome = string.IsNullOrWhiteSpace(request.Biome) ? null : request.Biome.Trim().ToLowerInvariant();
        int newFinds = verdict.Added > 0 ? await MonsterEndpoints.RollFinds(db, finder, me, profile, verdict.Added, day, biome, cancel) : 0;

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            // Two reports raced for the same new day: the other one won. Nothing is lost; the next report catches up.
            return Results.Problem("Steps were reported a moment ago. Try again shortly.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        string message = verdict.Rejected > 0
            ? $"{verdict.Rejected:N0} steps couldn't be counted (more than anyone can walk in that time)."
            : "";

        return Results.Ok(new StepReportResult(verdict.AcceptedToday, verdict.Added, verdict.Rejected, profile.LifetimeSteps, verdict.Flagged, message, newFinds));
    }

    private static async Task<IResult> Me(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        WalkingProfile profile = await GetOrCreateProfile(db, me, cancel);
        await db.SaveChangesAsync(cancel);
        (int today, int week) = await Summary(db, me, DateTime.UtcNow, cancel);

        return Results.Ok(new WalkingMeDto(today, week, profile.LifetimeSteps, profile.ShareSteps, profile.RacesWon, profile.RacesLost));
    }

    private static async Task<IResult> SetPrivacy(PrivacyRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        WalkingProfile profile = await GetOrCreateProfile(db, me, cancel);
        profile.ShareSteps = request?.ShareSteps ?? true;
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static async Task<IResult> FriendsSteps(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        await Presence.Touch(db, me, cancel);

        List<Guid> friendIds = await db.Friendships.AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.UserAId == me || f.UserBId == me))
            .Select(f => f.UserAId == me ? f.UserBId : f.UserAId)
            .ToListAsync(cancel);

        var users = await db.Users.AsNoTracking().Where(u => friendIds.Contains(u.Id)).Select(u => new { u.Id, u.Username, u.LastSeenUtc }).ToListAsync(cancel);
        Dictionary<Guid, WalkingProfile> profiles = await db.WalkingProfiles.AsNoTracking().Where(p => friendIds.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, cancel);

        DateTime now = DateTime.UtcNow;
        List<FriendStepsDto> result = new();

        foreach (var user in users.OrderBy(u => u.Username))
        {
            profiles.TryGetValue(user.Id, out WalkingProfile? profile);
            bool shared = profile == null || profile.ShareSteps;
            (int today, int week) = shared ? await Summary(db, user.Id, now, cancel) : (0, 0);
            bool online = user.LastSeenUtc != null && now - user.LastSeenUtc.Value < SocialEndpoints.OnlineWindow;

            result.Add(new FriendStepsDto(user.Id, user.Username, online, shared, today, week, profile?.RacesWon ?? 0));
        }

        return Results.Ok(result);
    }

    // ------------------------------------------------------------------ cheers

    private static async Task<IResult> SendCheer(Guid friendId, CheerRequest request, ClaimsPrincipal principal, AppDbContext db, IHubContext<ChatHub> hub, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        int kind = request?.Kind ?? -1;

        if (kind < 0 || kind >= CheerPhrases.Length)
        {
            return Results.Problem("That isn't one of the cheers.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (friendId == me || !await SocialEndpoints.AreFriends(db, me, friendId, cancel) || await SocialEndpoints.IsBlockedEitherWay(db, me, friendId, cancel))
        {
            return Results.Problem("You can only cheer on your friends.", statusCode: StatusCodes.Status403Forbidden);
        }

        DateTime now = DateTime.UtcNow;
        DateTime since = now.Date;

        if (await db.Cheers.CountAsync(c => c.FromUserId == me && c.ToUserId == friendId && c.SentUtc >= since, cancel) >= MaxCheersPerFriendPerDay)
        {
            return Results.Problem($"You've cheered them {MaxCheersPerFriendPerDay} times today. Save some encouragement for tomorrow!", statusCode: StatusCodes.Status429TooManyRequests);
        }

        Cheer cheer = new() { FromUserId = me, ToUserId = friendId, Kind = kind, SentUtc = now };
        db.Cheers.Add(cheer);
        await db.SaveChangesAsync(cancel);

        string username = await db.Users.Where(u => u.Id == me).Select(u => u.Username).FirstAsync(cancel);
        CheerDto dto = new(cheer.Id, me, username, kind, CheerPhrases[kind], cheer.SentUtc);

        // Live push for anyone listening (the game also asks every minute, so this is a bonus, not a requirement).
        await hub.Clients.Group(ChatHub.Group(friendId)).SendAsync("ReceiveCheer", dto, cancel);

        return Results.Ok(dto);
    }

    private static async Task<IResult> NewCheers(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        DateTime cutoff = DateTime.UtcNow.AddDays(-7);

        var rows = await (from c in db.Cheers.AsNoTracking()
                          join u in db.Users.AsNoTracking() on c.FromUserId equals u.Id
                          where c.ToUserId == me && c.SeenUtc == null && c.SentUtc > cutoff
                          orderby c.Id
                          select new { c.Id, c.FromUserId, u.Username, c.Kind, c.SentUtc }).Take(50).ToListAsync(cancel);

        return Results.Ok(rows.Select(r => new CheerDto(r.Id, r.FromUserId, r.Username, r.Kind, CheerPhrases[Math.Clamp(r.Kind, 0, CheerPhrases.Length - 1)], r.SentUtc)).ToList());
    }

    private static async Task<IResult> MarkCheersSeen(SeenRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        long upTo = request?.UpToId ?? 0;
        DateTime now = DateTime.UtcNow;
        await db.Cheers.Where(c => c.ToUserId == me && c.Id <= upTo && c.SeenUtc == null).ExecuteUpdateAsync(s => s.SetProperty(c => c.SeenUtc, now), cancel);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ races

    private static async Task<IResult> MyRaces(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        DateTime now = DateTime.UtcNow;

        // Close anything overdue first, so the answer is never out of date between background-job runs.
        await WalkingLifecycleService.RunOnce(db, now, cancel);

        DateTime recent = now.AddDays(-7);
        List<StepRace> races = await db.StepRaces.AsNoTracking()
            .Where(r => (r.ChallengerId == me || r.OpponentId == me) &&
                        (r.Status == RaceStatus.Pending || r.Status == RaceStatus.Active || (r.Status == RaceStatus.Finished && r.EndsUtc > recent)))
            .OrderByDescending(r => r.CreatedUtc)
            .Take(30)
            .ToListAsync(cancel);

        List<Guid> others = races.Select(r => r.ChallengerId == me ? r.OpponentId : r.ChallengerId).Distinct().ToList();
        Dictionary<Guid, string> names = await db.Users.AsNoTracking().Where(u => others.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancel);
        List<Guid> everyone = others.Append(me).ToList();
        Dictionary<Guid, long> lifetime = await db.WalkingProfiles.AsNoTracking().Where(p => everyone.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.LifetimeSteps, cancel);

        List<RaceDto> result = races.Select(r => Describe(r, me, names, lifetime)).ToList();
        return Results.Ok(result);
    }

    private static RaceDto Describe(StepRace race, Guid me, Dictionary<Guid, string> names, Dictionary<Guid, long> lifetime)
    {
        bool iChallenged = race.ChallengerId == me;
        Guid other = iChallenged ? race.OpponentId : race.ChallengerId;

        int challengerSteps = race.ChallengerSteps;
        int opponentSteps = race.OpponentSteps;

        if (race.Status == RaceStatus.Active)
        {
            challengerSteps = Progress(lifetime, race.ChallengerId, race.ChallengerBaseline);
            opponentSteps = Progress(lifetime, race.OpponentId, race.OpponentBaseline);
        }

        string result = race.Status != RaceStatus.Finished ? "" : race.WinnerId == null ? "Draw" : race.WinnerId == me ? "Won" : "Lost";

        return new RaceDto(race.Id, race.Status.ToString(), other, names.GetValueOrDefault(other, "?"), iChallenged, race.Hours, race.CreatedUtc,
            race.StartsUtc, race.EndsUtc, iChallenged ? challengerSteps : opponentSteps, iChallenged ? opponentSteps : challengerSteps, result);
    }

    public static int Progress(Dictionary<Guid, long> lifetime, Guid userId, long baseline)
        => (int)Math.Clamp(lifetime.GetValueOrDefault(userId) - baseline, 0, int.MaxValue);

    private static async Task<IResult> Challenge(RaceRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        if (request == null || Array.IndexOf(RaceLengthsHours, request.Hours) < 0)
        {
            return Results.Problem("A race lasts 24 hours or 3 days.", statusCode: StatusCodes.Status400BadRequest);
        }

        Guid friend = request.FriendId;

        if (friend == me || !await SocialEndpoints.AreFriends(db, me, friend, cancel) || await SocialEndpoints.IsBlockedEitherWay(db, me, friend, cancel))
        {
            return Results.Problem("You can only race your friends.", statusCode: StatusCodes.Status403Forbidden);
        }

        await WalkingLifecycleService.RunOnce(db, DateTime.UtcNow, cancel);

        bool open(StepRace r) => r.Status == RaceStatus.Pending || r.Status == RaceStatus.Active;

        List<StepRace> mine = await db.StepRaces.AsNoTracking()
            .Where(r => (r.ChallengerId == me || r.OpponentId == me) && (r.Status == RaceStatus.Pending || r.Status == RaceStatus.Active))
            .ToListAsync(cancel);

        if (mine.Any(r => open(r) && (r.ChallengerId == friend || r.OpponentId == friend)))
        {
            return Results.Problem("You already have a race with them. Finish that one first!", statusCode: StatusCodes.Status409Conflict);
        }

        if (mine.Count >= MaxOpenRaces)
        {
            return Results.Problem($"You can have {MaxOpenRaces} races at a time.", statusCode: StatusCodes.Status409Conflict);
        }

        StepRace race = new() { ChallengerId = me, OpponentId = friend, Hours = request.Hours };
        db.StepRaces.Add(race);
        await db.SaveChangesAsync(cancel);

        return Results.Ok(new { id = race.Id });
    }

    private static async Task<IResult> AcceptRace(Guid raceId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        DateTime now = DateTime.UtcNow;
        await WalkingLifecycleService.RunOnce(db, now, cancel);

        StepRace? race = await db.StepRaces.FirstOrDefaultAsync(r => r.Id == raceId && r.OpponentId == me && r.Status == RaceStatus.Pending, cancel);

        if (race == null)
        {
            return Results.Problem("That challenge doesn't exist or has expired.", statusCode: StatusCodes.Status404NotFound);
        }

        int open = await db.StepRaces.CountAsync(r => (r.ChallengerId == me || r.OpponentId == me) && r.Status == RaceStatus.Active, cancel);

        if (open >= MaxOpenRaces)
        {
            return Results.Problem($"You can have {MaxOpenRaces} races at a time.", statusCode: StatusCodes.Status409Conflict);
        }

        // The starting line: everyone's accepted steps so far. Only steps walked from now on count.
        WalkingProfile challenger = await GetOrCreateProfile(db, race.ChallengerId, cancel);
        WalkingProfile opponent = await GetOrCreateProfile(db, me, cancel);

        race.Status = RaceStatus.Active;
        race.StartsUtc = now;
        race.EndsUtc = now.AddHours(race.Hours);
        race.ChallengerBaseline = challenger.LifetimeSteps;
        race.OpponentBaseline = opponent.LifetimeSteps;
        await db.SaveChangesAsync(cancel);

        return Results.NoContent();
    }

    private static async Task<IResult> CloseRace(Guid raceId, bool decline, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        // Decline = the opponent says no. Cancel = the challenger takes it back. Only while it hasn't started.
        StepRace? race = await db.StepRaces.FirstOrDefaultAsync(r => r.Id == raceId && r.Status == RaceStatus.Pending &&
                                                                      (decline ? r.OpponentId == me : r.ChallengerId == me), cancel);

        if (race == null)
        {
            return Results.Problem("That challenge doesn't exist any more.", statusCode: StatusCodes.Status404NotFound);
        }

        race.Status = decline ? RaceStatus.Declined : RaceStatus.Cancelled;
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ helpers

    public static async Task<WalkingProfile> GetOrCreateProfile(AppDbContext db, Guid userId, CancellationToken cancel)
    {
        WalkingProfile? profile = await db.WalkingProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancel);

        if (profile == null)
        {
            profile = new WalkingProfile { UserId = userId };
            db.WalkingProfiles.Add(profile);
        }

        return profile;
    }

    /// <summary>
    /// "Today" and "this week" for a player: their most recent day (if it's today or yesterday by the server's clock,
    /// which covers every time zone), and the seven days up to it.
    /// </summary>
    public static async Task<(int Today, int Week)> Summary(AppDbContext db, Guid userId, DateTime nowUtc, CancellationToken cancel)
    {
        string oldest = nowUtc.Date.AddDays(-8).ToString("yyyy-MM-dd");
        List<WalkingDay> days = await db.WalkingDays.AsNoTracking()
            .Where(d => d.UserId == userId && string.Compare(d.Day, oldest) >= 0)
            .ToListAsync(cancel);

        WalkingDay? latest = days.OrderByDescending(d => d.Day).FirstOrDefault();

        if (latest == null)
        {
            return (0, 0);
        }

        DateTime latestDate = DateTime.ParseExact(latest.Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        bool current = latestDate >= nowUtc.Date.AddDays(-1);
        string weekStart = latestDate.AddDays(-6).ToString("yyyy-MM-dd");

        int week = days.Where(d => string.Compare(d.Day, weekStart) >= 0).Sum(d => d.Steps);
        return (current ? latest.Steps : 0, current ? week : 0);
    }
}
