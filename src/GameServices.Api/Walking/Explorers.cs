using System.Security.Claims;
using System.Text.RegularExpressions;
using GameServices.Api.Data;
using GameServices.Api.Moderation;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking;

/// <summary>One walker in the Players list: who they are, the title they wear, their companion (if they let people see
/// their Steplings) and what they've been up to - all made by the server, never typed by anyone.</summary>
public record ExplorerDto(Guid UserId, string Username, string Country, string Title, string Tier, int Points, bool IsFriend,
    bool WalkingNow, string Activity, PublicMonsterDto? Companion, int CompanionPower, string Frame = "");

public record ExplorerListDto(string View, string Note, List<ExplorerDto> Explorers);

/// <summary>
/// PLAYERS: other walkers to meet without having to walk into them.
///
///   GET /api/walking/explorers?view=active    walkers out and about lately (walking right now first)
///   GET /api/walking/explorers?view=close     walkers whose companion is a fair fight for yours (within 25% BST)
///   GET /api/walking/explorers?view=country   walkers who show the same country as you
///   GET /api/walking/explorers?view=friends   your friends
///
/// Each shows the walker's chosen TITLE (earned with medals in the game - the cosmetic part), their companion if they
/// let people see their Steplings, and one line about what they've been up to, written by the server: a battle they
/// won today, steps walked today (only if they share their steps), or just "Out exploring". Never where anyone is, and
/// never anyone blocked either way.
/// </summary>
public static partial class Explorers
{
    public const int Shown = 30;
    private static readonly TimeSpan Recent = TimeSpan.FromDays(3);
    private static readonly TimeSpan Now = TimeSpan.FromMinutes(15);

    [GeneratedRegex("^[A-Za-z' -]{0,40}$")]
    private static partial Regex TitleRule();

    public static RouteGroupBuilder MapExplorerEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/explorers", List);
        return group;
    }

    /// <summary>A title the game says its walker is wearing: words and spaces only, word-filtered (empty = none).</summary>
    public static string CleanTitle(string? title)
    {
        string t = (title ?? "").Trim();
        return TitleRule().IsMatch(t) && ContentFilter.IsClean(t) ? t : "";
    }

    private static async Task<IResult> List(string? view, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        DateTime now = DateTime.UtcNow;
        view = view is "close" or "country" or "friends" ? view : "active";

        List<Guid> blocked = await db.Blocks.AsNoTracking().Where(b => b.BlockerId == me || b.BlockedId == me)
            .Select(b => b.BlockerId == me ? b.BlockedId : b.BlockerId).ToListAsync(cancel);
        List<Guid> friends = await db.Friendships.AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Accepted && (f.UserAId == me || f.UserBId == me))
            .Select(f => f.UserAId == me ? f.UserBId : f.UserAId).ToListAsync(cancel);
        WalkingProfile mine = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);

        IQueryable<WalkingProfile> walkers = db.WalkingProfiles.AsNoTracking().Where(p => p.UserId != me && !blocked.Contains(p.UserId));
        string note = "";
        List<WalkingProfile> picked;
        switch (view)
        {
            case "friends":
                picked = await walkers.Where(p => friends.Contains(p.UserId)).OrderByDescending(p => p.LastReportUtc).Take(Shown).ToListAsync(cancel);
                if (picked.Count == 0) note = "No friends yet - add some from their profiles.";
                break;

            case "country":
                if (string.IsNullOrEmpty(mine.Country))
                {
                    picked = new List<WalkingProfile>();
                    note = "Choose the country you show (on the Ranked screen) to meet walkers from it.";
                    break;
                }
                picked = await walkers.Where(p => p.Country == mine.Country && p.LastReportUtc > now - Recent * 10)
                    .OrderByDescending(p => p.LastReportUtc).Take(Shown).ToListAsync(cancel);
                break;

            case "close":
                picked = await CloseFightsAsync(db, finder, mine, walkers, now, cancel);
                if (mine.CompanionMonsterId == null) note = "Choose a companion first - then we can find fair fights for it.";
                break;

            default:
                picked = await walkers.Where(p => p.LastReportUtc > now - Recent).OrderByDescending(p => p.LastReportUtc).Take(Shown).ToListAsync(cancel);
                break;
        }

        if (picked.Count == 0 && note == "") note = "Nobody here just now - try another list.";
        return Results.Ok(new ExplorerListDto(view, note, await ToDtosAsync(db, finder, me, picked, friends, now, cancel)));
    }

    // Walkers whose companion's BST is within 25% of mine, closest first.
    private static async Task<List<WalkingProfile>> CloseFightsAsync(AppDbContext db, MonsterFinder finder, WalkingProfile mine,
        IQueryable<WalkingProfile> walkers, DateTime now, CancellationToken cancel)
    {
        WalkingMonster? myCompanion = mine.CompanionMonsterId is Guid id ? await db.WalkingMonsters.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancel) : null;
        if (myCompanion == null) return new List<WalkingProfile>();
        int myBst = RankedEndpoints.StatsOf(finder.Catalog, myCompanion).Total;

        List<WalkingProfile> recent = await walkers.Where(p => p.CompanionMonsterId != null && p.LastReportUtc > now - Recent * 5)
            .OrderByDescending(p => p.LastReportUtc).Take(300).ToListAsync(cancel);
        List<Guid> ids = recent.Select(p => p.CompanionMonsterId!.Value).ToList();
        Dictionary<Guid, WalkingMonster> companions = await db.WalkingMonsters.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancel);

        return recent
            .Where(p => companions.ContainsKey(p.CompanionMonsterId!.Value))
            .Select(p => (Profile: p, Gap: Math.Abs(RankedEndpoints.StatsOf(finder.Catalog, companions[p.CompanionMonsterId!.Value]).Total - myBst)))
            .Where(x => x.Gap <= myBst * ChallengeEndpoints.RankedWindow)
            .OrderBy(x => x.Gap).Take(Shown).Select(x => x.Profile).ToList();
    }

    private static async Task<List<ExplorerDto>> ToDtosAsync(AppDbContext db, MonsterFinder finder, Guid me, List<WalkingProfile> picked,
        List<Guid> friends, DateTime now, CancellationToken cancel)
    {
        if (picked.Count == 0) return new List<ExplorerDto>();
        List<Guid> ids = picked.Select(p => p.UserId).ToList();
        Dictionary<Guid, string> names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancel);

        List<Guid> companionIds = picked.Where(p => p.CompanionMonsterId != null).Select(p => p.CompanionMonsterId!.Value).ToList();
        Dictionary<Guid, WalkingMonster> companions = await db.WalkingMonsters.AsNoTracking().Where(m => companionIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancel);

        string season = RankedPoints.SeasonOf(now);
        Dictionary<Guid, RankedSeasonRecord> seasons = await db.RankedSeasons.AsNoTracking()
            .Where(r => r.Season == season && ids.Contains(r.UserId)).ToDictionaryAsync(r => r.UserId, cancel);

        // What they've been up to: a challenge won in the last day, then today's steps.
        DateTime dayAgo = now.AddDays(-1);
        List<Challenge> wins = await db.Challenges.AsNoTracking()
            .Where(c => c.Status == ChallengeStatus.Fought && c.DecidedUtc > dayAgo && (ids.Contains(c.FromUserId) || ids.Contains(c.ToUserId)))
            .OrderByDescending(c => c.DecidedUtc).Take(200).ToListAsync(cancel);
        List<Guid> rivals = wins.SelectMany(c => new[] { c.FromUserId, c.ToUserId }).Distinct().ToList();
        Dictionary<Guid, string> rivalNames = await db.Users.AsNoTracking().Where(u => rivals.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancel);
        List<WalkingDay> days = await db.WalkingDays.AsNoTracking().Where(d => ids.Contains(d.UserId) && d.UpdatedUtc > dayAgo).ToListAsync(cancel);

        var result = new List<ExplorerDto>();
        foreach (WalkingProfile p in picked)
        {
            PublicMonsterDto? companion = null;
            int power = 0;
            if (p.CompanionMonsterId is Guid cid && companions.TryGetValue(cid, out WalkingMonster? m) && await PublicProfiles.CanSeeMonsters(db, me, p.UserId, cancel))
            {
                MonsterBattleStats stats = RankedEndpoints.StatsOf(finder.Catalog, m);
                companion = PublicProfiles.ToPublic(m, stats.Total);
                power = PowerBattles.PowerOf(stats);
            }

            Challenge? won = wins.FirstOrDefault(c => (c.FromUserId == p.UserId && c.FromWon) || (c.ToUserId == p.UserId && !c.FromWon));
            int stepsToday = days.Where(d => d.UserId == p.UserId).OrderByDescending(d => d.UpdatedUtc).Select(d => d.Steps).FirstOrDefault();
            string activity = won != null
                ? "Won a battle against " + rivalNames.GetValueOrDefault(won.FromUserId == p.UserId ? won.ToUserId : won.FromUserId, "a walker") + "!"
                : p.ShareSteps && stepsToday >= 500 ? "Walked " + stepsToday.ToString("N0") + " steps today"
                : "Out exploring";

            seasons.TryGetValue(p.UserId, out RankedSeasonRecord? record);
            RankedTier tier = RankedPoints.TierFor(record?.Points ?? 0);
            result.Add(new ExplorerDto(p.UserId, names.GetValueOrDefault(p.UserId, "A walker"), p.Country ?? "", p.Title ?? "", tier.ToString(),
                record?.Points ?? 0, friends.Contains(p.UserId), p.LastReportUtc > now - Now, activity, companion, power, p.Frame ?? ""));
        }

        return result;
    }
}
