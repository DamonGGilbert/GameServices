using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Ranked;

public record PlayerBoardRowDto(int Place, Guid UserId, string Username, int Points, string Tier, int Wins, int Losses, int HighValueWins, bool IsMe, string Country = "");

public record PlayerBoardDto(string Season, string SeasonName, DateTime SeasonEndsUtc, string Scope, List<PlayerBoardRowDto> Rows, PlayerBoardRowDto? Me);

/// <summary>A monster on the board, with its recipe so the phone can draw its card.</summary>
public record MonsterBoardRowDto(int Place, Guid MonsterId, Guid OwnerId, string OwnerName, string SpeciesId, long Seed, int RarityTier, int Level,
    int TrainedPower, int TrainedGuard, int TrainedTricks, int TrainedWits, int Bst, int Wins, int Losses, int Score, bool IsMine, string OwnerCountry = "");

public record SeasonRewardDto(string Season, string SeasonName, string Tier, int Points, int Place, int Glimmer, string Title, bool Claimed, bool CanClaim);

public record SeasonDto(string Season, string SeasonName, DateTime SeasonEndsUtc, int Points, string Tier, int Place, SeasonRewardDto? LastSeason);

/// <summary>
/// RANKED LEADERBOARDS AND SEASONS:
///
///   GET  /api/walking/ranked/leaderboard/players?scope=world|friends   this season: points, tier, record, high-value wins
///   GET  /api/walking/ranked/leaderboard/monsters?species=MON_0001     score = BST + 100 per ranked win (all time)
///   GET  /api/walking/ranked/season                                    my season, and last season's reward
///   POST /api/walking/ranked/season/claim                              collect last season's reward (once)
///
/// Players are ordered by points, then high-value wins, then wins. Monsters by score; a monster shows on the board once
/// it has fought in ranked. Blocked walkers are left out of each other's boards.
/// </summary>
public static class RankedLeaderboards
{
    public const int BoardSize = 100;

    public static RouteGroupBuilder MapRankedLeaderboardEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/ranked/leaderboard/players", Players);
        group.MapGet("/ranked/leaderboard/monsters", MonsterBoard);
        group.MapGet("/ranked/season", Season);
        group.MapPost("/ranked/season/claim", Claim);
        return group;
    }

    private static async Task<List<Guid>> BlockedAsync(AppDbContext db, Guid me, CancellationToken cancel) =>
        await db.Blocks.AsNoTracking().Where(b => b.BlockerId == me || b.BlockedId == me)
            .Select(b => b.BlockerId == me ? b.BlockedId : b.BlockerId).ToListAsync(cancel);

    private static async Task<IResult> Players(string? scope, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        bool friendsOnly = string.Equals(scope, "friends", StringComparison.OrdinalIgnoreCase);
        string season = RankedPoints.SeasonOf(DateTime.UtcNow);

        RankedSeasonRecord mine = await RankedSeasons.GetOrCreateAsync(db, me, season, cancel);
        await db.SaveChangesAsync(cancel);

        IQueryable<RankedSeasonRecord> query = db.RankedSeasons.AsNoTracking().Where(r => r.Season == season && (r.Wins + r.Losses > 0 || r.UserId == me));
        if (friendsOnly)
        {
            List<Guid> friends = await db.Friendships.AsNoTracking()
                .Where(f => (f.UserAId == me || f.UserBId == me) && f.Status == FriendshipStatus.Accepted)
                .Select(f => f.UserAId == me ? f.UserBId : f.UserAId).ToListAsync(cancel);
            friends.Add(me);
            query = query.Where(r => friends.Contains(r.UserId));
        }
        else
        {
            List<Guid> blocked = await BlockedAsync(db, me, cancel);
            query = query.Where(r => !blocked.Contains(r.UserId));
        }

        List<RankedSeasonRecord> top = await RankedSeasons.Ordered(query).Take(BoardSize).ToListAsync(cancel);
        List<Guid> ids = top.Select(r => r.UserId).Append(me).Distinct().ToList();
        Dictionary<Guid, string> names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancel);
        Dictionary<Guid, string> countries = await db.WalkingProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.Country, cancel);

        // Places on a friends board are among friends; tiers always use the WORLD place (Legend is a world top 100).
        var rows = new List<PlayerBoardRowDto>();
        for (int i = 0; i < top.Count; i++)
        {
            RankedSeasonRecord r = top[i];
            int worldPlace = friendsOnly ? await RankedSeasons.WorldPlaceAsync(db, r, cancel) : i + 1;
            rows.Add(Row(i + 1, r, names, RankedPoints.TierFor(r.Points, worldPlace), me) with { Country = countries.GetValueOrDefault(r.UserId, "") ?? "" });
        }

        PlayerBoardRowDto? meRow = rows.FirstOrDefault(r => r.IsMe);
        if (meRow == null)
        {
            int place = await RankedSeasons.WorldPlaceAsync(db, mine, cancel);
            meRow = Row(place, mine, names, RankedPoints.TierFor(mine.Points, place), me) with { Country = countries.GetValueOrDefault(me, "") ?? "" };
        }

        return Results.Ok(new PlayerBoardDto(season, RankedPoints.SeasonName(season), RankedPoints.SeasonEndsUtc(season), friendsOnly ? "friends" : "world", rows, meRow));
    }

    private static PlayerBoardRowDto Row(int place, RankedSeasonRecord r, Dictionary<Guid, string> names, RankedTier tier, Guid me) =>
        new(place, r.UserId, names.GetValueOrDefault(r.UserId, "A walker"), r.Points, tier.ToString(), r.Wins, r.Losses, r.HighValueWins, r.UserId == me);

    private static async Task<IResult> MonsterBoard(string? species, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        List<Guid> blocked = await BlockedAsync(db, me, cancel);

        IQueryable<WalkingMonster> query = db.WalkingMonsters.AsNoTracking()
            .Where(m => m.RankedWins + m.RankedLosses > 0 && !blocked.Contains(m.OwnerId));
        if (!string.IsNullOrWhiteSpace(species)) query = query.Where(m => m.SpeciesId == species);

        // BST depends on level and training, so the score is worked out here for every monster that has fought.
        List<WalkingMonster> fought = await query.ToListAsync(cancel);
        var scored = fought
            .Select(m => (Monster: m, Bst: RankedEndpoints.StatsOf(finder.Catalog, m).Total))
            .Select(x => (x.Monster, x.Bst, Score: RankedPoints.MonsterScore(x.Bst, x.Monster.RankedWins)))
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Monster.RankedWins).ThenBy(x => x.Monster.FoundUtc)
            .Take(BoardSize)
            .ToList();

        List<Guid> owners = scored.Select(x => x.Monster.OwnerId).Distinct().ToList();
        Dictionary<Guid, string> names = await db.Users.AsNoTracking().Where(u => owners.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancel);
        Dictionary<Guid, string> countries = await db.WalkingProfiles.AsNoTracking().Where(p => owners.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.Country, cancel);

        return Results.Ok(scored.Select((x, i) => new MonsterBoardRowDto(i + 1, x.Monster.Id, x.Monster.OwnerId, names.GetValueOrDefault(x.Monster.OwnerId, "A walker"),
            x.Monster.SpeciesId, x.Monster.Seed, x.Monster.RarityTier, x.Monster.Level, x.Monster.TrainedPower, x.Monster.TrainedGuard,
            x.Monster.TrainedTricks, x.Monster.TrainedWits, x.Bst, x.Monster.RankedWins, x.Monster.RankedLosses, x.Score, x.Monster.OwnerId == me,
            countries.GetValueOrDefault(x.Monster.OwnerId, "") ?? "")).ToList());
    }

    private static async Task<IResult> Season(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        string season = RankedPoints.SeasonOf(DateTime.UtcNow);
        RankedSeasonRecord record = await RankedSeasons.GetOrCreateAsync(db, me, season, cancel);
        await db.SaveChangesAsync(cancel);

        int place = await RankedSeasons.WorldPlaceAsync(db, record, cancel);
        return Results.Ok(new SeasonDto(season, RankedPoints.SeasonName(season), RankedPoints.SeasonEndsUtc(season), record.Points,
            RankedPoints.TierFor(record.Points, place).ToString(), place, await LastSeasonAsync(db, me, season, cancel)));
    }

    private static async Task<SeasonRewardDto?> LastSeasonAsync(AppDbContext db, Guid me, string season, CancellationToken cancel)
    {
        string previous = RankedPoints.PreviousSeason(season);
        RankedSeasonRecord? last = await db.RankedSeasons.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == me && r.Season == previous, cancel);
        if (last == null || last.Wins + last.Losses == 0) return null;

        int place = await RankedSeasons.WorldPlaceAsync(db, last, cancel);
        RankedTier tier = RankedPoints.TierFor(last.Points, place);
        (int glimmer, string title) = RankedPoints.SeasonReward(tier, previous);
        return new SeasonRewardDto(previous, RankedPoints.SeasonName(previous), tier.ToString(), last.Points, place, glimmer, title, last.RewardClaimed, !last.RewardClaimed);
    }

    private static async Task<IResult> Claim(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        string season = RankedPoints.SeasonOf(DateTime.UtcNow);
        SeasonRewardDto? reward = await LastSeasonAsync(db, me, season, cancel);
        if (reward == null) return Results.Problem("You didn't fight in ranked last season - there's nothing to collect yet.", statusCode: StatusCodes.Status404NotFound);
        if (reward.Claimed) return Results.Problem("Last season's reward has already been collected.", statusCode: StatusCodes.Status409Conflict);

        RankedSeasonRecord last = await db.RankedSeasons.FirstAsync(r => r.UserId == me && r.Season == reward.Season, cancel);
        last.RewardClaimed = true;
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        WalkingWallet.Change(db, profile, reward.Glimmer, "season", reward.SeasonName + " ranked season: " + reward.Tier);
        await db.SaveChangesAsync(cancel);

        return Results.Ok(reward with { Claimed = true, CanClaim = false });
    }
}
