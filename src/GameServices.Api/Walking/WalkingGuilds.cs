using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Guilds;
using GameServices.Api.Infrastructure;
using GameServices.Api.Social;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking;

public record JourneyMemberDto(Guid UserId, string Username, int Steps);

/// <summary>
/// This week's Guild Journey, plus last week's result (flat fields rather than a nested object, because Unity's
/// JsonUtility can't tell "no object" from "an empty one").
/// </summary>
public record JourneyDto(
    string WeekStart,
    DateTime EndsUtc,
    int Target,
    int Total,
    int MySteps,
    int MyGlimmerSoFar,
    List<JourneyMemberDto> Members,
    bool HasLastWeek,
    string LastWeekStart,
    int LastWeekTarget,
    int LastWeekTotal,
    int LastWeekMySteps,
    int LastWeekGlimmer,
    bool LastWeekClaimed);

public record JourneyClaimResult(string WeekStart, int Glimmer, string Message);

public record RaceWalkerDto(string Username, int Steps, bool Counts);

public record GuildStepRaceDto(
    string Status,
    string OpponentName,
    string OpponentTag,
    int OpponentRating,
    int MyScore,
    int TheirScore,
    DateTime StartsUtc,
    DateTime EndsUtc,
    DateTime NextStartsUtc,
    string Result,
    List<RaceWalkerDto> Mine,
    List<RaceWalkerDto> Theirs,
    string Note);

public record WalkingGuildRankDto(int Rank, Guid GuildId, string Name, string Tag, int Rating, int WarWins, string TierName, int Members);

public record WalkingGuildRankingDto(List<WalkingGuildRankDto> Items, Guid? MyGuildId, int MyRank);

/// <summary>
/// WALKING-GAME GUILDS: the two things a walking guild does together.
///
///   GUILD JOURNEY (every week, Monday to Sunday, UTC)
///     The guild shares one step target: 5,000 steps a day for each member, so a guild of 4 aims for 140,000.
///     Everyone's believable steps (see StepRules) count towards it. Rewards depend on BOTH how far the guild got
///     and how much you walked compared with your fair share, and are capped, so one very keen walker can't carry
///     everyone and nobody earns a fortune. Rewards are collected the week after.
///
///   WEEKEND STEP RACE (Saturday 00:00 to Monday 00:00, UTC)
///     Every walking guild with at least 2 members is paired with a guild of similar rating. A guild's score is
///     the weekend steps of its TOP 5 walkers (so a big guild can't win just by being big). The winner's rating
///     and war wins go up, which raises its tier and unlocks shinier banners - the same tiers as the card game.
///
///   GET  /api/walking/guild/journey        this week's Journey (and last week's result)
///   POST /api/walking/guild/journey/claim  collect last week's reward
///   GET  /api/walking/guild/race           this weekend's race (or the last one, or when the next starts)
///   GET  /api/walking/guild/ranking        the top walking guilds
/// </summary>
public static class WalkingGuilds
{
    public const int StepsPerMemberPerDay = 5_000;
    public const int MaxJourneyReward = 250;
    public const int JourneyCompleteBonus = 100;
    public const int RaceTopWalkers = 5;
    public const int RaceMinMembers = 2;

    public static void MapWalkingGuildEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/guild/journey", Journey);
        group.MapPost("/guild/journey/claim", ClaimJourney);
        group.MapGet("/guild/race", Race);
        group.MapGet("/guild/ranking", Ranking);
    }

    // ============================================================ the rules (pure maths, unit tested)

    public static int TargetFor(int members) => Math.Max(1, members) * StepsPerMemberPerDay * 7;

    /// <summary>
    /// A player's Journey reward. "How far the guild got" (0-100%) times "how much of your fair share you walked"
    /// (0-100%) of the maximum, plus a bonus when the guild reached its target and you helped.
    /// </summary>
    public static int JourneyReward(int total, int target, int mySteps, int members)
    {
        if (mySteps <= 0 || target <= 0)
        {
            return 0;
        }

        double guildDone = Math.Min(1.0, total / (double)target);
        double fairShare = target / (double)Math.Max(1, members);
        double myPart = Math.Min(1.0, mySteps / fairShare);
        int reward = (int)Math.Round(MaxJourneyReward * guildDone * myPart);

        return reward + (guildDone >= 1.0 ? JourneyCompleteBonus : 0);
    }

    /// <summary>The Monday (UTC) that starts the week containing "now".</summary>
    public static DateTime WeekStart(DateTime nowUtc)
    {
        int sinceMonday = ((int)nowUtc.DayOfWeek + 6) % 7;
        return nowUtc.Date.AddDays(-sinceMonday);
    }

    public static string DayKey(DateTime date) => date.ToString("yyyy-MM-dd");

    /// <summary>A guild's race score: the total of its best walkers (only the top 5 count).</summary>
    public static int RaceScore(IEnumerable<int> walkerSteps) => walkerSteps.OrderByDescending(s => s).Take(RaceTopWalkers).Sum();

    // ============================================================ reading steps

    // Each member's steps between two calendar days (inclusive). Step days are the player's own calendar days, so
    // they line up with a UTC week to within a few hours - fair enough for a guild goal.
    private static async Task<List<JourneyMemberDto>> MemberSteps(AppDbContext db, Guid guildId, DateTime fromDay, DateTime toDay, CancellationToken cancel)
    {
        string from = DayKey(fromDay);
        string to = DayKey(toDay);

        var members = await (from m in db.GuildMembers.AsNoTracking()
                             join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                             where m.GuildId == guildId
                             select new { m.UserId, u.Username }).ToListAsync(cancel);

        List<Guid> ids = members.Select(m => m.UserId).ToList();

        Dictionary<Guid, int> steps = await db.WalkingDays.AsNoTracking()
            .Where(d => ids.Contains(d.UserId) && string.Compare(d.Day, from) >= 0 && string.Compare(d.Day, to) <= 0)
            .GroupBy(d => d.UserId)
            .Select(g => new { UserId = g.Key, Steps = g.Sum(d => d.Steps) })
            .ToDictionaryAsync(x => x.UserId, x => x.Steps, cancel);

        return members
            .Select(m => new JourneyMemberDto(m.UserId, m.Username, steps.GetValueOrDefault(m.UserId)))
            .OrderByDescending(m => m.Steps).ThenBy(m => m.Username)
            .ToList();
    }

    private static Task<GuildMember?> WalkingMembership(AppDbContext db, Guid userId, CancellationToken cancel)
        => db.GuildMembers.FirstOrDefaultAsync(m => m.UserId == userId && m.GameId == GameIds.Walking, cancel);

    private static IResult NotInGuild()
        => Results.Problem("You're not in a walking guild yet.", statusCode: StatusCodes.Status404NotFound);

    // ============================================================ GUILD JOURNEY

    private static async Task<IResult> Journey(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? membership = await WalkingMembership(db, me, cancel);
        if (membership == null) return NotInGuild();

        DateTime now = DateTime.UtcNow;
        DateTime monday = WeekStart(now);
        DateTime lastMonday = monday.AddDays(-7);

        List<JourneyMemberDto> week = await MemberSteps(db, membership.GuildId, monday, monday.AddDays(6), cancel);
        int target = TargetFor(week.Count);
        int total = week.Sum(m => m.Steps);
        int mine = week.FirstOrDefault(m => m.UserId == me)?.Steps ?? 0;

        // Last week: only worth showing if the player walked or can still collect something.
        List<JourneyMemberDto> last = await MemberSteps(db, membership.GuildId, lastMonday, monday.AddDays(-1), cancel);
        int lastTarget = TargetFor(last.Count);
        int lastTotal = last.Sum(m => m.Steps);
        int lastMine = last.FirstOrDefault(m => m.UserId == me)?.Steps ?? 0;
        int lastReward = JourneyReward(lastTotal, lastTarget, lastMine, last.Count);
        bool claimed = await db.GuildJourneyClaims.AnyAsync(c => c.UserId == me && c.WeekStart == DayKey(lastMonday), cancel);

        return Results.Ok(new JourneyDto(
            DayKey(monday), monday.AddDays(7), target, total, mine, JourneyReward(total, target, mine, week.Count), week,
            lastTotal > 0, DayKey(lastMonday), lastTarget, lastTotal, lastMine, lastReward, claimed));
    }

    private static async Task<IResult> ClaimJourney(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? membership = await WalkingMembership(db, me, cancel);
        if (membership == null) return NotInGuild();

        DateTime monday = WeekStart(DateTime.UtcNow);
        DateTime lastMonday = monday.AddDays(-7);
        string week = DayKey(lastMonday);

        if (await db.GuildJourneyClaims.AnyAsync(c => c.UserId == me && c.WeekStart == week, cancel))
        {
            return Results.Problem("You've already collected last week's Journey reward.", statusCode: StatusCodes.Status409Conflict);
        }

        List<JourneyMemberDto> last = await MemberSteps(db, membership.GuildId, lastMonday, monday.AddDays(-1), cancel);
        int mine = last.FirstOrDefault(m => m.UserId == me)?.Steps ?? 0;
        int reward = JourneyReward(last.Sum(m => m.Steps), TargetFor(last.Count), mine, last.Count);

        if (reward <= 0)
        {
            return Results.Problem("There's nothing to collect for last week - walk this week to earn a reward!", statusCode: StatusCodes.Status400BadRequest);
        }

        db.GuildJourneyClaims.Add(new GuildJourneyClaim { UserId = me, GuildId = membership.GuildId, WeekStart = week, Glimmer = reward });

        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateException)
        {
            // Two taps at once: the unique index let only one through.
            return Results.Problem("You've already collected last week's Journey reward.", statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Ok(new JourneyClaimResult(week, reward, "Journey reward collected: " + reward + " Glimmer!"));
    }

    // ============================================================ WEEKEND STEP RACE (the job)

    /// <summary>One pass of the job: finish races that ended, then pair guilds for this weekend. Public for tests.</summary>
    public static async Task<int> RunOnce(AppDbContext db, DateTime now, CancellationToken cancel = default)
    {
        int changes = await FinishRaces(db, now, cancel);
        DateTime? weekend = GuildWars.WeekendStart(now);

        if (weekend != null)
        {
            changes += await PairGuilds(db, weekend.Value, cancel);
        }

        return changes;
    }

    private static async Task<int> PairGuilds(AppDbContext db, DateTime weekendStart, CancellationToken cancel)
    {
        var pairs = await db.GuildStepRaces.AsNoTracking()
            .Where(r => r.StartsUtc == weekendStart)
            .Select(r => new { r.GuildAId, r.GuildBId })
            .ToListAsync(cancel);

        List<Guid> busy = pairs.SelectMany(p => new[] { p.GuildAId, p.GuildBId }).ToList();

        List<Guid> bigEnough = await db.GuildMembers.AsNoTracking()
            .Where(m => m.GameId == GameIds.Walking)
            .GroupBy(m => m.GuildId)
            .Where(g => g.Count() >= RaceMinMembers)
            .Select(g => g.Key)
            .ToListAsync(cancel);

        List<Guild> guilds = await db.Guilds.AsNoTracking()
            .Where(g => g.GameId == GameIds.Walking && bigEnough.Contains(g.Id) && !busy.Contains(g.Id))
            .ToListAsync(cancel);

        // Similar ratings race each other. An odd guild out waits for next weekend (or a guild that grows late).
        List<Guild> ordered = guilds.OrderByDescending(g => g.Rating).ThenBy(g => g.Id).ToList();
        int created = 0;

        for (int i = 0; i + 1 < ordered.Count; i += 2)
        {
            db.GuildStepRaces.Add(new GuildStepRace
            {
                GuildAId = ordered[i].Id,
                GuildBId = ordered[i + 1].Id,
                StartsUtc = weekendStart,
                EndsUtc = GuildWars.WeekendEnd(weekendStart)
            });

            created++;
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(cancel);
        }

        return created;
    }

    private static async Task<int> FinishRaces(AppDbContext db, DateTime now, CancellationToken cancel)
    {
        List<GuildStepRace> over = await db.GuildStepRaces.Where(r => r.Status == RaceStatus.Active && r.EndsUtc <= now).ToListAsync(cancel);

        foreach (GuildStepRace race in over)
        {
            race.ScoreA = RaceScore((await RaceWalkers(db, race.GuildAId, race.StartsUtc, cancel)).Select(w => w.Steps));
            race.ScoreB = RaceScore((await RaceWalkers(db, race.GuildBId, race.StartsUtc, cancel)).Select(w => w.Steps));
            race.Status = RaceStatus.Finished;

            Guid? winner = race.ScoreA > race.ScoreB ? race.GuildAId : race.ScoreB > race.ScoreA ? race.GuildBId : null;
            race.WinnerGuildId = winner;

            Guild? a = await db.Guilds.FirstOrDefaultAsync(g => g.Id == race.GuildAId, cancel);
            Guild? b = await db.Guilds.FirstOrDefaultAsync(g => g.Id == race.GuildBId, cancel);

            if (a != null && b != null && winner != null)
            {
                Guild won = winner == a.Id ? a : b;
                Guild lost = winner == a.Id ? b : a;

                // The same reward as a card-game war win, so tiers mean the same thing in both games.
                won.WarWins++;
                won.Rating += GuildWars.WinnerRatingBonus;
                lost.WarLosses++;
            }
        }

        if (over.Count > 0)
        {
            await db.SaveChangesAsync(cancel);
        }

        return over.Count;
    }

    // A guild's walkers for the weekend starting on that Saturday, best first.
    private static Task<List<JourneyMemberDto>> RaceWalkers(AppDbContext db, Guid guildId, DateTime saturday, CancellationToken cancel)
        => MemberSteps(db, guildId, saturday, saturday.AddDays(1), cancel);

    // ============================================================ WEEKEND STEP RACE (what the game shows)

    private static async Task<IResult> Race(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? membership = await WalkingMembership(db, me, cancel);
        if (membership == null) return NotInGuild();

        return Results.Ok(await DescribeRace(db, membership.GuildId, DateTime.UtcNow, cancel));
    }

    public static async Task<GuildStepRaceDto> DescribeRace(AppDbContext db, Guid guildId, DateTime now, CancellationToken cancel)
    {
        DateTime next = GuildWars.NextWeekendStart(now);

        GuildStepRace? race = await db.GuildStepRaces.AsNoTracking()
            .Where(r => (r.GuildAId == guildId || r.GuildBId == guildId) && r.StartsUtc <= now)
            .OrderByDescending(r => r.StartsUtc)
            .FirstOrDefaultAsync(cancel);

        // A finished race stays on show for a few days so everyone sees the result.
        if (race == null || (race.Status == RaceStatus.Finished && now - race.EndsUtc > TimeSpan.FromDays(5)))
        {
            int members = await db.GuildMembers.CountAsync(m => m.GuildId == guildId, cancel);
            string note = members < RaceMinMembers
                ? $"Your guild needs at least {RaceMinMembers} members to race. Invite a friend!"
                : GuildWars.WeekendStart(now) != null
                    ? "Every rival guild was already paired this weekend. You'll be matched next Saturday."
                    : "On Saturday your guild is matched with a guild of similar rating. The top 5 walkers' weekend steps are your score.";

            return new GuildStepRaceDto("None", "", "", 0, 0, 0, next, GuildWars.WeekendEnd(next), next, "", new List<RaceWalkerDto>(), new List<RaceWalkerDto>(), note);
        }

        bool iAmA = race.GuildAId == guildId;
        Guid opponentId = iAmA ? race.GuildBId : race.GuildAId;
        Guild? opponent = await db.Guilds.AsNoTracking().FirstOrDefaultAsync(g => g.Id == opponentId, cancel);

        List<RaceWalkerDto> Walkers(List<JourneyMemberDto> list)
            => list.Select((w, i) => new RaceWalkerDto(w.Username, w.Steps, i < RaceTopWalkers)).ToList();

        List<JourneyMemberDto> mine = await RaceWalkers(db, guildId, race.StartsUtc, cancel);
        List<JourneyMemberDto> theirs = await RaceWalkers(db, opponentId, race.StartsUtc, cancel);

        bool active = race.Status == RaceStatus.Active && race.EndsUtc > now;

        // While running, the score is live; once finished, it's the score that was locked in.
        int myScore = active ? RaceScore(mine.Select(w => w.Steps)) : iAmA ? race.ScoreA : race.ScoreB;
        int theirScore = active ? RaceScore(theirs.Select(w => w.Steps)) : iAmA ? race.ScoreB : race.ScoreA;
        string result = active ? "" : race.WinnerGuildId == null ? "Draw" : race.WinnerGuildId == guildId ? "Won" : "Lost";

        string explain = active
            ? "Your top 5 walkers' steps this weekend are your score. Every walk counts - get outside!"
            : "The race is over. The next one starts on Saturday.";

        return new GuildStepRaceDto(
            active ? "Active" : "Finished",
            opponent?.Name ?? "?",
            opponent?.Tag ?? "",
            opponent?.Rating ?? 0,
            myScore,
            theirScore,
            race.StartsUtc,
            race.EndsUtc,
            next,
            result,
            Walkers(mine),
            Walkers(theirs),
            explain);
    }

    // ============================================================ RANKING

    private static async Task<IResult> Ranking(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        GuildMember? membership = await WalkingMembership(db, me, cancel);

        var guilds = await db.Guilds.AsNoTracking()
            .Where(g => g.GameId == GameIds.Walking)
            .Select(g => new { g.Id, g.Name, g.Tag, g.Rating, g.WarWins, Members = db.GuildMembers.Count(m => m.GuildId == g.Id) })
            .ToListAsync(cancel);

        List<WalkingGuildRankDto> ranked = guilds
            .OrderByDescending(g => GuildTiers.Score(g.Rating, g.WarWins)).ThenBy(g => g.Name)
            .Select((g, i) => new WalkingGuildRankDto(i + 1, g.Id, g.Name, g.Tag, g.Rating, g.WarWins, GuildTiers.NameOf(GuildTiers.TierOf(g.Rating, g.WarWins)), g.Members))
            .ToList();

        int myRank = membership == null ? 0 : ranked.FirstOrDefault(r => r.GuildId == membership.GuildId)?.Rank ?? 0;

        return Results.Ok(new WalkingGuildRankingDto(ranked.Take(10).ToList(), membership?.GuildId, myRank));
    }
}
