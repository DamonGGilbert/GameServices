using System.Security.Claims;
using System.Text.Json;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Progress;
using GameServices.Api.Walking.Ranked;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Battles;

public record BattleStartRequest(string OpponentId, List<Guid> MonsterIds, int TeamIndex = -1, string? BiomeId = null);

/// <summary>A computer's monster: the recipe the phone builds its card from, and the stats it fights with.</summary>
public record BattleMonsterDto(string SpeciesId, long Seed, int RarityTier, int Level, int[] Stats);

public record TrainerTeamDto(List<BattleMonsterDto> Team);

/// <summary>What you're up against. NOT the seed: that decides the fight, so the phone only gets it afterwards to replay it.</summary>
public record BattleDto(Guid Id, string OpponentId, List<BattleMonsterDto> Opponents, int[] MyStats, int[] OpponentStats,
    bool MyArena, bool OpponentArena, int Tonics, bool HasSignature, int MaxStake);

public record BattleFightRequest(string Strategy, bool UseTonic = false, int Stake = 0, bool Signature = false);

/// <summary>Everything the phone needs to replay the fight (seed, stats, strategies) and to show what it earned.</summary>
public record BattleResultDto(Guid Id, bool Won, string Strategy, string OpponentStrategy, long Seed, float Chance, float Roll,
    int[] MineBefore, int[] TheirsBefore, bool MyArena, bool OpponentArena, bool Tonic, bool Signature,
    int GlimmerChange, int Glimmer, int PlayerXp, int MonsterXp, int PowerChange, int CrystalSteps, int StepsToWake, int Tonics,
    bool TrainerReward, List<WalkingMonsterDto> Monsters, string Message);

/// <summary>
/// BATTLES AGAINST WILD MONSTERS AND TRAINERS, run by the server. The phone only asks and then shows what happened:
///
///   GET  /api/walking/battles/trainers/{id}?monsterId=   a trainer's team, as it would face this monster
///   POST /api/walking/battles                            choose an opponent: the server picks it and fixes the seed
///   POST /api/walking/battles/{id}/fight                 lock in a strategy: the server decides it and pays out
///
/// The server keeps the Glimmer, the experience, the Trail Tonics, the crystals and the snoozes, and the fight is stored
/// with its seed so it can be replayed and checked at any time (Wallet/WalletAudit). What it pays:
///   * Glimmer for taking part, up to MaxGlimmerPerDay a day. A stake pays out only while that lasts.
///   * Experience for the monster(s), within MonsterLevels' daily limit.
///   * Wild: a win takes a small bite of power (3 a day) and a loss is a crystal. Trainers: a loss is a snooze.
///
/// Not signed in? The phone's own offline practice battles are all you get, and they earn nothing here.
/// </summary>
public static class BattleEndpoints
{
    public const string Wild = "wild";
    public const int MaxGlimmerPerDay = 300;
    public const int MaxStake = 200;
    public const int SnoozeSteps = 500;
    public const long BestFriendsSteps = 20000;

    /// <summary>A bite counts a wild monster's power up to this much above your monster's (now and then a rare one turns up).</summary>
    public const double MostAboveMine = 1.25;

    private const int TeamSize = 3;
    private static readonly TimeSpan BattleLifetime = TimeSpan.FromMinutes(15);

    public static RouteGroupBuilder MapBattleEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/battles/trainers/{trainerId}", TrainerTeam);
        group.MapPost("/battles", Start);
        group.MapPost("/battles/{battleId:guid}/fight", Fight);
        return group;
    }

    private static IResult Problem(string message, int status = StatusCodes.Status400BadRequest) => Results.Problem(message, statusCode: status);

    private static int[] Stats(DuelStats s) => new[] { s.Power, s.Guard, s.Trickery, s.Wits };

    // ------------------------------------------------------------------ the opponents

    // A trainer's team is always the same monsters, at a level matched to yours. A wildcard (no team) has three wild ones
    // that change every day.
    private static List<BattleMonsterDto> TeamOf(MonsterCatalog catalog, MonsterCatalog.TrainerEntry trainer, int myLevel, DateTime now)
    {
        var species = new List<string>(trainer.Team);
        if (species.Count == 0)
        {
            List<MonsterCatalog.SpeciesEntry> wild = catalog.Species.Where(s => s.Wild).ToList();
            var pick = new DeterministicRandom(SeedUtility.HashString("trainer-team/" + trainer.Id + "/" + now.ToString("yyyy-MM-dd")));
            for (int i = 0; i < TeamSize && wild.Count > 0; i++) species.Add(wild[pick.NextInt(0, wild.Count)].Id);
        }

        long trainerSeed = SeedUtility.HashString("trainer/" + trainer.Id);
        int level = Math.Max(1, myLevel + trainer.LevelOffset);
        var team = new List<BattleMonsterDto>();
        for (int i = 0; i < species.Count; i++)
        {
            MonsterCatalog.SpeciesEntry? entry = catalog.FindSpecies(species[i]);
            if (entry == null) continue;
            long seed = SeedUtility.Derive(trainerSeed, "member" + i);
            team.Add(new BattleMonsterDto(entry.Id, seed, entry.MinTier, level, Stats(DuelStats.From(MonsterStats.For(catalog, entry.Id, seed, entry.MinTier, level, 0, 0, 0, 0)))));
        }
        return team;
    }

    private static BattleMonsterDto WildOne(MonsterFinder finder, int myLevel, string? biome)
    {
        (string species, int tier, long seed, int _) = finder.Roll(biome);
        int level = Math.Max(1, myLevel + Random.Shared.Next(-1, 2));
        return new BattleMonsterDto(species, seed, tier, level, Stats(DuelStats.From(MonsterStats.For(finder.Catalog, species, seed, tier, level, 0, 0, 0, 0))));
    }

    private static async Task<IResult> TrainerTeam(string trainerId, Guid monsterId, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        MonsterCatalog.TrainerEntry? trainer = finder.Catalog.Trainers.FirstOrDefault(t => t.Id == trainerId);
        if (trainer == null) return Results.NotFound();
        int? level = await db.WalkingMonsters.AsNoTracking().Where(m => m.Id == monsterId && m.OwnerId == me).Select(m => (int?)m.Level).FirstOrDefaultAsync(cancel);
        if (level == null) return Results.NotFound();
        return Results.Ok(new TrainerTeamDto(TeamOf(finder.Catalog, trainer, level.Value, DateTime.UtcNow)));
    }

    private static bool AtHome(MonsterCatalog catalog, string? biomeId, Clan clan)
    {
        MonsterCatalog.BiomeEntry? biome = catalog.Biomes.FirstOrDefault(b => b.Id == biomeId);
        return biome != null && biome.Elements.GetValueOrDefault(clan.ToString()) > 1;
    }

    // Why a monster can't battle right now - null if it can.
    private static string? WhyNot(WalkingMonster m, WalkingProfile profile)
    {
        string name = string.IsNullOrEmpty(m.Nickname) ? "That monster" : m.Nickname;
        if (PowerBattles.IsCrystal(m)) return name + " is a crystal. Walk " + m.CrystalStepsLeft.ToString("N0") + " more steps to bring its power back!";
        long wake = m.RestUntilSteps - profile.LifetimeSteps;
        return wake > 0 ? name + " is having a snooze. Walk " + wake.ToString("N0") + " more steps to wake them up!" : null;
    }

    // ------------------------------------------------------------------ choosing an opponent

    private static async Task<IResult> Start(BattleStartRequest request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        MonsterCatalog catalog = finder.Catalog;
        bool wild = request?.OpponentId == Wild;
        MonsterCatalog.TrainerEntry? trainer = wild ? null : catalog.Trainers.FirstOrDefault(t => t.Id == request?.OpponentId);
        if (!wild && trainer == null) return Problem("Unknown opponent.");

        List<Guid> ids = (request!.MonsterIds ?? new List<Guid>()).Distinct().ToList();
        bool team = ids.Count == TeamSize && trainer != null;
        if (ids.Count != 1 && !team) return Problem("Pick one monster, or three for a team battle against a trainer.");

        List<WalkingMonster> rows = await db.WalkingMonsters.Where(m => m.OwnerId == me && ids.Contains(m.Id)).ToListAsync(cancel);
        if (rows.Count != ids.Count) return Problem("Pick your own monsters.");
        List<WalkingMonster> mine = ids.ConvertAll(id => rows.First(m => m.Id == id));

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        foreach (WalkingMonster m in mine)
        {
            string? why = WhyNot(m, profile);
            if (why != null) return Problem(why);
        }

        // One battle at a time: one that was never fought ends here.
        foreach (WalkingBattle old in await db.WalkingBattles.Where(b => b.UserId == me && b.Status == WalkingBattleStatus.Open).ToListAsync(cancel))
        {
            old.Status = WalkingBattleStatus.Expired;
        }

        DateTime now = DateTime.UtcNow;
        string? biome = string.IsNullOrWhiteSpace(request.BiomeId) ? null : request.BiomeId.Trim().ToLowerInvariant();
        var opponents = new List<BattleMonsterDto>();
        if (wild)
        {
            opponents.Add(WildOne(finder, mine[0].Level, biome));
        }
        else if (team)
        {
            // Each of theirs is matched to the one of yours it lines up against.
            for (int i = 0; i < TeamSize; i++)
            {
                List<BattleMonsterDto> against = TeamOf(catalog, trainer!, mine[i].Level, now);
                if (against.Count < TeamSize) return Problem("That trainer hasn't got a full team today.");
                opponents.Add(against[i]);
            }
        }
        else
        {
            List<BattleMonsterDto> against = TeamOf(catalog, trainer!, mine[0].Level, now);
            if (against.Count == 0) return Problem("That trainer hasn't got a team today.");
            opponents.Add(against[request.TeamIndex >= 0 && request.TeamIndex < against.Count ? request.TeamIndex : Random.Shared.Next(against.Count)]);
        }

        // The clans: each monster against the one lined up against it, then every stat added together. For a team, the order
        // you pick them in matters.
        List<Clan> myClans = mine.ConvertAll(m => Clans.Of(catalog, m.SpeciesId, m.Seed));
        List<Clan> theirClans = opponents.ConvertAll(o => Clans.Of(catalog, o.SpeciesId, o.Seed));
        DuelStats myStats = Sum(mine.Select((m, i) => Clans.InBattle(DuelStats.From(RankedEndpoints.StatsOf(catalog, m)), myClans[i], theirClans[i])));
        DuelStats theirStats = Sum(opponents.Select((o, i) => Clans.InBattle(new DuelStats(o.Stats[0], o.Stats[1], o.Stats[2], o.Stats[3]), theirClans[i], myClans[i])));

        // The arena counts for a monster at home here (for a team, if most of it is).
        int home = myClans.Count(c => AtHome(catalog, biome, c));
        int theirHome = theirClans.Count(c => AtHome(catalog, biome, c));

        var battle = new WalkingBattle
        {
            UserId = me,
            OpponentId = wild ? Wild : trainer!.Id,
            MonsterIds = string.Join(",", ids),
            OpponentsJson = JsonSerializer.Serialize(opponents),
            Style = (int)(wild ? DuelStyle.Random : Enum.TryParse(trainer!.Style, true, out DuelStyle style) ? style : DuelStyle.Balanced),
            Seed = RandomSeed(),
            MyPower = myStats.Power, MyGuard = myStats.Guard, MyTricks = myStats.Trickery, MyWits = myStats.Wits,
            TheirPower = theirStats.Power, TheirGuard = theirStats.Guard, TheirTricks = theirStats.Trickery, TheirWits = theirStats.Wits,
            MyArena = team ? home >= 2 : home > 0,
            TheirArena = team ? theirHome >= 2 : theirHome > 0,
            CreatedUtc = now,
            ExpiresUtc = now + BattleLifetime,
        };
        db.WalkingBattles.Add(battle);
        RollDay(profile, now);
        await db.SaveChangesAsync(cancel);

        return Results.Ok(new BattleDto(battle.Id, battle.OpponentId, opponents, Stats(myStats), Stats(theirStats), battle.MyArena, battle.TheirArena,
            profile.Tonics, HasSignature(mine[0]), StakeLimit(profile)));
    }

    private static DuelStats Sum(IEnumerable<DuelStats> all)
    {
        int p = 0, g = 0, t = 0, w = 0;
        foreach (DuelStats s in all)
        {
            p += s.Power;
            g += s.Guard;
            t += s.Trickery;
            w += s.Wits;
        }
        return new DuelStats(p, g, t, w);
    }

    private static long RandomSeed() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue) * 4294967296L + (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

    private static bool HasSignature(WalkingMonster first) => first.BondSteps >= BestFriendsSteps;

    // Small and optional: at most a fifth of your Glimmer, never more than 200 - and never more than today's battle
    // Glimmer has left to pay out, so a win always pays what it said it would.
    private static int StakeLimit(WalkingProfile profile) =>
        Math.Min(Math.Min(MaxStake, profile.Glimmer / 5), Math.Max(0, MaxGlimmerPerDay - profile.BattleGlimmerToday));

    private static void RollDay(WalkingProfile profile, DateTime now)
    {
        string today = now.ToString("yyyy-MM-dd");
        if (profile.BattleDay == today) return;
        profile.BattleDay = today;
        profile.BattleGlimmerToday = 0;
    }

    // ------------------------------------------------------------------ the fight

    public static DuelOutcome Decide(WalkingBattle b, DuelStrategy strategy, out DuelStrategy theirStrategy)
    {
        var mine = new DuelStats(b.MyPower, b.MyGuard, b.MyTricks, b.MyWits);
        var theirs = new DuelStats(b.TheirPower, b.TheirGuard, b.TheirTricks, b.TheirWits);

        // The opponent chooses only now, from the fight's own seed - so nobody can peek, and it can always be checked.
        theirStrategy = DuelRules.PickForComputer((DuelStyle)b.Style, theirs, mine, new DeterministicRandom(SeedUtility.Derive(b.Seed, "ai")));
        return DuelRules.Resolve(b.Seed, mine, strategy, b.Tonic, 0, b.MyArena, theirs, theirStrategy, false, 0, b.TheirArena, b.Signature);
    }

    private static async Task<IResult> Fight(Guid battleId, BattleFightRequest request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        if (request == null || !Enum.TryParse(request.Strategy, true, out DuelStrategy strategy) || !Enum.IsDefined(strategy)) return Problem("Pick a strategy.");

        WalkingBattle? battle = await db.WalkingBattles.FirstOrDefaultAsync(b => b.Id == battleId && b.UserId == me, cancel);
        if (battle == null) return Results.NotFound();

        DateTime now = DateTime.UtcNow;
        if (battle.Status == WalkingBattleStatus.Open && now > battle.ExpiresUtc)
        {
            battle.Status = WalkingBattleStatus.Expired;
            await db.SaveChangesAsync(cancel);
        }
        if (battle.Status != WalkingBattleStatus.Open) return Problem("That battle has already finished - start another.", StatusCodes.Status409Conflict);

        List<Guid> ids = battle.MonsterIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();
        List<WalkingMonster> rows = await db.WalkingMonsters.Where(m => m.OwnerId == me && ids.Contains(m.Id)).ToListAsync(cancel);
        if (rows.Count != ids.Count) return Problem("Your monster isn't here any more.");
        List<WalkingMonster> mine = ids.ConvertAll(id => rows.First(m => m.Id == id));

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        RollDay(profile, now);
        foreach (WalkingMonster m in mine)
        {
            string? why = WhyNot(m, profile);
            if (why != null) return Problem(why);
        }

        if (request.UseTonic && profile.Tonics <= 0) return Problem("You haven't got a Trail Tonic. Walk 5,000 steps today to earn one!");
        if (request.Signature && !HasSignature(mine[0])) return Problem("Only a monster that's Best friends with you knows a signature move.");

        int stake = request.Stake;
        if (stake < 0 || stake > StakeLimit(profile)) return Problem("You can put in up to " + StakeLimit(profile) + " Glimmer.");
        if (request.UseTonic) profile.Tonics--;
        int glimmerBefore = profile.Glimmer;
        if (stake > 0) WalkingWallet.TrySpend(db, profile, stake, "battle-stake", "Put on a battle");

        battle.Tonic = request.UseTonic;
        battle.Signature = request.Signature;
        battle.Stake = stake;
        battle.Strategy = (int)strategy;
        DuelOutcome outcome = Decide(battle, strategy, out DuelStrategy theirStrategy);
        battle.TheirStrategy = (int)theirStrategy;
        battle.Won = outcome.PlayerWon;
        battle.Chance = outcome.Player.Chance;
        battle.Roll = outcome.Roll;
        battle.Status = WalkingBattleStatus.Fought;
        battle.FoughtUtc = now;

        Settlement result = Settle(db, finder.Catalog, battle, mine, profile, stake, now);
        await Explorer.OnBattleAsync(db, profile, battle.Won, now, cancel);
        if (battle.Won && battle.OpponentId != Wild)
        {
            MonsterCatalog.TrainerEntry? trainer = finder.Catalog.Trainers.FirstOrDefault(t => t.Id == battle.OpponentId);
            if (trainer != null) await Explorer.GrantAsync(db, profile, finder, trainer.WinReward, "trainer", "Beat " + trainer.Id, Explorer.Today(profile, now), now, new RewardTally(), cancel);
        }

        battle.GlimmerChange = profile.Glimmer - glimmerBefore;
        battle.PowerChange = result.PowerChange;
        await db.SaveChangesAsync(cancel);

        List<WalkingMonsterDto> monsters = mine.ConvertAll(m => MonsterEndpoints.ToDto(m, null, profile.LifetimeSteps));
        return Results.Ok(new BattleResultDto(battle.Id, battle.Won, strategy.ToString(), theirStrategy.ToString(), battle.Seed, outcome.Player.Chance, outcome.Roll,
            new[] { battle.MyPower, battle.MyGuard, battle.MyTricks, battle.MyWits }, new[] { battle.TheirPower, battle.TheirGuard, battle.TheirTricks, battle.TheirWits },
            battle.MyArena, battle.TheirArena, battle.Tonic, battle.Signature, battle.GlimmerChange, profile.Glimmer,
            battle.Won ? finder.Catalog.Battle.PlayerXpPerWin : finder.Catalog.Battle.PlayerXpPerLoss, result.MonsterXp, result.PowerChange, result.CrystalSteps, result.StepsToWake,
            profile.Tonics, battle.Won && battle.OpponentId != Wild, monsters, result.Message));
    }

    private readonly record struct Settlement(int MonsterXp, int PowerChange, int CrystalSteps, int StepsToWake, string Message);

    // The end of a battle: Glimmer, growth for the monster(s), and a bite, a crystal or a snooze.
    private static Settlement Settle(AppDbContext db, MonsterCatalog catalog, WalkingBattle battle, List<WalkingMonster> mine, WalkingProfile profile, int stake, DateTime now)
    {
        MonsterCatalog.BattleEntry rules = catalog.Battle;
        string today = now.ToString("yyyy-MM-dd");
        bool team = mine.Count > 1;
        bool wild = battle.OpponentId == Wild;

        // The winner gets their stake back, the computer's stake and the battle's reward - as far as today's limit goes.
        int prize = battle.Won ? stake + rules.GlimmerPerWin * (team ? 2 : 1) : rules.GlimmerPerLoss;
        int paid = Math.Min(prize, Math.Max(0, MaxGlimmerPerDay - profile.BattleGlimmerToday));
        profile.BattleGlimmerToday += paid;
        if (battle.Won && stake > 0) WalkingWallet.Change(db, profile, stake, "battle-stake", "Stake back from a battle");
        if (paid > 0) WalkingWallet.Change(db, profile, paid, "battle", battle.Won ? "Won a battle" : "Played a battle");

        // Every battle makes your monsters a little stronger - win or lose.
        int xp = battle.Won ? rules.MonsterXpPerBattle : Math.Max(1, rules.MonsterXpPerBattle / 3);
        int gained = 0;
        foreach (WalkingMonster m in mine) gained = Math.Max(gained, MonsterLevels.Grant(m, xp, today));

        int powerChange = 0, crystalSteps = 0, stepsToWake = 0;
        string message = battle.Won ? "Victory!" : "Good try!";
        if (wild)
        {
            WalkingMonster monster = mine[0];
            MonsterBattleStats stats = RankedEndpoints.StatsOf(catalog, monster);
            if (profile.WildDay != today)
            {
                profile.WildDay = today;
                profile.WildBitesToday = 0;
            }

            if (!battle.Won)
            {
                powerChange = -PowerBattles.Share(PowerBattles.PowerOf(stats), PowerBattles.DefeatShare);
                crystalSteps = PowerBattles.DefeatSteps;
                PowerBattles.Crystallise(monster, -powerChange, crystalSteps);
                message = "It turned to crystal. Walk " + crystalSteps.ToString("N0") + " steps to bring its power back.";
            }
            else if (profile.WildBitesToday < PowerBattles.WildBitesPerDay)
            {
                int theirPower = battle.TheirPower + battle.TheirGuard + battle.TheirTricks + battle.TheirWits;
                int counted = Math.Clamp(theirPower, 1, (int)Math.Ceiling(PowerBattles.PowerOf(stats) * MostAboveMine));
                powerChange = PowerBattles.Share(counted, PowerBattles.WildBiteShare);
                PowerBattles.Absorb(monster, stats, powerChange);
                profile.WildBitesToday++;
                message = "A bite: +" + powerChange + " power, for good!";
            }
            else
            {
                message = "That's today's wild bites - wins still count.";
            }
        }
        else if (!battle.Won)
        {
            // A trainer never takes anything: your monster just needs a snooze, and wakes after a short walk.
            stepsToWake = SnoozeSteps;
            foreach (WalkingMonster m in mine) m.RestUntilSteps = profile.LifetimeSteps + SnoozeSteps;
        }

        return new Settlement(gained, powerChange, crystalSteps, stepsToWake, message);
    }

    // ------------------------------------------------------------------ what walking earns

    /// <summary>
    /// Steps the server has accepted: they earn your companion experience (and it keeps up its daily limit like any other),
    /// grow your bond and fill today's training bar, and a Trail Tonic for every 5,000 steps today, up to two a day.
    /// </summary>
    public static async Task OnStepsAsync(AppDbContext db, MonsterCatalog catalog, WalkingProfile profile, int addedSteps, int acceptedToday, bool isToday, DateTime now, List<string> notices, CancellationToken cancel)
    {
        if (addedSteps <= 0) return;

        if (isToday)
        {
            int before = acceptedToday - addedSteps;
            profile.Tonics += Math.Max(0, Math.Min(2, acceptedToday / 5000) - Math.Min(2, before / 5000));
        }

        if (profile.CompanionMonsterId is not Guid companion) return;
        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == companion && m.OwnerId == profile.UserId, cancel);
        if (monster == null) return;

        int steps = profile.CompanionStepRemainder + addedSteps;
        int perXp = Math.Max(1, catalog.Battle.CompanionStepsPerXp);
        profile.CompanionStepRemainder = steps % perXp;
        MonsterLevels.Grant(monster, steps / perXp, now.ToString("yyyy-MM-dd"));
        Companions.OnSteps(db, profile, monster, addedSteps, Explorer.Today(profile, now), notices);
    }
}
