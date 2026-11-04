using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Wallet;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Progress;

/// <param name="Biome">The kind of place the walker is in ("park"), so a Stepling can suit it. Never where they are.</param>
public record InvestigateRequest(string? Biome = null);

/// <param name="Kind">"monster", "fullteam", "treasure", "bonus", "fragments", "trainer" or "golden".</param>
/// <param name="MonsterId">A monster's kept here already, waiting to be befriended. Null otherwise.</param>
/// <param name="TrainerId">Who wants to battle. Null otherwise.</param>
public record InvestigateResultDto(string Kind, bool Golden, string Message, int Glimmer, int Xp, int Fragments, List<string> Extras, Guid? MonsterId, string? SpeciesId, string? TrainerId, ProgressDto Progress);

/// <param name="Points">0-2: how the treat and the pats went (the phone counts those; 2 is the most it can ever be worth).</param>
public record BefriendRequest(int Points);

/// <summary>
/// FINDS ON THE MAP: tap Investigate and the server decides what's there.
///
///   POST /api/walking/investigate                  what's in the next find?
///   POST /api/walking/investigate/{id}/befriend    how making friends with a Stepling went
///
/// Walking brings them: the steps in a day that earn each one are in DiscoveryEntry.Steps, up to seven, so most days bring
/// three to seven. The map is the phone's business (the pins, how close you need to be), so where anyone is never comes
/// here. Someone faking their GPS can still only get what their steps have earned, at the same odds as everyone else.
///
/// Usually it's treasure, a walking bonus, fragments or a trainer; now and then a Stepling, and never too long between
/// them. The seventh of the day is a Golden Cache. The odds go to the game with the progress, so players can see them.
/// </summary>
public static class Discoveries
{
    public const string Source = "find";

    public static RouteGroupBuilder MapDiscoveryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/investigate", Investigate);
        group.MapPost("/investigate/{monsterId:guid}/befriend", Befriend);
        return group;
    }

    public static float BoostOf(WalkingProfile profile, DateTime now) => profile.BoostUntilUtc > now ? Math.Max(1f, profile.BoostMultiplier) : 1f;

    /// <summary>The chance in 100 of a Stepling, with any discovery boost.</summary>
    public static int MonsterChance(MonsterCatalog.DiscoveryEntry rules, float boost) => (int)Math.Min(100f, rules.MonsterPercent * Math.Max(1f, boost));

    /// <summary>What's in a find. <paramref name="next"/> gives a whole number from 0 up to (not including) what it's asked for.</summary>
    public static string Roll(MonsterCatalog.DiscoveryEntry rules, int findsSinceMonster, float boost, Func<int, int> next)
    {
        if (findsSinceMonster + 1 >= rules.PityAfter || next(100) < MonsterChance(rules, boost)) return "Monster";

        int roll = next(Math.Max(1, (int)rules.Weights.Sum(w => w.Weight)));
        foreach (MonsterCatalog.WeightEntry weight in rules.Weights)
        {
            roll -= (int)weight.Weight;
            if (roll < 0) return weight.Type;
        }
        return "Treasure";
    }

    /// <summary>The odds, for players to see. Trainers and the rest share what a Stepling leaves.</summary>
    public static List<OddsDto> Odds(MonsterCatalog.DiscoveryEntry rules, float boost)
    {
        int monster = MonsterChance(rules, boost);
        var odds = new List<OddsDto> { new("A Stepling", monster) };
        double total = Math.Max(1, rules.Weights.Sum(w => w.Weight));
        foreach (MonsterCatalog.WeightEntry weight in rules.Weights)
        {
            string name = weight.Type switch { "WalkingBonus" => "A walking bonus", "CardFragment" => "Fragments", "Trainer" => "A trainer", _ => "Treasure" };
            odds.Add(new OddsDto(name, (int)Math.Round((100 - monster) * weight.Weight / total)));
        }
        return odds;
    }

    private static async Task<IResult> Investigate(InvestigateRequest? request, ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        profile.ProgressStarted = true;

        DateTime now = DateTime.UtcNow;
        string day = Explorer.Today(profile, now);
        WalkingDay row = await Explorer.DayAsync(db, me, day, cancel);
        MonsterCatalog.DiscoveryEntry rules = MonsterCatalog.Shared.Discovery;

        if (row.Discoveries >= rules.Earned(row.Steps))
        {
            int nextAt = rules.NextAt(row.Steps);
            ActivityLog.Add(db, me, "find-refused", "Asked with " + row.Discoveries + " found and " + row.Steps + " steps");
            await db.SaveChangesAsync(cancel);
            return Results.Problem(nextAt == 0
                ? "That's all " + rules.Steps.Count + " finds for today. More tomorrow!"
                : "There's nothing here yet. Your next find comes at " + nextAt.ToString("N0") + " steps.", statusCode: StatusCodes.Status409Conflict);
        }

        bool golden = row.Discoveries == rules.Steps.Count - 1;
        string roll = Roll(rules, profile.FindsSinceMonster, BoostOf(profile, now), System.Security.Cryptography.RandomNumberGenerator.GetInt32);
        if (golden && roll == "Trainer") roll = "Treasure";   // the last of the day is always something to keep

        row.Discoveries++;
        profile.FindsSinceMonster = roll == "Monster" ? 0 : profile.FindsSinceMonster + 1;
        var tally = new RewardTally();
        Explorer.AddXp(db, profile, MonsterCatalog.Shared.Progression.XpPerDiscovery, "Find");
        tally.Xp += MonsterCatalog.Shared.Progression.XpPerDiscovery;

        string kind, message;
        Guid? monsterId = null;
        string? speciesId = null, trainerId = null;
        string detail = "Find " + row.Discoveries + " of " + rules.Steps.Count + " today";

        switch (roll)
        {
            case "Monster":
                if (await MonsterSlots.HasRoomAsync(db, me, cancel))
                {
                    string? biome = string.IsNullOrWhiteSpace(request?.Biome) ? null : request.Biome.Trim().ToLowerInvariant();
                    (string species, int tier, long seed, int potential) = finder.Roll(biome, golden ? 1 : 0);
                    WalkingMonster monster = await MonsterEndpoints.Add(db, me, species, tier, seed, potential, MonsterCatalog.Shared.RulesVersion, Source, biome, day, cancel);
                    monsterId = monster.Id;
                    speciesId = species;
                    kind = "monster";
                    message = "A shy Stepling is hiding here!";
                    tally.Monsters++;
                }
                else
                {
                    int instead = MonsterCatalog.Shared.Progression.GlimmerInsteadOfMonster;
                    WalkingWallet.Change(db, profile, instead, "find", detail + ": team full, Glimmer instead of a monster");
                    tally.Glimmer += instead;
                    kind = "fullteam";
                    message = "A Stepling peeked out, but your team is full. It left you some Glimmer.";
                }
                break;

            case "Trainer":
                List<MonsterCatalog.TrainerEntry> trainers = MonsterCatalog.Shared.Trainers;
                trainerId = trainers.Count > 0 ? trainers[Random.Shared.Next(trainers.Count)].Id : null;
                kind = "trainer";
                message = trainerId != null ? "A trainer wants to battle!" : "Nobody was there after all.";
                break;

            case "WalkingBonus":
                await Explorer.GrantAsync(db, profile, finder, rules.WalkingBonus, "find", detail + ": walking bonus", day, now, tally, cancel);
                kind = "bonus";
                message = "Walking bonus! Keep it up.";
                break;

            case "CardFragment":
                await Explorer.GrantAsync(db, profile, finder, rules.Fragments, "find", detail + ": fragments", day, now, tally, cancel);
                kind = "fragments";
                message = "You found shimmering fragments.";
                break;

            default:
                await Explorer.GrantAsync(db, profile, finder, rules.Treasure, "find", detail + ": treasure", day, now, tally, cancel);
                kind = "treasure";
                message = "You uncovered a hidden cache!";
                break;
        }

        if (golden)
        {
            WalkingWallet.Change(db, profile, rules.GoldenGlimmer, "find", "The Golden Cache");
            profile.Fragments += rules.GoldenFragments;
            Explorer.AddXp(db, profile, rules.GoldenXp, "The Golden Cache");
            tally.Glimmer += rules.GoldenGlimmer;
            tally.Fragments += rules.GoldenFragments;
            tally.Xp += rules.GoldenXp;
            if (kind != "monster" && kind != "fullteam") kind = "golden";
            message = "The Golden Cache! That's all " + rules.Steps.Count + " finds today." + (kind == "monster" ? " And a shy Stepling is hiding inside!" : "");
        }

        ActivityLog.Add(db, me, "find", detail + ": " + kind + (speciesId != null ? " " + speciesId : "") + ", " + tally.Describe());
        ProgressDto dto = await ProgressEndpoints.BuildAsync(db, profile, now, cancel);
        await db.SaveChangesAsync(cancel);
        return Results.Ok(new InvestigateResultDto(kind, golden, message, tally.Glimmer, tally.Xp, tally.Fragments, tally.Extras, monsterId, speciesId, trainerId, dto));
    }

    // A happier start for a Stepling you made friends with: up to 2 training points on its lowest stats, within its potential.
    private static async Task<IResult> Befriend(Guid monsterId, BefriendRequest? request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == monsterId && m.OwnerId == me && m.Source == Source, cancel);
        if (monster == null) return Results.NotFound();
        if (monster.BefriendedUtc != null) return Results.Problem("You've already made friends with this one.", statusCode: StatusCodes.Status409Conflict);

        monster.BefriendedUtc = DateTime.UtcNow;
        int total = monster.TrainedPower + monster.TrainedGuard + monster.TrainedTricks + monster.TrainedWits;
        int given = Math.Clamp(request?.Points ?? 0, 0, Math.Min(2, Math.Max(0, Explorer.MostTraining(monster) - total)));
        for (int i = 0; i < given; i++) Companions.AddTrainingPoint(monster, Companions.Balanced);

        ActivityLog.Add(db, me, "befriended", monster.SpeciesId + ": +" + given + " training");
        await db.SaveChangesAsync(cancel);
        return Results.Ok(new { given });
    }
}

/// <summary>Your companion's growth from walking together: training, and steps together (the server counts both now).</summary>
public static class Companions
{
    public const int DailySteps = 3000;
    public const int Balanced = 0;

    /// <summary>A point of training: on the stat you chose, or, if Balanced, the one with the fewest so far. Returns which.</summary>
    public static string AddTrainingPoint(WalkingMonster m, int focus)
    {
        int stat = focus switch
        {
            1 => 0,
            2 => 1,
            3 => 2,
            4 => 3,
            _ => Lowest(m),
        };

        switch (stat)
        {
            case 0: m.TrainedPower++; return "Power";
            case 1: m.TrainedGuard++; return "Guard";
            case 2: m.TrainedTricks++; return "Tricks";
            default: m.TrainedWits++; return "Wits";
        }
    }

    // Ties go in the order Power, Guard, Tricks, Wits, as in the game.
    private static int Lowest(WalkingMonster m)
    {
        int[] points = { m.TrainedPower, m.TrainedGuard, m.TrainedTricks, m.TrainedWits };
        int best = 0;
        for (int i = 1; i < 4; i++) if (points[i] < points[best]) best = i;
        return best;
    }

    /// <summary>Steps with your companion: they grow your bond, and fill today's training bar (one point a day).</summary>
    public static void OnSteps(AppDbContext db, WalkingProfile profile, WalkingMonster monster, int steps, string today, List<string> notices)
    {
        monster.BondSteps += steps;

        if (monster.TrainingDay != today)
        {
            monster.TrainingDay = today;
            monster.TrainingSteps = 0;
        }

        int total = monster.TrainedPower + monster.TrainedGuard + monster.TrainedTricks + monster.TrainedWits;
        int most = Explorer.MostTraining(monster);
        if (total >= most || monster.TrainingSteps >= DailySteps) return;

        monster.TrainingSteps = Math.Min(DailySteps, monster.TrainingSteps + steps);
        if (monster.TrainingSteps < DailySteps) return;

        string stat = AddTrainingPoint(monster, profile.TrainingFocus);
        string name = !string.IsNullOrEmpty(monster.Nickname) ? monster.Nickname : MonsterCatalog.Shared.FindSpecies(monster.SpeciesId)?.Name ?? "Your companion";
        notices.Add(total + 1 >= most
            ? name + " trained +1 " + stat + " - and is now FULLY TRAINED. What a journey together!"
            : name + " finished today's training: +1 " + stat + "! Next point tomorrow.");
        ActivityLog.Add(db, profile.UserId, "training", monster.SpeciesId + ": +1 " + stat);
    }
}
