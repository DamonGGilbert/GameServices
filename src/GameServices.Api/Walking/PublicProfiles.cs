using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking;

/// <summary>Who can see a walker's Steplings on their public profile.</summary>
public enum MonsterVisibility { Everyone = 0, Friends = 1, Nobody = 2 }

public record PublicMonsterDto(Guid Id, string SpeciesId, long Seed, int RarityTier, int Level,
    int TrainedPower, int TrainedGuard, int TrainedTricks, int TrainedWits, int Bst, int RankedWins, int RankedLosses, string Nickname,
    int AbsorbedPower = 0, int AbsorbedGuard = 0, int AbsorbedTricks = 0, int AbsorbedWits = 0, int CrystalStepsLeft = 0, string CardStyle = "");

public record PublicProfileDto(Guid UserId, string Username, string Country, bool IsMe, bool IsFriend,
    string Tier, int Points, int Wins, int Losses, int HighValueWins, int WorldPlace,
    string MonsterVisibility, bool MonstersHidden, int MonsterCount, List<PublicMonsterDto> Monsters);

public record PlayerSearchDto(Guid UserId, string Username, string Country);

public record PrivacyDto(string MonsterVisibility);

public record MonsterPrivacyRequest(string MonsterVisibility);

/// <summary>
/// PUBLIC PROFILES: every walker has one, so anyone can look anyone up and challenge them.
///
///   GET /api/walking/players?search=ell          walkers whose names start with "ell" (up to 20)
///   GET /api/walking/players/{name or id}        their profile: country, this season's ranked record - and their
///                                                Steplings, if they let you see them
///   GET /api/walking/monster-privacy | PUT { monsterVisibility }   who can see MY Steplings: Everyone (the default),
///                                                          Friends, or Nobody
///
/// Hidden Steplings are hidden completely (not even how many). Blocked walkers can't find each other at all. Nothing
/// here says where anyone is. You can still challenge a walker whose Steplings are hidden - you just can't pick out
/// one of their monsters (see ChallengeEndpoints).
/// </summary>
public static class PublicProfiles
{
    public const int MonstersShown = 60;

    public static RouteGroupBuilder MapPublicProfileEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/players", Search);
        group.MapGet("/players/{who}", Profile);
        group.MapGet("/monster-privacy", GetPrivacy);
        group.MapPut("/monster-privacy", SetPrivacy);
        return group;
    }

    /// <summary>A monster as anyone may see it (its recipe, progress, BST and record).</summary>
    public static PublicMonsterDto ToPublic(WalkingMonster m, int bst) =>
        new(m.Id, m.SpeciesId, m.Seed, m.RarityTier, m.Level, m.TrainedPower, m.TrainedGuard, m.TrainedTricks, m.TrainedWits, bst,
            m.RankedWins, m.RankedLosses, m.Nickname ?? "", m.AbsorbedPower, m.AbsorbedGuard, m.AbsorbedTricks, m.AbsorbedWits, m.CrystalStepsLeft, m.CardStyle ?? "");

    /// <summary>Whether viewer may see owner's Steplings.</summary>
    public static async Task<bool> CanSeeMonsters(AppDbContext db, Guid viewer, Guid owner, CancellationToken cancel)
    {
        if (viewer == owner) return true;
        MonsterVisibility visibility = (MonsterVisibility)(await db.WalkingProfiles.AsNoTracking()
            .Where(p => p.UserId == owner).Select(p => (int?)p.MonsterVisibility).FirstOrDefaultAsync(cancel) ?? 0);
        return visibility == MonsterVisibility.Everyone ||
               (visibility == MonsterVisibility.Friends && await SocialEndpoints.AreFriends(db, viewer, owner, cancel));
    }

    private static async Task<IResult> Search(string? search, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        string prefix = (search ?? "").Trim().ToUpperInvariant();
        if (prefix.Length < 2) return Results.Ok(new List<PlayerSearchDto>());

        List<Guid> blocked = await db.Blocks.AsNoTracking().Where(b => b.BlockerId == me || b.BlockedId == me)
            .Select(b => b.BlockerId == me ? b.BlockedId : b.BlockerId).ToListAsync(cancel);
        var found = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedUsername.StartsWith(prefix) && u.Id != me && !blocked.Contains(u.Id))
            .OrderBy(u => u.NormalizedUsername)
            .Take(20)
            .Select(u => new { u.Id, u.Username })
            .ToListAsync(cancel);
        List<Guid> ids = found.Select(f => f.Id).ToList();
        Dictionary<Guid, string> countries = await db.WalkingProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.Country, cancel);
        return Results.Ok(found.Select(f => new PlayerSearchDto(f.Id, f.Username, countries.GetValueOrDefault(f.Id, "") ?? "")).ToList());
    }

    private static async Task<IResult> Profile(string who, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        string normalised = (who ?? "").Trim().ToUpperInvariant();
        User? user = Guid.TryParse(who, out Guid id)
            ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancel)
            : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.NormalizedUsername == normalised, cancel);
        if (user == null || await SocialEndpoints.IsBlockedEitherWay(db, me, user.Id, cancel))
        {
            return Results.Problem("There's nobody with that name.", statusCode: StatusCodes.Status404NotFound);
        }

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, user.Id, cancel);
        string season = RankedPoints.SeasonOf(DateTime.UtcNow);
        RankedSeasonRecord record = await RankedSeasons.GetOrCreateAsync(db, user.Id, season, cancel);
        await db.SaveChangesAsync(cancel);
        int place = record.Wins + record.Losses > 0 ? await RankedSeasons.WorldPlaceAsync(db, record, cancel) : 0;
        RankedTier tier = RankedPoints.TierFor(record.Points, place > 0 ? place : int.MaxValue);

        bool isMe = user.Id == me;
        bool isFriend = !isMe && await SocialEndpoints.AreFriends(db, me, user.Id, cancel);
        bool canSee = await CanSeeMonsters(db, me, user.Id, cancel);

        var monsters = new List<PublicMonsterDto>();
        int count = 0;
        if (canSee)
        {
            List<WalkingMonster> owned = await db.WalkingMonsters.AsNoTracking().Where(m => m.OwnerId == user.Id).ToListAsync(cancel);
            count = owned.Count;
            monsters = owned
                .Select(m => (Monster: m, Bst: RankedEndpoints.StatsOf(finder.Catalog, m).Total))
                .OrderByDescending(x => x.Bst).ThenByDescending(x => x.Monster.RankedWins)
                .Take(MonstersShown)
                .Select(x => ToPublic(x.Monster, x.Bst))
                .ToList();
        }

        return Results.Ok(new PublicProfileDto(user.Id, user.Username, profile.Country ?? "", isMe, isFriend,
            tier.ToString(), record.Points, record.Wins, record.Losses, record.HighValueWins, place,
            ((MonsterVisibility)profile.MonsterVisibility).ToString(), !canSee, count, monsters));
    }

    private static async Task<IResult> GetPrivacy(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(new PrivacyDto(((MonsterVisibility)profile.MonsterVisibility).ToString()));
    }

    private static async Task<IResult> SetPrivacy(MonsterPrivacyRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!Enum.TryParse(request?.MonsterVisibility, true, out MonsterVisibility visibility) || !Enum.IsDefined(visibility))
        {
            return Results.Problem("Choose Everyone, Friends or Nobody.", statusCode: StatusCodes.Status400BadRequest);
        }

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        profile.MonsterVisibility = (int)visibility;
        await db.SaveChangesAsync(cancel);
        return Results.Ok(new PrivacyDto(visibility.ToString()));
    }
}
