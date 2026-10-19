using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Monsters;

public record WalkingMonsterDto(Guid Id, string SpeciesId, long Seed, int RarityTier, int Potential, int RulesVersion, int Serial,
    string Source, string? BiomeId, DateTime FoundUtc, bool Seen,
    int Level, int Experience, int TrainedPower, int TrainedGuard, int TrainedTricks, int TrainedWits, int TrainingSteps,
    string? ArrivedFrom);

public record MonsterProgressDto(Guid Id, int Level, int Experience, int TrainedPower, int TrainedGuard, int TrainedTricks, int TrainedWits, int TrainingSteps);

/// <summary>The game's view of its monsters' progress, and which one is the companion.</summary>
public record ProgressRequest(Guid? CompanionId, List<MonsterProgressDto> Monsters);

/// <summary>
/// THE WALKING GAME'S MONSTERS, made on the server.
///
///   GET  /api/walking/monsters            my monsters (a first-timer is given the starter companion here)
///   POST /api/walking/monsters/{id}/seen  I've watched its reveal - don't show it as new again
///   PUT  /api/walking/monsters/progress   the game's levels and training for my monsters, and my companion
///                                         (kept so a monster's progress goes with it to a new home)
///
/// New monsters are never asked for: they're found while walking, when a step report earns a lucky chance
/// (see MonsterFinder and ReportSteps). The server stores what it rolled - species, rarity, seed, potential and a
/// serial number ("the 12th Emberling ever found") - and the game builds every other detail from the seed.
/// </summary>
public static class MonsterEndpoints
{
    public static RouteGroupBuilder MapWalkingMonsterEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/monsters", Mine);
        group.MapPost("/monsters/{monsterId:guid}/seen", MarkSeen);
        group.MapPut("/monsters/progress", SaveProgress);
        return group;
    }

    private static async Task<IResult> Mine(ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        // Every walker has a companion: the first visit brings the starter.
        if (!await db.WalkingMonsters.AnyAsync(m => m.OwnerId == me, cancel))
        {
            (string species, int tier, long seed, int potential) = finder.RollStarter();
            await Add(db, me, species, tier, seed, potential, finder.Catalog.RulesVersion, "starter", null, DateTime.UtcNow.ToString("yyyy-MM-dd"), cancel);

            try
            {
                await db.SaveChangesAsync(cancel);
            }
            catch (DbUpdateException)
            {
                // Two requests at once both tried to give the starter (or two finds took the same serial): the other
                // one won, so carry on and show what's there.
                db.ChangeTracker.Clear();
            }
        }

        List<WalkingMonster> mine = await db.WalkingMonsters.AsNoTracking().Where(m => m.OwnerId == me).OrderBy(m => m.FoundUtc).ToListAsync(cancel);
        List<Guid> givers = mine.Where(m => m.ArrivedFromUserId != null).Select(m => m.ArrivedFromUserId!.Value).Distinct().ToList();
        Dictionary<Guid, string> names = await db.Users.Where(u => givers.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Username, cancel);

        return Results.Ok(mine.Select(m => ToDto(m, m.ArrivedFromUserId != null && names.TryGetValue(m.ArrivedFromUserId.Value, out string? n) ? n : null)).ToList());
    }

    public static WalkingMonsterDto ToDto(WalkingMonster m, string? arrivedFrom) =>
        new(m.Id, m.SpeciesId, m.Seed, m.RarityTier, m.Potential, m.RulesVersion, m.Serial, m.Source, m.BiomeId, m.FoundUtc, m.Seen,
            m.Level, m.Experience, m.TrainedPower, m.TrainedGuard, m.TrainedTricks, m.TrainedWits, m.TrainingSteps, arrivedFrom);

    // The game sends its monsters' levels and training. The server can't watch every step of a battle or a walk, so it
    // keeps what's believable: no level above 100, never more training than the monster's potential allows.
    private static async Task<IResult> SaveProgress(ProgressRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        Dictionary<Guid, WalkingMonster> mine = await db.WalkingMonsters.Where(m => m.OwnerId == me).ToDictionaryAsync(m => m.Id, cancel);

        foreach (MonsterProgressDto p in request?.Monsters ?? new List<MonsterProgressDto>())
        {
            if (!mine.TryGetValue(p.Id, out WalkingMonster? m)) continue;
            int maxPoints = 10 + (int)Math.Round(m.Potential * 0.4);
            m.Level = Math.Clamp(p.Level, 1, 100);
            m.Experience = Math.Max(0, p.Experience);

            // Training is one point a day, so a monster can't have more points than days since it was found (plus
            // today). The server's own clock decides that, so changing the phone's date doesn't help. A claim that
            // breaks the rule is simply not taken - the points saved before stay as they were.
            int before = m.TrainedPower + m.TrainedGuard + m.TrainedTricks + m.TrainedWits;
            int claimed = Math.Max(0, p.TrainedPower) + Math.Max(0, p.TrainedGuard) + Math.Max(0, p.TrainedTricks) + Math.Max(0, p.TrainedWits);
            int daysOwned = (int)(DateTime.UtcNow - m.FoundUtc).TotalDays + 1;
            if (claimed <= before || claimed <= Math.Min(maxPoints, daysOwned))
            {
                m.TrainedPower = Math.Max(0, p.TrainedPower);
                m.TrainedGuard = Math.Max(0, p.TrainedGuard);
                m.TrainedTricks = Math.Max(0, p.TrainedTricks);
                m.TrainedWits = Math.Max(0, p.TrainedWits);
            }
            if (m.TrainedPower + m.TrainedGuard + m.TrainedTricks + m.TrainedWits > maxPoints)
            {
                m.TrainedPower = m.TrainedGuard = m.TrainedTricks = m.TrainedWits = 0;   // more than is possible: not believed
            }
            m.TrainingSteps = Math.Clamp(p.TrainingSteps, 0, 20000);
        }

        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);
        if (request?.CompanionId is Guid companion && mine.ContainsKey(companion)) profile.CompanionMonsterId = companion;

        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    private static async Task<IResult> MarkSeen(Guid monsterId, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        WalkingMonster? monster = await db.WalkingMonsters.FirstOrDefaultAsync(m => m.Id == monsterId && m.OwnerId == me, cancel);
        if (monster == null) return Results.NotFound();

        monster.Seen = true;
        await db.SaveChangesAsync(cancel);
        return Results.NoContent();
    }

    /// <summary>
    /// Called by the step report with the steps it just accepted: adds them to the walker's carried-over count, rolls
    /// a chance for every 5,000, and adds any finds (saved together with the report). Returns how many were found.
    /// </summary>
    public static async Task<int> RollFinds(AppDbContext db, MonsterFinder finder, Guid me, WalkingProfile profile, int addedSteps,
        string day, string? biomeId, CancellationToken cancel)
    {
        int carried = profile.EncounterSteps;
        int chances = finder.AddSteps(ref carried, addedSteps);
        profile.EncounterSteps = carried;
        if (chances == 0) return 0;

        int foundToday = await db.WalkingMonsters.CountAsync(m => m.OwnerId == me && m.FoundDay == day && m.Source == "walk", cancel);
        int found = 0;

        for (int i = 0; i < chances && foundToday + found < finder.MaxFindsPerDay; i++)
        {
            if (!finder.Lucky()) continue;
            (string species, int tier, long seed, int potential) = finder.Roll(biomeId);
            await Add(db, me, species, tier, seed, potential, finder.Catalog.RulesVersion, "walk", biomeId, day, cancel);
            found++;
        }

        return found;
    }

    // A new monster with the next serial number for its species (the counter is saved with it).
    public static async Task Add(AppDbContext db, Guid owner, string speciesId, int tier, long seed, int potential, int rulesVersion,
        string source, string? biomeId, string day, CancellationToken cancel)
    {
        MonsterSpeciesCount? count = db.MonsterSpeciesCounts.Local.FirstOrDefault(c => c.SpeciesId == speciesId)
                                    ?? await db.MonsterSpeciesCounts.FirstOrDefaultAsync(c => c.SpeciesId == speciesId, cancel);
        if (count == null)
        {
            count = new MonsterSpeciesCount { SpeciesId = speciesId };
            db.MonsterSpeciesCounts.Add(count);
        }

        count.Found++;
        db.WalkingMonsters.Add(new WalkingMonster
        {
            OwnerId = owner,
            SpeciesId = speciesId,
            RarityTier = tier,
            Seed = seed,
            Potential = potential,
            RulesVersion = rulesVersion,
            Serial = count.Found,
            Source = source,
            BiomeId = biomeId,
            FoundDay = day,
            FoundUtc = DateTime.UtcNow,
            Seen = source == "starter",   // the starter isn't a surprise
        });
    }
}
