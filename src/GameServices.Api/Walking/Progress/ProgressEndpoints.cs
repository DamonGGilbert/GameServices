using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Moderation;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Progress;

public record QuestDto(string Id, int Progress, int State);
public record AchievementDto(string Id, bool Unlocked, bool Claimed, long UnlockedUnix);
public record MedalDto(string Id, int Reached, int Claimed, List<long> EarnedUnix);
public record StatDto(int Stat, long Value);
public record OddsDto(string Name, int Percent);

/// <param name="Earned">How many finds today's steps have brought.</param>
/// <param name="NextAtSteps">The steps that bring the next one (0 = you've earned them all).</param>
public record DiscoveryDto(int Found, int Earned, int Most, int NextAtSteps, List<int> Steps, List<OddsDto> Odds);

/// <summary>Everything about an explorer's progress: what the game shows, all decided here.</summary>
public record ProgressDto(bool Started, string Day, int Level, int Xp, int XpForNext, int Slots, int Glimmer, int Fragments, long LifetimeSteps, long LifetimeMetres,
    int StepsToday, bool GoalCompleted, int DailyGoals, int CurrentStreak, int LongestStreak, string LastGoalDay, int RestDays, string RestDaysWeek,
    List<int> ClaimedMilestones, List<QuestDto> Quests, List<AchievementDto> Achievements, List<MedalDto> Medals, List<string> Titles, List<string> Badges, List<string> Cosmetics,
    List<StatDto> Stats, DiscoveryDto Discovery, long BoostUntilUnix, float BoostMultiplier, string Title);

/// <param name="Kind">"milestone", "quest", "achievement" or "medal".</param>
/// <param name="Id">The milestone's steps, or the quest's, achievement's or medal ladder's id.</param>
public record ClaimRequest(string Kind, string Id);

public record ClaimResultDto(int Glimmer, int Xp, int Fragments, int Monsters, List<string> Extras, int Milestones, int Quests, ProgressDto Progress);

public record MedalClaimDto(string Id, int Claimed);

/// <summary>
/// What a phone has from before it signed in: its own walking, battles and medals. Taken once (Start), within limits, and
/// it can't earn anything: what comes across counts for the ladders, but it's already been paid for on the phone.
/// </summary>
public record StartRequest(int Level, int Xp, long LifetimeSteps, double LifetimeMetres, int DailyGoals, int CurrentStreak, int LongestStreak, string? LastGoalDay, int RestDays, string? RestDaysWeek,
    int BattlesWon, int BattlesLost, int Trades, int Areas, int RareFinds, int LegendaryFinds, int Routes, int Challenges, int Fragments,
    List<string>? Species, List<string>? Cosmetics, List<string>? Titles, List<MedalClaimDto>? Medals, List<string>? ClaimedAchievements, string? Day, List<int>? ClaimedMilestones);

/// <summary>
/// THE PHONE ASKS, THE SERVER DECIDES: levels, milestones, quests, achievements, medals and streaks (see Explorer for the
/// rules, Discoveries for finds on the map).
///
///   GET  /api/walking/progress            everything, as of now
///   POST /api/walking/progress/start      what a phone has from before it signed in (once)
///   POST /api/walking/progress/claim      collect one reward
///   POST /api/walking/progress/claim-all  collect everything ready: milestones and quests
/// </summary>
public static class ProgressEndpoints
{
    // How much can come across from a phone. Generous for anyone who's really been walking, and no use to a cheat: none of
    // it earns any Glimmer, and a level this high only brings a few more slots.
    public const int MostStartingLevel = 15;
    public const long MostStartingSteps = 200_000;
    public const int MostStartingCount = 200;
    public const int MostStartingStreak = 60;
    public const int MostStartingFragments = 100;
    public const int MostStartingFinds = 20;

    public static RouteGroupBuilder MapProgressEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/progress", Get);
        group.MapPost("/progress/start", Start);
        group.MapPost("/progress/claim", Claim);
        group.MapPost("/progress/claim-all", ClaimAll);
        group.MapDiscoveryEndpoints();
        return group;
    }

    /// <param name="offset">The phone's time zone in minutes, so "today" is the walker's today even before their first steps.</param>
    private static async Task<IResult> Get(int? offset, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        if (offset is int minutes && Math.Abs(minutes) <= StepRules.MaxUtcOffsetMinutes) profile.UtcOffsetMinutes = minutes;

        ProgressDto dto = await BuildAsync(db, profile, DateTime.UtcNow, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(dto);
    }

    /// <summary>Brings everything up to date and says where it's got to. The caller saves.</summary>
    public static async Task<ProgressDto> BuildAsync(AppDbContext db, WalkingProfile profile, DateTime now, CancellationToken cancel)
    {
        string day = Explorer.Today(profile, now);
        WalkingDay row = await Explorer.DayAsync(db, profile.UserId, day, cancel);
        Dictionary<ExplorerStat, long> stats = await Explorer.StatsAsync(db, profile, row, cancel);

        // Before the phone's first visit there's nothing to work out yet: what it brings comes first (Start).
        var quests = new List<Explorer.QuestEntry>();
        if (profile.ProgressStarted)
        {
            await Explorer.RefreshAsync(db, profile, stats, now, cancel);
            quests = Explorer.EnsureQuests(row, stats);
        }

        var achievements = await db.WalkingAchievements.AsNoTracking().Where(a => a.UserId == profile.UserId).ToListAsync(cancel);
        foreach (WalkingAchievement a in db.WalkingAchievements.Local.Where(a => a.UserId == profile.UserId && !achievements.Any(x => x.AchievementId == a.AchievementId))) achievements.Add(a);
        var medals = await db.WalkingMedals.AsNoTracking().Where(m => m.UserId == profile.UserId).ToListAsync(cancel);
        for (int i = 0; i < medals.Count; i++) medals[i] = db.WalkingMedals.Local.FirstOrDefault(m => m.UserId == profile.UserId && m.TrackId == medals[i].TrackId) ?? medals[i];
        foreach (WalkingMedal m in db.WalkingMedals.Local.Where(m => m.UserId == profile.UserId && !medals.Any(x => x.TrackId == m.TrackId))) medals.Add(m);

        MonsterCatalog.DiscoveryEntry finds = MonsterCatalog.Shared.Discovery;
        float boost = Discoveries.BoostOf(profile, now);

        return new ProgressDto(profile.ProgressStarted, day, profile.PlayerLevel, profile.PlayerXp, Explorer.XpForLevel(profile.PlayerLevel), profile.MonsterSlots, profile.Glimmer, profile.Fragments,
            stats[ExplorerStat.LifetimeSteps], profile.LifetimeMetres, row.Steps, row.GoalCompleted, profile.DailyGoalsCompleted, profile.CurrentStreak, profile.LongestStreak, profile.LastGoalDay, profile.RestDays, profile.RestDaysWeek,
            ClaimedMilestones(row), quests.Select(q => ToDto(q, stats)).ToList(),
            achievements.Select(a => new AchievementDto(a.AchievementId, a.Unlocked, a.Claimed, a.UnlockedUtc is DateTime t ? new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds() : 0)).ToList(),
            medals.Select(m => new MedalDto(m.TrackId, m.Reached, m.Claimed, EarnedTimes(m))).ToList(),
            Explorer.Items(profile.Titles), Explorer.Items(profile.Badges), Explorer.Items(profile.Cosmetics),
            stats.Select(s => new StatDto((int)s.Key, s.Value)).ToList(),
            new DiscoveryDto(row.Discoveries, finds.Earned(row.Steps), finds.Steps.Count, finds.NextAt(row.Steps), finds.Steps, Discoveries.Odds(finds, boost)),
            profile.BoostUntilUtc > now ? new DateTimeOffset(profile.BoostUntilUtc.Value, TimeSpan.Zero).ToUnixTimeSeconds() : 0, boost, profile.Title);
    }

    private static List<long> EarnedTimes(WalkingMedal medal) =>
        Explorer.Items(medal.EarnedUnix).Select(e => long.Parse(e.Split(':')[0], System.Globalization.CultureInfo.InvariantCulture)).ToList();

    private static List<int> ClaimedMilestones(WalkingDay row) => Explorer.Items(row.ClaimedMilestones).Select(int.Parse).ToList();

    private static QuestDto ToDto(Explorer.QuestEntry quest, Dictionary<ExplorerStat, long> stats)
    {
        MonsterCatalog.QuestEntry? def = MonsterCatalog.Shared.Progression.Quests.FirstOrDefault(q => q.Id == quest.Id);
        if (def == null) return new QuestDto(quest.Id, 0, quest.Claimed ? 3 : 0);

        int progress = (int)Math.Min(def.Target, Math.Max(0, stats.GetValueOrDefault((ExplorerStat)def.Stat) - quest.Baseline));
        int state = quest.Claimed ? 3 : progress >= def.Target ? 2 : progress > 0 ? 1 : 0;   // the game's QuestState
        return new QuestDto(quest.Id, progress, state);
    }

    // ------------------------------------------------------------------ what a phone brings

    private static async Task<IResult> Start(StartRequest request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        DateTime now = DateTime.UtcNow;

        if (!profile.ProgressStarted)
        {
            profile.ProgressStarted = true;
            await MonsterEndpoints.GiveStarterIfNewAsync(db, me, finder, cancel);   // before anything else can arrive
            await TakeFromPhoneAsync(db, profile, request, now, cancel);
        }

        ProgressDto dto = await BuildAsync(db, profile, now, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(dto);
    }

    private static async Task TakeFromPhoneAsync(AppDbContext db, WalkingProfile profile, StartRequest request, DateTime now, CancellationToken cancel)
    {
        MonsterCatalog catalog = MonsterCatalog.Shared;

        int level = Math.Clamp(request.Level, 1, MostStartingLevel);
        if (level > profile.PlayerLevel)
        {
            profile.PlayerLevel = level;
            profile.PlayerXp = Math.Clamp(request.Xp, 0, Explorer.XpForLevel(level) - 1);
            profile.MonsterSlots = Explorer.SlotsFor(level);
        }

        profile.StartingSteps = Math.Clamp(request.LifetimeSteps - profile.LifetimeSteps, 0, MostStartingSteps);
        long walked = profile.LifetimeSteps + profile.StartingSteps;
        profile.LifetimeMetres = Math.Max(profile.LifetimeMetres, (long)Math.Clamp(request.LifetimeMetres, 0, walked * Explorer.MaxMetresPerStep));

        profile.DailyGoalsCompleted += Math.Clamp(request.DailyGoals, 0, MostStartingCount);
        profile.BattlesWon += Math.Clamp(request.BattlesWon, 0, MostStartingCount);
        profile.BattlesLost += Math.Clamp(request.BattlesLost, 0, MostStartingCount);
        profile.TradesCompleted += Math.Clamp(request.Trades, 0, MostStartingCount);
        profile.AreasVisited += Math.Clamp(request.Areas, 0, MostStartingCount);
        profile.RareFinds += Math.Clamp(request.RareFinds, 0, MostStartingFinds);
        profile.LegendaryFinds += Math.Clamp(request.LegendaryFinds, 0, MostStartingFinds);
        profile.RoutesCompleted += Math.Clamp(request.Routes, 0, MostStartingCount);
        profile.ChallengesCompleted += Math.Clamp(request.Challenges, 0, MostStartingCount);
        profile.Fragments += Math.Clamp(request.Fragments, 0, MostStartingFragments);

        profile.CurrentStreak = Math.Max(profile.CurrentStreak, Math.Clamp(request.CurrentStreak, 0, MostStartingStreak));
        profile.LongestStreak = Math.Max(profile.LongestStreak, Math.Max(profile.CurrentStreak, Math.Clamp(request.LongestStreak, 0, MostStartingStreak)));
        if (IsDay(request.LastGoalDay) && string.CompareOrdinal(request.LastGoalDay, Explorer.Today(profile, now)) <= 0) profile.LastGoalDay = request.LastGoalDay!;
        profile.RestDays = Math.Clamp(request.RestDays, 0, 2);
        if (request.RestDaysWeek?.Length <= 10) profile.RestDaysWeek = request.RestDaysWeek;

        foreach (string id in request.Species ?? new List<string>())
        {
            if (catalog.FindSpecies(id) != null) profile.DiscoveredSpecies = Explorer.WithItem(profile.DiscoveredSpecies, id);
        }

        foreach (string item in (request.Cosmetics ?? new List<string>()).Take(50))
        {
            if (item.Length <= 40 && ContentFilter.IsClean(item)) profile.Cosmetics = Explorer.WithItem(profile.Cosmetics, item);
        }

        // Titles the ladders can't give again: only ones as far as Gold, the first that ever gives a title.
        foreach (string title in (request.Titles ?? new List<string>()).Take(20))
        {
            if (title.StartsWith("Gold ", StringComparison.Ordinal) && Explorers.CleanTitle(title).Length > 0) profile.Titles = Explorer.WithItem(profile.Titles, title);
        }

        WalkingDay row = await Explorer.DayAsync(db, profile.UserId, Explorer.Today(profile, now), cancel);
        if (request.Day == row.Day)
        {
            foreach (int steps in request.ClaimedMilestones ?? new List<int>())
            {
                if (catalog.Progression.Milestones.Any(m => m.Steps == steps)) row.ClaimedMilestones = Explorer.WithItem(row.ClaimedMilestones, steps.ToString());
            }
        }

        // Medals and achievements the phone paid for stay paid for. Anything it had reached by now counts as paid too.
        foreach (MedalClaimDto claim in request.Medals ?? new List<MedalClaimDto>())
        {
            if (catalog.Progression.Medals.All(m => m.Id != claim.Id)) continue;
            db.WalkingMedals.Add(new WalkingMedal { UserId = profile.UserId, TrackId = claim.Id, Claimed = Math.Max(0, claim.Claimed) });
        }

        foreach (string id in request.ClaimedAchievements ?? new List<string>())
        {
            if (catalog.Progression.Achievements.All(a => a.Id != id)) continue;
            db.WalkingAchievements.Add(new WalkingAchievement { UserId = profile.UserId, AchievementId = id, Claimed = true });
        }

        await Explorer.RefreshAsync(db, profile, await Explorer.StatsAsync(db, profile, row, cancel), now, cancel, alreadyPaid: true);
        ActivityLog.Add(db, profile.UserId, "start", "Came from the phone: level " + request.Level + ", " + request.LifetimeSteps + " steps, " + request.BattlesWon + " wins (kept: level " + profile.PlayerLevel + ")");
    }

    private static bool IsDay(string? day) =>
        DateTime.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _);

    // ------------------------------------------------------------------ collecting

    private static async Task<IResult> Claim(ClaimRequest request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        profile.ProgressStarted = true;

        DateTime now = DateTime.UtcNow;
        string day = Explorer.Today(profile, now);
        WalkingDay row = await Explorer.DayAsync(db, me, day, cancel);
        Dictionary<ExplorerStat, long> stats = await Explorer.StatsAsync(db, profile, row, cancel);
        await Explorer.RefreshAsync(db, profile, stats, now, cancel);

        var tally = new RewardTally();
        string? problem = (request?.Kind) switch
        {
            "milestone" => await ClaimMilestoneAsync(db, profile, finder, row, request.Id, now, tally, cancel),
            "quest" => await ClaimQuestAsync(db, profile, finder, row, stats, request.Id, now, tally, cancel),
            "achievement" => await ClaimAchievementAsync(db, profile, finder, day, request.Id, now, tally, cancel),
            "medal" => await ClaimMedalAsync(db, profile, request.Id, tally, cancel),
            _ => "That isn't something to collect.",
        };

        if (problem != null) return Results.Problem(problem, statusCode: StatusCodes.Status409Conflict);

        ActivityLog.Add(db, me, "claim", request!.Kind + " " + request.Id + ": " + tally.Describe());
        ProgressDto dto = await BuildAsync(db, profile, now, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(new ClaimResultDto(tally.Glimmer, tally.Xp, tally.Fragments, tally.Monsters, tally.Extras, request.Kind == "milestone" ? 1 : 0, request.Kind == "quest" ? 1 : 0, dto));
    }

    private static async Task<IResult> ClaimAll(ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        profile.ProgressStarted = true;

        DateTime now = DateTime.UtcNow;
        WalkingDay row = await Explorer.DayAsync(db, me, Explorer.Today(profile, now), cancel);
        Dictionary<ExplorerStat, long> stats = await Explorer.StatsAsync(db, profile, row, cancel);
        var tally = new RewardTally();
        int milestones = 0, quests = 0;

        foreach (MonsterCatalog.MilestoneEntry m in MonsterCatalog.Shared.Progression.Milestones)
        {
            if (await ClaimMilestoneAsync(db, profile, finder, row, m.Steps.ToString(), now, tally, cancel) == null) milestones++;
        }

        foreach (Explorer.QuestEntry q in Explorer.EnsureQuests(row, stats))
        {
            if (await ClaimQuestAsync(db, profile, finder, row, stats, q.Id, now, tally, cancel) == null) quests++;
        }

        if (milestones + quests > 0) ActivityLog.Add(db, me, "claim", "all: " + milestones + " milestones, " + quests + " quests: " + tally.Describe());
        ProgressDto dto = await BuildAsync(db, profile, now, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(new ClaimResultDto(tally.Glimmer, tally.Xp, tally.Fragments, tally.Monsters, tally.Extras, milestones, quests, dto));
    }

    private static async Task<string?> ClaimMilestoneAsync(AppDbContext db, WalkingProfile profile, MonsterFinder finder, WalkingDay row, string? id, DateTime now, RewardTally tally, CancellationToken cancel)
    {
        MonsterCatalog.MilestoneEntry? milestone = int.TryParse(id, out int steps) ? MonsterCatalog.Shared.Progression.Milestones.FirstOrDefault(m => m.Steps == steps) : null;
        if (milestone == null) return "Unknown milestone.";
        if (row.Steps < milestone.Steps) return "Your latest steps are still on their way. Try again in a moment.";
        if (Explorer.HasItem(row.ClaimedMilestones, id!)) return "Already claimed today.";

        row.ClaimedMilestones = Explorer.WithItem(row.ClaimedMilestones, id!);
        await Explorer.GrantAsync(db, profile, finder, milestone.Reward, "milestone", "Milestone: " + milestone.Steps.ToString("N0") + " steps", row.Day, now, tally, cancel);
        return null;
    }

    private static async Task<string?> ClaimQuestAsync(AppDbContext db, WalkingProfile profile, MonsterFinder finder, WalkingDay row, Dictionary<ExplorerStat, long> stats, string? id, DateTime now, RewardTally tally, CancellationToken cancel)
    {
        List<Explorer.QuestEntry> quests = Explorer.EnsureQuests(row, stats);
        Explorer.QuestEntry? quest = quests.FirstOrDefault(q => q.Id == id);
        MonsterCatalog.QuestEntry? def = MonsterCatalog.Shared.Progression.Quests.FirstOrDefault(q => q.Id == id);
        if (quest == null || def == null) return "That quest isn't active today.";
        if (quest.Claimed) return "Already claimed.";
        if (ToDto(quest, stats).State != 2) return "Not finished yet.";

        quest.Claimed = true;
        Explorer.SaveQuests(row, quests);
        await Explorer.GrantAsync(db, profile, finder, def.Reward, "quest", "Quest: " + def.Id, row.Day, now, tally, cancel);
        return null;
    }

    private static async Task<string?> ClaimAchievementAsync(AppDbContext db, WalkingProfile profile, MonsterFinder finder, string day, string? id, DateTime now, RewardTally tally, CancellationToken cancel)
    {
        MonsterCatalog.AchievementEntry? def = MonsterCatalog.Shared.Progression.Achievements.FirstOrDefault(a => a.Id == id);
        WalkingAchievement? row = db.WalkingAchievements.Local.FirstOrDefault(a => a.UserId == profile.UserId && a.AchievementId == id)
                                  ?? await db.WalkingAchievements.FirstOrDefaultAsync(a => a.UserId == profile.UserId && a.AchievementId == id, cancel);
        if (def == null || row is not { Unlocked: true }) return "Achievement not unlocked yet.";
        if (row.Claimed) return "Already claimed.";

        row.Claimed = true;
        await Explorer.GrantAsync(db, profile, finder, def.Reward, "achievement", "Achievement: " + def.Id, day, now, tally, cancel);
        return null;
    }

    private static async Task<string?> ClaimMedalAsync(AppDbContext db, WalkingProfile profile, string? id, RewardTally tally, CancellationToken cancel)
    {
        WalkingMedal? row = db.WalkingMedals.Local.FirstOrDefault(m => m.UserId == profile.UserId && m.TrackId == id)
                            ?? await db.WalkingMedals.FirstOrDefaultAsync(m => m.UserId == profile.UserId && m.TrackId == id, cancel);
        if (row == null || row.Claimed >= row.Reached) return "No medal rewards to collect here yet.";

        int glimmer = 0;
        for (int rung = row.Claimed; rung < row.Reached; rung++) glimmer += Explorer.MedalReward(rung);
        int count = row.Reached - row.Claimed;
        row.Claimed = row.Reached;
        WalkingWallet.Change(db, profile, glimmer, "medal", "Medals: " + id + " (" + count + ")");
        tally.Glimmer += glimmer;
        return null;
    }
}
