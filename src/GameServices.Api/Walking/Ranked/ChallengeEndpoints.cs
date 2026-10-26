using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Ranked;

public record ChallengeSendRequest(string? ToUsername, Guid? ToUserId, Guid MonsterId, int Stake, string Strategy);

public record ChallengeAcceptRequest(Guid MonsterId, string Strategy);

public record CountryRequest(string Country);

/// <summary>A monster in a challenge: its recipe (so the phone can draw the card) and its stats as the server worked them out.</summary>
public record ChallengeMonsterDto(Guid Id, string SpeciesId, long Seed, int RarityTier, int Level,
    int TrainedPower, int TrainedGuard, int TrainedTricks, int TrainedWits, int Bst, int[] Stats);

/// <summary>How it went: enough to replay the fight with the game's own duel rules (challenger as the first side).</summary>
public record ChallengeFightDto(long Seed, string FromStrategy, string ToStrategy, float Chance, float Roll, bool FromWon);

public record ChallengeDto(Guid Id, bool Incoming, Guid OtherUserId, string OtherName, string OtherCountry, int Stake, string Status,
    DateTime CreatedUtc, DateTime ExpiresUtc, ChallengeMonsterDto FromMonster, ChallengeMonsterDto? ToMonster,
    bool Ranked, bool IWon, int MyPointsChange, int GlimmerChange, ChallengeFightDto? Fight);

public record ChallengeListDto(List<ChallengeDto> Incoming, List<ChallengeDto> Outgoing, List<ChallengeDto> Recent, string MyCountry);

/// <summary>
/// CHALLENGES: fight any walker in the world, for an optional Glimmer stake.
///
///   GET  /api/walking/challenges                  waiting for me, sent by me, and recent results
///   POST /api/walking/challenges                  challenge someone (my monster, a stake, my secret strategy)
///   POST /api/walking/challenges/{id}/accept      pick my monster and strategy - the server decides it there and then
///   POST /api/walking/challenges/{id}/decline | cancel
///   PUT  /api/walking/country                     the country I show (two letters, or empty)
///
/// Rules (see docs/RANKED_DESIGN.md in the game):
///   * Glimmer only - never real money - and never monsters or training. Both stakes are held by the server; the
///     winner takes both. Declined, cancelled or expired (after 2 days): the challenger's stake comes back.
///   * The fight uses the ranked rules (no tonic, walking bonus or arena). If the two monsters are within 25% BST of
///     each other it's RANKED - points and monster records move as in a ranked fight - otherwise it's a friendly.
///   * Not yourself, never anyone blocked either way; up to 5 sent and 20 waiting at once; stakes up to 1,000.
/// </summary>
public static class ChallengeEndpoints
{
    public const int MaxStake = 1000;
    public const int MaxOpenSent = 5;
    public const int MaxWaitingFor = 20;
    public const double RankedWindow = 0.25;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(2);

    private static readonly Regex CountryCode = new("^[A-Z]{2}$");

    public static RouteGroupBuilder MapChallengeEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/challenges", List);
        group.MapPost("/challenges", Send);
        group.MapPost("/challenges/{challengeId:guid}/accept", Accept);
        group.MapPost("/challenges/{challengeId:guid}/decline", (Guid challengeId, ClaimsPrincipal p, AppDbContext db, MonsterFinder f, CancellationToken c) => Close(challengeId, ChallengeStatus.Declined, p, db, f, c));
        group.MapPost("/challenges/{challengeId:guid}/cancel", (Guid challengeId, ClaimsPrincipal p, AppDbContext db, MonsterFinder f, CancellationToken c) => Close(challengeId, ChallengeStatus.Cancelled, p, db, f, c));
        group.MapPut("/country", SetCountry);
        return group;
    }

    private static IResult Problem(string message, int status = StatusCodes.Status400BadRequest) => Results.Problem(message, statusCode: status);

    // ------------------------------------------------------------------ list

    private static async Task<IResult> List(ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        await ExpireAsync(db, me, cancel);
        await db.SaveChangesAsync(cancel);

        List<Challenge> mine = await db.Challenges.AsNoTracking()
            .Where(c => (c.FromUserId == me || c.ToUserId == me) &&
                        (c.Status == ChallengeStatus.Pending || c.Status == ChallengeStatus.Fought))
            .OrderByDescending(c => c.DecidedUtc ?? c.CreatedUtc)
            .Take(60)
            .ToListAsync(cancel);

        Dictionary<Guid, (string Name, string Country)> people = await PeopleAsync(db, mine.SelectMany(c => new[] { c.FromUserId, c.ToUserId }), cancel);
        Dictionary<Guid, WalkingMonster> monsters = await MonstersAsync(db, mine.Where(c => c.Status == ChallengeStatus.Pending).Select(c => c.FromMonsterId), cancel);
        string myCountry = await db.WalkingProfiles.AsNoTracking().Where(p => p.UserId == me).Select(p => p.Country).FirstOrDefaultAsync(cancel) ?? "";

        List<ChallengeDto> all = mine.Select(c => ToDto(c, me, people, monsters, finder.Catalog)).Where(d => d != null).Select(d => d!).ToList();
        return Results.Ok(new ChallengeListDto(
            all.Where(d => d.Incoming && d.Status == nameof(ChallengeStatus.Pending)).ToList(),
            all.Where(d => !d.Incoming && d.Status == nameof(ChallengeStatus.Pending)).ToList(),
            all.Where(d => d.Status == nameof(ChallengeStatus.Fought)).Take(20).ToList(),
            myCountry));
    }

    // ------------------------------------------------------------------ send

    private static async Task<IResult> Send(ChallengeSendRequest request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!Enum.TryParse(request?.Strategy, true, out DuelStrategy strategy) || !Enum.IsDefined(strategy)) return Problem("Pick a strategy.");
        if (request!.Stake < 0 || request.Stake > MaxStake) return Problem($"Stakes are from 0 to {MaxStake:N0} Glimmer.");

        User? to = request.ToUserId is Guid id
            ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancel)
            : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == (request.ToUsername ?? "").Trim(), cancel);
        if (to == null) return Problem("There's nobody with that name.", StatusCodes.Status404NotFound);
        if (to.Id == me) return Problem("You can't challenge yourself!");
        if (await SocialEndpoints.IsBlockedEitherWay(db, me, to.Id, cancel)) return Problem("You can't challenge this walker.", StatusCodes.Status403Forbidden);

        WalkingMonster? monster = await db.WalkingMonsters.AsNoTracking().FirstOrDefaultAsync(m => m.Id == request.MonsterId && m.OwnerId == me, cancel);
        if (monster == null) return Problem("Pick one of your own monsters.");

        await ExpireAsync(db, me, cancel);
        if (await db.Challenges.CountAsync(c => c.FromUserId == me && c.Status == ChallengeStatus.Pending, cancel) >= MaxOpenSent)
        {
            return Problem($"You already have {MaxOpenSent} challenges waiting for an answer.", StatusCodes.Status429TooManyRequests);
        }
        if (await db.Challenges.CountAsync(c => c.ToUserId == to.Id && c.Status == ChallengeStatus.Pending, cancel) >= MaxWaitingFor)
        {
            return Problem("They have lots of challenges waiting already - try again later.", StatusCodes.Status429TooManyRequests);
        }
        if (await db.Challenges.AnyAsync(c => c.FromUserId == me && c.ToUserId == to.Id && c.Status == ChallengeStatus.Pending, cancel))
        {
            return Problem("You've already challenged them - wait for their answer.", StatusCodes.Status409Conflict);
        }

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        if (request.Stake > 0 && !WalkingWallet.TrySpend(db, profile, request.Stake, "challenge", "Stake held for a challenge to " + to.Username))
        {
            return Problem("You don't have that much Glimmer.");
        }

        DateTime now = DateTime.UtcNow;
        var challenge = new Challenge
        {
            FromUserId = me, ToUserId = to.Id, FromMonsterId = monster.Id, FromStrategy = (int)strategy, Stake = request.Stake,
            CreatedUtc = now, ExpiresUtc = now + Lifetime,
        };
        db.Challenges.Add(challenge);
        await db.SaveChangesAsync(cancel);

        Dictionary<Guid, (string, string)> people = await PeopleAsync(db, new[] { me, to.Id }, cancel);
        return Results.Ok(ToDto(challenge, me, people, new Dictionary<Guid, WalkingMonster> { [monster.Id] = monster }, finder.Catalog));
    }

    // ------------------------------------------------------------------ accept: the fight

    private static async Task<IResult> Accept(Guid challengeId, ChallengeAcceptRequest request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!Enum.TryParse(request?.Strategy, true, out DuelStrategy strategy) || !Enum.IsDefined(strategy)) return Problem("Pick a strategy.");

        await ExpireAsync(db, me, cancel);
        Challenge? c = await db.Challenges.FirstOrDefaultAsync(x => x.Id == challengeId && x.ToUserId == me, cancel);
        if (c == null) return Results.NotFound();
        if (c.Status != ChallengeStatus.Pending)
        {
            await db.SaveChangesAsync(cancel);
            return Problem("This challenge has already finished (or expired).", StatusCodes.Status409Conflict);
        }

        WalkingMonster? mine = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == request!.MonsterId && m.OwnerId == me, cancel);
        if (mine == null) return Problem("Pick one of your own monsters.");
        WalkingMonster? theirs = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == c.FromMonsterId, cancel);
        if (theirs == null || theirs.OwnerId != c.FromUserId)
        {
            // Their monster found a new home meanwhile: the challenge can't go ahead.
            await RefundAsync(db, c, ChallengeStatus.Cancelled, cancel);
            await db.SaveChangesAsync(cancel);
            return Problem("Their monster isn't with them any more - the challenge was called off.", StatusCodes.Status409Conflict);
        }

        WalkingProfile myProfile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        if (c.Stake > 0 && !WalkingWallet.TrySpend(db, myProfile, c.Stake, "challenge", "Stake for a challenge"))
        {
            return Problem($"You need {c.Stake:N0} Glimmer to match their stake.");
        }

        DateTime now = DateTime.UtcNow;
        MonsterCatalog catalog = finder.Catalog;
        MonsterBattleStats fromStats = RankedEndpoints.StatsOf(catalog, theirs);
        MonsterBattleStats toStats = RankedEndpoints.StatsOf(catalog, mine);
        Snapshot(c, theirs, fromStats, mine, toStats);

        c.ToMonsterId = mine.Id;
        c.ToStrategy = (int)strategy;
        c.Seed = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue) * 4294967296L + (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

        DuelOutcome outcome = DuelRules.ResolveRanked(c.Seed, DuelStats.From(fromStats), (DuelStrategy)c.FromStrategy, DuelStats.From(toStats), strategy);
        c.FromWon = outcome.PlayerWon;
        c.Chance = outcome.Player.Chance;
        c.Roll = outcome.Roll;
        c.Status = ChallengeStatus.Fought;
        c.DecidedUtc = now;

        // The winner takes both stakes.
        WalkingProfile fromProfile = await WalkingEndpoints.GetOrCreateProfile(db, c.FromUserId, cancel);
        if (c.Stake > 0)
        {
            WalkingWallet.Change(db, c.FromWon ? fromProfile : myProfile, c.Stake * 2, "challenge", "Won a challenge");
        }

        // Close enough in BST? Then it counts as ranked: points and monster records move.
        int bigger = Math.Max(fromStats.Total, toStats.Total);
        c.Ranked = Math.Abs(fromStats.Total - toStats.Total) <= bigger * RankedWindow;
        if (c.Ranked)
        {
            string season = RankedPoints.SeasonOf(now);
            (WalkingMonster winner, MonsterBattleStats winnerStats, Guid winnerUser) = c.FromWon ? (theirs, fromStats, c.FromUserId) : (mine, toStats, me);
            (WalkingMonster loser, MonsterBattleStats loserStats, Guid loserUser) = c.FromWon ? (mine, toStats, me) : (theirs, fromStats, c.FromUserId);

            int won = RankedPoints.WinPoints(winnerStats.Total, loserStats.Total, loser.RarityTier, false);
            int lost = RankedPoints.LossPoints(loserStats.Total, winnerStats.Total, false);
            bool highValue = RankedPoints.IsHighValue(winnerStats.Total, loserStats.Total, loser.RarityTier);

            RankedSeasonRecord winnerSeason = await RankedSeasons.GetOrCreateAsync(db, winnerUser, season, cancel);
            winnerSeason.Points += won;
            winnerSeason.Wins++;
            if (highValue) winnerSeason.HighValueWins++;
            winnerSeason.UpdatedUtc = now;

            RankedSeasonRecord loserSeason = await RankedSeasons.GetOrCreateAsync(db, loserUser, season, cancel);
            loserSeason.Points = Math.Max(0, loserSeason.Points - lost);
            loserSeason.Losses++;
            loserSeason.UpdatedUtc = now;

            winner.RankedWins++;
            loser.RankedLosses++;
            c.FromPointsChange = c.FromWon ? won : -lost;
            c.ToPointsChange = c.FromWon ? -lost : won;
        }

        await db.SaveChangesAsync(cancel);

        Dictionary<Guid, (string, string)> people = await PeopleAsync(db, new[] { c.FromUserId, me }, cancel);
        return Results.Ok(ToDto(c, me, people, new Dictionary<Guid, WalkingMonster>(), catalog));
    }

    private static void Snapshot(Challenge c, WalkingMonster from, MonsterBattleStats fs, WalkingMonster to, MonsterBattleStats ts)
    {
        c.FromSpeciesId = from.SpeciesId; c.FromSeed = from.Seed; c.FromTier = from.RarityTier; c.FromLevel = from.Level;
        c.FromTrainedPower = from.TrainedPower; c.FromTrainedGuard = from.TrainedGuard; c.FromTrainedTricks = from.TrainedTricks; c.FromTrainedWits = from.TrainedWits;
        c.FromPower = fs.Power; c.FromGuard = fs.Guard; c.FromTricks = fs.Tricks; c.FromWits = fs.Wits; c.FromBst = fs.Total;
        c.ToSpeciesId = to.SpeciesId; c.ToSeed = to.Seed; c.ToTier = to.RarityTier; c.ToLevel = to.Level;
        c.ToTrainedPower = to.TrainedPower; c.ToTrainedGuard = to.TrainedGuard; c.ToTrainedTricks = to.TrainedTricks; c.ToTrainedWits = to.TrainedWits;
        c.ToPower = ts.Power; c.ToGuard = ts.Guard; c.ToTricks = ts.Tricks; c.ToWits = ts.Wits; c.ToBst = ts.Total;
    }

    // ------------------------------------------------------------------ decline, cancel, expire

    private static async Task<IResult> Close(Guid challengeId, ChallengeStatus how, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        Challenge? c = await db.Challenges.FirstOrDefaultAsync(x => x.Id == challengeId &&
            (how == ChallengeStatus.Declined ? x.ToUserId == me : x.FromUserId == me), cancel);
        if (c == null) return Results.NotFound();
        if (c.Status != ChallengeStatus.Pending) return Problem("This challenge has already finished.", StatusCodes.Status409Conflict);

        await RefundAsync(db, c, how, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static async Task RefundAsync(AppDbContext db, Challenge c, ChallengeStatus how, CancellationToken cancel)
    {
        c.Status = how;
        c.DecidedUtc = DateTime.UtcNow;
        if (c.Stake <= 0) return;
        WalkingProfile from = await WalkingEndpoints.GetOrCreateProfile(db, c.FromUserId, cancel);
        WalkingWallet.Change(db, from, c.Stake, "challenge", "Challenge stake returned");
    }

    /// <summary>Unanswered challenges involving this walker that ran out of time: closed, stakes returned.</summary>
    private static async Task ExpireAsync(AppDbContext db, Guid me, CancellationToken cancel)
    {
        DateTime now = DateTime.UtcNow;
        List<Challenge> late = await db.Challenges
            .Where(c => (c.FromUserId == me || c.ToUserId == me) && c.Status == ChallengeStatus.Pending && c.ExpiresUtc < now)
            .ToListAsync(cancel);
        foreach (Challenge c in late) await RefundAsync(db, c, ChallengeStatus.Expired, cancel);
    }

    // ------------------------------------------------------------------ country

    private static async Task<IResult> SetCountry(CountryRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        string code = (request?.Country ?? "").Trim().ToUpperInvariant();
        if (code.Length > 0 && !CountryCode.IsMatch(code)) return Problem("A country is two letters, like GB, ES or BR.");

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        profile.Country = code;
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    // ------------------------------------------------------------------ helpers

    public static async Task<Dictionary<Guid, (string Name, string Country)>> PeopleAsync(AppDbContext db, IEnumerable<Guid> ids, CancellationToken cancel)
    {
        List<Guid> list = ids.Distinct().ToList();
        Dictionary<Guid, string> names = await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancel);
        Dictionary<Guid, string> countries = await db.WalkingProfiles.AsNoTracking().Where(p => list.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.Country, cancel);
        return list.ToDictionary(id => id, id => (names.GetValueOrDefault(id, "A walker"), countries.GetValueOrDefault(id, "") ?? ""));
    }

    private static async Task<Dictionary<Guid, WalkingMonster>> MonstersAsync(AppDbContext db, IEnumerable<Guid> ids, CancellationToken cancel)
    {
        List<Guid> list = ids.Distinct().ToList();
        return await db.WalkingMonsters.AsNoTracking().Where(m => list.Contains(m.Id)).ToDictionaryAsync(m => m.Id, cancel);
    }

    private static ChallengeDto? ToDto(Challenge c, Guid me, Dictionary<Guid, (string Name, string Country)> people,
        Dictionary<Guid, WalkingMonster> monsters, MonsterCatalog catalog)
    {
        bool incoming = c.ToUserId == me;
        Guid other = incoming ? c.FromUserId : c.ToUserId;
        (string otherName, string otherCountry) = people.GetValueOrDefault(other, ("A walker", ""));

        ChallengeMonsterDto from;
        ChallengeMonsterDto? to = null;
        ChallengeFightDto? fight = null;
        bool iWon = false;
        int myPoints = 0, glimmer = 0;

        if (c.Status == ChallengeStatus.Fought)
        {
            from = new ChallengeMonsterDto(c.FromMonsterId, c.FromSpeciesId, c.FromSeed, c.FromTier, c.FromLevel, c.FromTrainedPower, c.FromTrainedGuard,
                c.FromTrainedTricks, c.FromTrainedWits, c.FromBst, new[] { c.FromPower, c.FromGuard, c.FromTricks, c.FromWits });
            to = new ChallengeMonsterDto(c.ToMonsterId ?? Guid.Empty, c.ToSpeciesId, c.ToSeed, c.ToTier, c.ToLevel, c.ToTrainedPower, c.ToTrainedGuard,
                c.ToTrainedTricks, c.ToTrainedWits, c.ToBst, new[] { c.ToPower, c.ToGuard, c.ToTricks, c.ToWits });
            fight = new ChallengeFightDto(c.Seed, ((DuelStrategy)c.FromStrategy).ToString(), ((DuelStrategy)c.ToStrategy).ToString(), c.Chance, c.Roll, c.FromWon);
            iWon = incoming ? !c.FromWon : c.FromWon;
            myPoints = incoming ? c.ToPointsChange : c.FromPointsChange;
            glimmer = c.Stake == 0 ? 0 : iWon ? c.Stake : -c.Stake;
        }
        else
        {
            if (!monsters.TryGetValue(c.FromMonsterId, out WalkingMonster? m)) return null;
            MonsterBattleStats s = RankedEndpoints.StatsOf(catalog, m);
            from = new ChallengeMonsterDto(m.Id, m.SpeciesId, m.Seed, m.RarityTier, m.Level, m.TrainedPower, m.TrainedGuard, m.TrainedTricks, m.TrainedWits,
                s.Total, new[] { s.Power, s.Guard, s.Tricks, s.Wits });
        }

        return new ChallengeDto(c.Id, incoming, other, otherName, otherCountry, c.Stake, c.Status.ToString(), c.CreatedUtc, c.ExpiresUtc,
            from, to, c.Ranked, iWon, myPoints, glimmer, fight);
    }
}
