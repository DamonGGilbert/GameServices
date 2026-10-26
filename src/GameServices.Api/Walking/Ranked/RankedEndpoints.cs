using System.Security.Claims;
using System.Security.Cryptography;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Ranked;

public record RankedEntryRequest(Guid MonsterId, string Style);

public record RankedEntriesRequest(List<RankedEntryRequest> Entries);

public record RankedEntryDto(Guid MonsterId, int Slot, string Style, int Bst, int Wins, int Losses);

public record RankedMeDto(List<RankedEntryDto> Entries, int Wins, int Losses, int RewardedWinsToday, int RewardedWinsPerDay, int WinGlimmer,
    string Season, string SeasonName, DateTime SeasonEndsUtc, int Points, string Tier, int Place, int SeasonWins, int SeasonLosses, int HighValueWins);

public record RankedMatchRequest(Guid MonsterId);

/// <summary>The opponent, as the phone needs it to draw the card (it builds the same stats from the same recipe).</summary>
public record RankedMatchDto(Guid Id, Guid MonsterId, int Bst, string OpponentName, bool Shadow, string SpeciesId, long Seed, int RarityTier,
    int Level, int TrainedPower, int TrainedGuard, int TrainedTricks, int TrainedWits, int OpponentBst, string OpponentStyle,
    int WindowPercent, DateTime ExpiresUtc, int[] MyStats, int[] OpponentStats);

public record RankedFightRequest(string Strategy);

/// <summary>Seed and both monsters' stats before strategies: the phone replays the fight with the game's own duel to show it.</summary>
public record RankedFightResultDto(Guid MatchId, bool Won, string Strategy, string OpponentStrategy,
    int[] Mine, int[] Theirs, int Score, int OpponentScore, float Chance, float Roll, int Reward, int RewardedWinsToday, string Message,
    long Seed, int[] MineBefore, int[] TheirsBefore, int Wins, int Losses,
    int PointsChange, int Points, string Tier, bool HighValue);

public record RankedHistoryDto(Guid MatchId, bool Attacking, string OtherName, bool Shadow, string MySpeciesId, string OtherSpeciesId, bool Won, DateTime FoughtUtc);

/// <summary>
/// RANKED FIGHTS (see docs/RANKED_DESIGN.md in the walking game). The spin duel, judged by the server:
///
///   GET  /api/walking/ranked                      my ranked team, record and today's rewarded wins
///   PUT  /api/walking/ranked/entries              enter up to 3 of my monsters, each with a defence style
///   POST /api/walking/ranked/match                "Fight": find an opponent for one of my entered monsters
///   POST /api/walking/ranked/matches/{id}/fight   pick a strategy - the server decides the fight
///   GET  /api/walking/ranked/history              my last fights, attacking and defending
///
/// Matchmaking compares BST (base stat total, worked out here - never taken from a phone): first within 10% of yours,
/// then 15%, 20%, 25%. Nobody in range? A SHADOW monster the server makes at your BST. The opponent's stats are fixed
/// when the match is made, so what you see is what you fight. Ranked has no walking bonus, tonic or arena boost.
/// Fights are unlimited; Glimmer for a win stops after RewardedWinsPerDay a day. Nobody ever loses a monster.
/// Every fight moves RANKED POINTS (RankedPoints: big, rare and upset wins are worth most), which decide tiers, the
/// monthly season and the leaderboards (RankedLeaderboards). Within the BST window, matchmaking prefers walkers with
/// similar points.
/// </summary>
public static class RankedEndpoints
{
    public const int MaxEntries = 3;
    public static readonly double[] Windows = { 0.10, 0.15, 0.20, 0.25 };
    public static readonly TimeSpan MatchLifetime = TimeSpan.FromMinutes(10);
    public const int WinGlimmer = 20;
    public const int ShadowWinGlimmer = 10;
    public const int RewardedWinsPerDay = 10;
    /// <summary>The last few opponents aren't picked again straight away.</summary>
    public const int RecentOpponents = 3;

    public static RouteGroupBuilder MapRankedEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/ranked", Me);
        group.MapPut("/ranked/entries", SetEntries);
        group.MapPost("/ranked/match", Match);
        group.MapPost("/ranked/matches/{matchId:guid}/fight", Fight);
        group.MapGet("/ranked/history", History);
        group.MapRankedLeaderboardEndpoints();
        return group;
    }

    public static MonsterBattleStats StatsOf(MonsterCatalog catalog, WalkingMonster m) =>
        MonsterStats.For(catalog, m.SpeciesId, m.Seed, m.RarityTier, m.Level, m.TrainedPower, m.TrainedGuard, m.TrainedTricks, m.TrainedWits);

    private static IResult Problem(string message, int status = StatusCodes.Status400BadRequest) => Results.Problem(message, statusCode: status);

    // ------------------------------------------------------------------ my team

    private static async Task<IResult> Me(ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        RollDay(profile);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await MeDto(db, finder.Catalog, me, profile, cancel));
    }

    private static async Task<RankedMeDto> MeDto(AppDbContext db, MonsterCatalog catalog, Guid me, WalkingProfile profile, CancellationToken cancel)
    {
        var rows = await (from e in db.RankedEntries.AsNoTracking()
                          join m in db.WalkingMonsters.AsNoTracking() on e.MonsterId equals m.Id
                          where e.UserId == me && m.OwnerId == me
                          orderby e.Slot
                          select new { e, m }).ToListAsync(cancel);

        List<RankedEntryDto> entries = rows.Select(r => new RankedEntryDto(r.m.Id, r.e.Slot, ((DuelStyle)r.e.DefenceStyle).ToString(),
            StatsOf(catalog, r.m).Total, r.e.Wins, r.e.Losses)).ToList();

        string season = RankedPoints.SeasonOf(DateTime.UtcNow);
        RankedSeasonRecord record = await RankedSeasons.GetOrCreateAsync(db, me, season, cancel);
        await db.SaveChangesAsync(cancel);
        int place = await RankedSeasons.WorldPlaceAsync(db, record, cancel);
        RankedTier tier = RankedPoints.TierFor(record.Points, place);

        return new RankedMeDto(entries, profile.RankedWins, profile.RankedLosses, profile.RankedRewardedWinsToday, RewardedWinsPerDay, WinGlimmer,
            season, RankedPoints.SeasonName(season), RankedPoints.SeasonEndsUtc(season), record.Points, tier.ToString(), place,
            record.Wins, record.Losses, record.HighValueWins);
    }

    private static async Task<IResult> SetEntries(RankedEntriesRequest request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        List<RankedEntryRequest> wanted = request?.Entries ?? new List<RankedEntryRequest>();

        if (wanted.Count > MaxEntries) return Problem($"Up to {MaxEntries} monsters can fight in ranked.");
        if (wanted.Select(w => w.MonsterId).Distinct().Count() != wanted.Count) return Problem("Each monster can only be entered once.");

        var styles = new List<DuelStyle>();
        foreach (RankedEntryRequest w in wanted)
        {
            if (!Enum.TryParse(w.Style, true, out DuelStyle style) || !Enum.IsDefined(style)) return Problem("Unknown fighting style: " + w.Style);
            styles.Add(style);
        }

        List<Guid> ids = wanted.Select(w => w.MonsterId).ToList();
        int mine = await db.WalkingMonsters.CountAsync(m => ids.Contains(m.Id) && m.OwnerId == me, cancel);
        if (mine != ids.Count) return Problem("You can only enter your own monsters.");

        db.RankedEntries.RemoveRange(await db.RankedEntries.Where(e => e.UserId == me).ToListAsync(cancel));
        for (int i = 0; i < wanted.Count; i++)
        {
            db.RankedEntries.Add(new RankedEntry { UserId = me, MonsterId = wanted[i].MonsterId, Slot = i, DefenceStyle = (int)styles[i] });
        }

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        RollDay(profile);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(await MeDto(db, finder.Catalog, me, profile, cancel));
    }

    // ------------------------------------------------------------------ finding an opponent

    private sealed record Candidate(RankedEntry Entry, WalkingMonster Monster, MonsterBattleStats Stats);

    private static async Task<IResult> Match(RankedMatchRequest request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        MonsterCatalog catalog = finder.Catalog;

        WalkingMonster? attacker = await db.WalkingMonsters.AsNoTracking().FirstOrDefaultAsync(m => m.Id == request.MonsterId && m.OwnerId == me, cancel);
        if (attacker == null || !await db.RankedEntries.AnyAsync(e => e.UserId == me && e.MonsterId == attacker.Id, cancel))
        {
            return Problem("Enter this monster into ranked first.");
        }

        DateTime now = DateTime.UtcNow;

        // One open fight at a time: an earlier one that was never fought simply lapses.
        foreach (RankedMatch old in await db.RankedMatches.Where(m => m.AttackerId == me && m.Status == RankedMatchStatus.Open).ToListAsync(cancel))
        {
            old.Status = RankedMatchStatus.Expired;
        }

        MonsterBattleStats mine = StatsOf(catalog, attacker);
        int myBst = mine.Total;

        // Everyone else's ranked monsters (not blocked either way, not someone just fought).
        List<Guid> blocked = await db.Blocks.AsNoTracking()
            .Where(b => b.BlockerId == me || b.BlockedId == me)
            .Select(b => b.BlockerId == me ? b.BlockedId : b.BlockerId)
            .ToListAsync(cancel);
        List<Guid> recent = await db.RankedMatches.AsNoTracking()
            .Where(m => m.AttackerId == me && m.Status == RankedMatchStatus.Fought && m.DefenderMonsterId != null)
            .OrderByDescending(m => m.FoughtUtc).Take(RecentOpponents)
            .Select(m => m.DefenderMonsterId!.Value)
            .ToListAsync(cancel);

        var rows = await (from e in db.RankedEntries.AsNoTracking()
                          join m in db.WalkingMonsters.AsNoTracking() on e.MonsterId equals m.Id
                          where e.UserId != me && m.OwnerId == e.UserId && !blocked.Contains(e.UserId) && !recent.Contains(m.Id)
                          select new { e, m }).ToListAsync(cancel);
        List<Candidate> candidates = rows.Select(r => new Candidate(r.e, r.m, StatsOf(catalog, r.m))).ToList();

        // Everyone's points this season (new to the season: a quarter of last season's), to pair similar walkers.
        string season = RankedPoints.SeasonOf(now);
        int myPoints = (await RankedSeasons.GetOrCreateAsync(db, me, season, cancel)).Points;
        List<Guid> owners = candidates.Select(c => c.Entry.UserId).Distinct().ToList();
        Dictionary<Guid, int> points = await db.RankedSeasons.AsNoTracking()
            .Where(r => r.Season == season && owners.Contains(r.UserId))
            .ToDictionaryAsync(r => r.UserId, r => r.Points, cancel);

        var match = new RankedMatch
        {
            AttackerId = me,
            AttackerMonsterId = attacker.Id,
            AttackerPower = mine.Power, AttackerGuard = mine.Guard, AttackerTricks = mine.Tricks, AttackerWits = mine.Wits, AttackerBst = myBst,
            AttackerTier = attacker.RarityTier,
            Season = season,
            Seed = NewSeed(),
            CreatedUtc = now,
            ExpiresUtc = now + MatchLifetime,
        };

        Candidate? found = null;
        foreach (double window in Windows)
        {
            List<Candidate> inRange = candidates.Where(c => Math.Abs(c.Stats.Total - myBst) <= myBst * window).ToList();
            if (inRange.Count == 0) continue;

            // The closest few in points (then BST), so it isn't always the same opponent.
            List<Candidate> closest = inRange
                .OrderBy(c => Math.Abs(points.GetValueOrDefault(c.Entry.UserId) - myPoints))
                .ThenBy(c => Math.Abs(c.Stats.Total - myBst))
                .Take(3).ToList();
            found = closest[RandomNumberGenerator.GetInt32(closest.Count)];
            match.WindowPercent = (int)Math.Round(window * 100);
            break;
        }

        string opponentName;
        if (found != null)
        {
            match.DefenderId = found.Entry.UserId;
            match.DefenderMonsterId = found.Monster.Id;
            Fill(match, found.Monster.SpeciesId, found.Monster.Seed, found.Monster.RarityTier, found.Monster.Level,
                found.Monster.TrainedPower, found.Monster.TrainedGuard, found.Monster.TrainedTricks, found.Monster.TrainedWits, found.Stats);
            match.DefenderStyle = found.Entry.DefenceStyle;
            opponentName = await db.Users.AsNoTracking().Where(u => u.Id == found.Entry.UserId).Select(u => u.Username).FirstOrDefaultAsync(cancel) ?? "A walker";
        }
        else
        {
            (string species, int tier, int level, long seed, MonsterBattleStats stats) = MakeShadow(catalog, myBst, attacker.RarityTier);
            match.Shadow = true;
            Fill(match, species, seed, tier, level, 0, 0, 0, 0, stats);
            match.DefenderStyle = RandomNumberGenerator.GetInt32(4);
            opponentName = "Shadow " + (catalog.FindSpecies(species)?.Name ?? "monster");
        }

        db.RankedMatches.Add(match);
        await db.SaveChangesAsync(cancel);

        return Results.Ok(new RankedMatchDto(match.Id, attacker.Id, myBst, opponentName, match.Shadow, match.DefenderSpeciesId, match.DefenderSeed,
            match.DefenderTier, match.DefenderLevel, match.DefenderTrainedPower, match.DefenderTrainedGuard, match.DefenderTrainedTricks,
            match.DefenderTrainedWits, match.DefenderBst, ((DuelStyle)match.DefenderStyle).ToString(), match.WindowPercent, match.ExpiresUtc,
            new[] { match.AttackerPower, match.AttackerGuard, match.AttackerTricks, match.AttackerWits },
            new[] { match.DefenderPower, match.DefenderGuard, match.DefenderTricks, match.DefenderWits }));
    }

    private static void Fill(RankedMatch match, string species, long seed, int tier, int level, int tp, int tg, int tt, int tw, MonsterBattleStats stats)
    {
        match.DefenderSpeciesId = species;
        match.DefenderSeed = seed;
        match.DefenderTier = tier;
        match.DefenderLevel = level;
        match.DefenderTrainedPower = tp;
        match.DefenderTrainedGuard = tg;
        match.DefenderTrainedTricks = tt;
        match.DefenderTrainedWits = tw;
        match.DefenderPower = stats.Power;
        match.DefenderGuard = stats.Guard;
        match.DefenderTricks = stats.Tricks;
        match.DefenderWits = stats.Wits;
        match.DefenderBst = stats.Total;
    }

    /// <summary>
    /// A shadow monster as close as possible to the target BST: a random wild species at the attacker's rarity (or the
    /// nearest rarity it can be), at whichever level lands nearest. Untrained, so its stats come only from its recipe.
    /// </summary>
    public static (string Species, int Tier, int Level, long Seed, MonsterBattleStats Stats) MakeShadow(MonsterCatalog catalog, int targetBst, int preferredTier)
    {
        List<MonsterCatalog.SpeciesEntry> wild = catalog.Species.Where(s => s.Wild).ToList();
        if (wild.Count == 0) wild = catalog.Species;

        (string, int, int, long, MonsterBattleStats)? best = null;
        int bestGap = int.MaxValue;

        // A few tries with different species and seeds; keep whichever comes closest.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            MonsterCatalog.SpeciesEntry species = wild[RandomNumberGenerator.GetInt32(wild.Count)];
            int tier = Math.Clamp(preferredTier, species.MinTier, species.MaxTier);
            long seed = NewSeed();

            for (int level = 1; level <= 100; level++)
            {
                MonsterBattleStats stats = MonsterStats.For(catalog, species.Id, seed, tier, level, 0, 0, 0, 0);
                int gap = Math.Abs(stats.Total - targetBst);
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = (species.Id, tier, level, seed, stats);
                }
                if (stats.Total > targetBst) break;   // only gets further from here
            }
        }

        return best!.Value;
    }

    private static long NewSeed() =>
        RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue) * 4294967296L + (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

    // ------------------------------------------------------------------ the fight

    private static async Task<IResult> Fight(Guid matchId, RankedFightRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (!Enum.TryParse(request?.Strategy, true, out DuelStrategy strategy) || !Enum.IsDefined(strategy)) return Problem("Pick a strategy.");

        RankedMatch? match = await db.RankedMatches.FirstOrDefaultAsync(m => m.Id == matchId && m.AttackerId == me, cancel);
        if (match == null) return Results.NotFound();

        DateTime now = DateTime.UtcNow;
        if (match.Status == RankedMatchStatus.Open && now > match.ExpiresUtc) match.Status = RankedMatchStatus.Expired;
        if (match.Status != RankedMatchStatus.Open)
        {
            await db.SaveChangesAsync(cancel);
            return Problem("This fight has already finished or timed out - tap Fight for a new opponent.", StatusCodes.Status409Conflict);
        }

        var attacker = new DuelStats(match.AttackerPower, match.AttackerGuard, match.AttackerTricks, match.AttackerWits);
        var defender = new DuelStats(match.DefenderPower, match.DefenderGuard, match.DefenderTricks, match.DefenderWits);

        // The defender's plan picks its strategy - from the fight's own seed, so it can always be checked.
        DuelStrategy defence = DuelRules.PickForComputer((DuelStyle)match.DefenderStyle, defender, attacker, new DeterministicRandom(SeedUtility.Derive(match.Seed, "defence")));
        DuelOutcome outcome = DuelRules.ResolveRanked(match.Seed, attacker, strategy, defender, defence);

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        RollDay(profile);

        match.Status = RankedMatchStatus.Fought;
        match.AttackerStrategy = (int)strategy;
        match.DefenderStrategy = (int)defence;
        match.AttackerWon = outcome.PlayerWon;
        match.Chance = outcome.Player.Chance;
        match.Roll = outcome.Roll;
        match.FoughtUtc = now;

        // Ranked Points, season records and each monster's own record.
        string season = RankedPoints.SeasonOf(now);
        match.Season = season;
        PointsChange change = RankedPoints.For(outcome.PlayerWon, match.AttackerBst, match.AttackerTier, match.DefenderBst, match.DefenderTier, match.Shadow);
        match.AttackerPointsChange = change.Attacker;
        match.DefenderPointsChange = change.Defender;
        match.HighValue = change.HighValue;

        RankedSeasonRecord mySeason = await RankedSeasons.GetOrCreateAsync(db, me, season, cancel);
        ApplySeason(mySeason, outcome.PlayerWon, change.Attacker, change.HighValue, now);
        if (match.DefenderId is Guid defenderUser)
        {
            RankedSeasonRecord theirSeason = await RankedSeasons.GetOrCreateAsync(db, defenderUser, season, cancel);
            ApplySeason(theirSeason, !outcome.PlayerWon, change.Defender, false, now);
        }

        WalkingMonster? myMonster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == match.AttackerMonsterId, cancel);
        WalkingMonster? theirMonster = match.DefenderMonsterId is Guid dmId ? await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == dmId, cancel) : null;
        if (myMonster != null) { if (outcome.PlayerWon) myMonster.RankedWins++; else myMonster.RankedLosses++; }
        if (theirMonster != null) { if (outcome.PlayerWon) theirMonster.RankedLosses++; else theirMonster.RankedWins++; }

        RankedEntry? myEntry = await db.RankedEntries.FirstOrDefaultAsync(e => e.UserId == me && e.MonsterId == match.AttackerMonsterId, cancel);
        RankedEntry? theirEntry = match.DefenderMonsterId is Guid dm ? await db.RankedEntries.FirstOrDefaultAsync(e => e.MonsterId == dm, cancel) : null;
        WalkingProfile? theirProfile = match.DefenderId is Guid du ? await WalkingEndpoints.GetOrCreateProfile(db, du, cancel) : null;

        string message;
        if (outcome.PlayerWon)
        {
            profile.RankedWins++;
            if (myEntry != null) myEntry.Wins++;
            if (theirEntry != null) theirEntry.Losses++;
            if (theirProfile != null) theirProfile.RankedLosses++;

            if (profile.RankedRewardedWinsToday < RewardedWinsPerDay)
            {
                match.Reward = match.Shadow ? ShadowWinGlimmer : WinGlimmer;
                profile.RankedRewardedWinsToday++;
                WalkingWallet.Change(db, profile, match.Reward, "ranked", "Ranked win");
                message = $"Victory! +{match.Reward} Glimmer.";
            }
            else
            {
                message = "Victory! (That's today's Glimmer from ranked - wins still count.)";
            }
        }
        else
        {
            profile.RankedLosses++;
            if (myEntry != null) myEntry.Losses++;
            if (theirEntry != null) theirEntry.Wins++;
            if (theirProfile != null) theirProfile.RankedWins++;
            message = "Defeated this time - your monster is fine. Try another fight!";
        }

        if (change.HighValue) message += " A high-value win!";
        await db.SaveChangesAsync(cancel);
        RankedTier tierNow = await RankedSeasons.TierAsync(db, mySeason, cancel);

        return Results.Ok(new RankedFightResultDto(match.Id, outcome.PlayerWon, strategy.ToString(), defence.ToString(),
            Stats(outcome.Player.After), Stats(outcome.Opponent.After), outcome.Player.Score, outcome.Opponent.Score,
            outcome.Player.Chance, outcome.Roll, match.Reward, profile.RankedRewardedWinsToday, message,
            match.Seed, Stats(attacker), Stats(defender), profile.RankedWins, profile.RankedLosses,
            change.Attacker, mySeason.Points, tierNow.ToString(), change.HighValue));
    }

    private static int[] Stats(DuelStats s) => new[] { s.Power, s.Guard, s.Trickery, s.Wits };

    private static void ApplySeason(RankedSeasonRecord record, bool won, int pointsChange, bool highValue, DateTime now)
    {
        record.Points = Math.Max(0, record.Points + pointsChange);
        if (won) record.Wins++; else record.Losses++;
        if (highValue) record.HighValueWins++;
        record.UpdatedUtc = now;
    }

    // ------------------------------------------------------------------ history

    private static async Task<IResult> History(ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        List<RankedMatch> matches = await db.RankedMatches.AsNoTracking()
            .Where(m => m.Status == RankedMatchStatus.Fought && (m.AttackerId == me || m.DefenderId == me))
            .OrderByDescending(m => m.FoughtUtc).Take(20)
            .ToListAsync(cancel);

        List<Guid> people = matches.SelectMany(m => new[] { m.AttackerId, m.DefenderId ?? Guid.Empty }).Distinct().ToList();
        Dictionary<Guid, string> names = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancel);
        List<Guid> monsterIds = matches.Select(m => m.AttackerMonsterId).Distinct().ToList();
        Dictionary<Guid, string> species = await db.WalkingMonsters.AsNoTracking().Where(m => monsterIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.SpeciesId, cancel);

        return Results.Ok(matches.Select(m =>
        {
            bool attacking = m.AttackerId == me;
            string other = attacking
                ? (m.Shadow ? "Shadow monster" : names.GetValueOrDefault(m.DefenderId ?? Guid.Empty, "A walker"))
                : names.GetValueOrDefault(m.AttackerId, "A walker");
            string attackerSpecies = species.GetValueOrDefault(m.AttackerMonsterId, "");
            return new RankedHistoryDto(m.Id, attacking, other, m.Shadow,
                attacking ? attackerSpecies : m.DefenderSpeciesId,
                attacking ? m.DefenderSpeciesId : attackerSpecies,
                attacking ? m.AttackerWon : !m.AttackerWon,
                m.FoughtUtc ?? m.CreatedUtc);
        }).ToList());
    }

    // ------------------------------------------------------------------ helpers

    private static void RollDay(WalkingProfile profile)
    {
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (profile.RankedDay == today) return;
        profile.RankedDay = today;
        profile.RankedRewardedWinsToday = 0;
    }
}
