using System.Security.Claims;
using GameServices.Api.Data;
using GameServices.Api.Social;
using GameServices.Api.Walking.Progress;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Walking.Monsters;

public record WalkingMonsterDto(Guid Id, string SpeciesId, long Seed, int RarityTier, int Potential, int RulesVersion, int Serial,
    string Source, string? BiomeId, DateTime FoundUtc, bool Seen,
    int Level, int Experience, int TrainedPower, int TrainedGuard, int TrainedTricks, int TrainedWits, int TrainingSteps,
    string? ArrivedFrom, string Nickname = "",
    int AbsorbedPower = 0, int AbsorbedGuard = 0, int AbsorbedTricks = 0, int AbsorbedWits = 0, int CrystalPower = 0, int CrystalSteps = 0, int CrystalStepsLeft = 0,
    string CardStyle = "", long BondSteps = 0, int StepsToWake = 0, string TrainingDay = "");

/// <summary>A starter's progress from before signing in, taken once. Everything else about a monster's growth is the server's.</summary>
public record MonsterProgressDto(Guid Id, int Level, int Experience, int TrainedPower, int TrainedGuard, int TrainedTricks, int TrainedWits, int TrainingSteps, long BondSteps = 0);

/// <summary>The choices that are yours: which monster is your companion, what it trains, and the title you wear (if you've earned it).</summary>
public record ProgressRequest(Guid? CompanionId, List<MonsterProgressDto>? Monsters = null, string? Title = null, int? Focus = null, string? Frame = null,
    int? Colour = null, int? Motto = null);

/// <summary>
/// THE WALKING GAME'S MONSTERS, made on the server.
///
///   GET  /api/walking/monsters            my monsters (a first-timer is given the starter companion here)
///   POST /api/walking/monsters/{id}/seen  I've watched its reveal - don't show it as new again
///   PUT  /api/walking/monsters/progress   my companion, what it trains, the title and frame I wear, my colour and motto
///
/// New monsters are never asked for: they're found while walking, when a step report earns a lucky chance
/// (see MonsterFinder and ReportSteps). The server stores what it rolled - species, rarity, seed, potential and a
/// serial number ("the 12th Emberling ever found") - and the game builds every other detail from the seed.
/// </summary>
public static class MonsterEndpoints
{
    /// <summary>The most training a starter can bring from before signing in.</summary>
    public const int MostGuestTraining = 10;

    public static RouteGroupBuilder MapWalkingMonsterEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/monsters", Mine);
        group.MapMonsterNameEndpoints();
        group.MapPost("/monsters/{monsterId:guid}/seen", MarkSeen);
        group.MapPut("/monsters/progress", SaveProgress);
        return group;
    }

    private static async Task<IResult> Mine(ClaimsPrincipal principal, AppDbContext db, MonsterFinder finder, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        // Every walker has a companion: the first visit brings the starter.
        if (await GiveStarterIfNewAsync(db, me, finder, cancel))
        {
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

        long walked = await db.WalkingProfiles.AsNoTracking().Where(p => p.UserId == me).Select(p => p.LifetimeSteps).FirstOrDefaultAsync(cancel);
        return Results.Ok(mine.Select(m => ToDto(m, m.ArrivedFromUserId != null && names.TryGetValue(m.ArrivedFromUserId.Value, out string? n) ? n : null, walked)).ToList());
    }

    /// <summary>A walker with no monsters is given the starter (saved with whatever else is being saved). True if they were.</summary>
    public static async Task<bool> GiveStarterIfNewAsync(AppDbContext db, Guid me, MonsterFinder finder, CancellationToken cancel)
    {
        if (await db.WalkingMonsters.AnyAsync(m => m.OwnerId == me, cancel) || db.ChangeTracker.Entries<WalkingMonster>().Any(e => e.State == EntityState.Added && e.Entity.OwnerId == me)) return false;

        (string species, int tier, long seed, int potential) = finder.RollStarter();
        await Add(db, me, species, tier, seed, potential, finder.Catalog.RulesVersion, "starter", null, DateTime.UtcNow.ToString("yyyy-MM-dd"), cancel);
        return true;
    }

    /// <param name="walkerSteps">Its walker's lifetime steps, to say how far it is from waking up from a snooze.</param>
    public static WalkingMonsterDto ToDto(WalkingMonster m, string? arrivedFrom, long walkerSteps = long.MaxValue) =>
        new(m.Id, m.SpeciesId, m.Seed, m.RarityTier, m.Potential, m.RulesVersion, m.Serial, m.Source, m.BiomeId, m.FoundUtc, m.Seen,
            m.Level, m.Experience, m.TrainedPower, m.TrainedGuard, m.TrainedTricks, m.TrainedWits, m.TrainingSteps, arrivedFrom, m.Nickname ?? "",
            m.AbsorbedPower, m.AbsorbedGuard, m.AbsorbedTricks, m.AbsorbedWits, m.CrystalPower, m.CrystalSteps, m.CrystalStepsLeft, m.CardStyle ?? "",
            m.BondSteps, (int)Math.Clamp(m.RestUntilSteps - walkerSteps, 0, int.MaxValue), m.TrainingDay ?? "");

    // Your choices. A monster's levels, training and steps together are all worked out here (MonsterLevels, Companions); the
    // one thing taken from the phone is a starter's progress from before signing in, once, within limits.
    private static async Task<IResult> SaveProgress(ProgressRequest request, ClaimsPrincipal principal, AppDbContext db, CancellationToken cancel)
    {
        if (!SocialEndpoints.TryGetUserId(principal, out Guid me)) return Results.Unauthorized();

        Dictionary<Guid, WalkingMonster> mine = await db.WalkingMonsters.Where(m => m.OwnerId == me).ToDictionaryAsync(m => m.Id, cancel);
        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        WalkingProfile profile = await WalkingEndpoints.GetOrCreateProfile(db, me, cancel);

        foreach (MonsterProgressDto p in request?.Monsters ?? new List<MonsterProgressDto>())
        {
            if (profile.GuestProgressTaken || !mine.TryGetValue(p.Id, out WalkingMonster? m) || m.Source != "starter") continue;

            MonsterLevels.TakeGuestProgress(m, p.Level, p.Experience, today);
            m.BondSteps = Math.Max(m.BondSteps, Math.Clamp(p.BondSteps, 0, ProgressEndpoints.MostStartingSteps));

            int claimed = Math.Max(0, p.TrainedPower) + Math.Max(0, p.TrainedGuard) + Math.Max(0, p.TrainedTricks) + Math.Max(0, p.TrainedWits);
            if (claimed <= Math.Min(Explorer.MostTraining(m), MostGuestTraining))
            {
                m.TrainedPower = Math.Max(0, p.TrainedPower);
                m.TrainedGuard = Math.Max(0, p.TrainedGuard);
                m.TrainedTricks = Math.Max(0, p.TrainedTricks);
                m.TrainedWits = Math.Max(0, p.TrainedWits);
            }
        }

        if (request?.Monsters?.Count > 0) profile.GuestProgressTaken = true;
        if (request?.Focus is int focus) profile.TrainingFocus = Math.Clamp(focus, 0, 4);

        // A title has to be one you've earned. Anything else is ignored, and what you were wearing stays.
        if (request?.Title != null)
        {
            string title = Explorers.CleanTitle(request.Title);
            if (request.Title.Length == 0 || Explorer.HasItem(profile.Titles, title)) profile.Title = title;
        }

        // So does a frame (Explorer.EarnedFramesAsync).
        if (request?.Frame != null && (request.Frame.Length == 0 || (await Explorer.EarnedFramesAsync(db, profile, cancel)).Contains(request.Frame)))
        {
            profile.Frame = request.Frame;
        }

        // The colour of their profile card and the line under their name: any of the set ones (or none), never anything else.
        MonsterCatalog.ProgressionEntry rules = MonsterCatalog.Shared.Progression;
        if (request?.Colour is int colour && colour >= -1 && colour < rules.ProfileColours) profile.ProfileColour = colour;
        if (request?.Motto is int motto && motto >= -1 && motto < rules.Mottos.Count) profile.Motto = motto;

        if (request?.CompanionId is Guid companion && mine.ContainsKey(companion))
        {
            profile.CompanionMonsterId = companion;

            // Choosing a monster as your companion takes it off the Market (any top bid goes back).
            List<MarketListing> listed = await db.MarketListings.Where(l => l.MonsterId == companion && l.Status == ListingStatus.Active).ToListAsync(cancel);
            foreach (MarketListing listing in listed) await MonsterMarket.Close(db, listing, ListingStatus.Closed, DateTime.UtcNow, cancel);
        }

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
        if (!await MonsterSlots.HasRoomAsync(db, me, cancel)) return 0;   // a full team finds nothing new

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
    public static async Task<WalkingMonster> Add(AppDbContext db, Guid owner, string speciesId, int tier, long seed, int potential, int rulesVersion,
        string source, string? biomeId, string day, CancellationToken cancel)
    {
        WalkingProfile profile = db.WalkingProfiles.Local.FirstOrDefault(p => p.UserId == owner) ?? await WalkingEndpoints.GetOrCreateProfile(db, owner, cancel);
        Explorer.OnMonsterArrived(db, profile, speciesId, tier, source);

        MonsterSpeciesCount? count = db.MonsterSpeciesCounts.Local.FirstOrDefault(c => c.SpeciesId == speciesId)
                                    ?? await db.MonsterSpeciesCounts.FirstOrDefaultAsync(c => c.SpeciesId == speciesId, cancel);
        if (count == null)
        {
            count = new MonsterSpeciesCount { SpeciesId = speciesId };
            db.MonsterSpeciesCounts.Add(count);
        }

        count.Found++;
        WalkingMonster monster = new()
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
        };
        db.WalkingMonsters.Add(monster);
        return monster;
    }
}
