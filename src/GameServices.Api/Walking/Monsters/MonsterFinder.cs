using System.Security.Cryptography;

namespace GameServices.Api.Walking.Monsters;

/// <summary>
/// HOW MONSTERS ARE FOUND - very rarely, by walking.
///
///   * Every 5,000 steps the server accepts is one CHANCE (steps carry over between reports, so offline walking
///     counts as soon as it's reported).
///   * Each chance finds a monster 1 time in 20 - so about one find per 100,000 steps: roughly every ten days of
///     good walking. Never more than one find a day.
///   * The find: a species that likes the kind of place you're in (park, coast, town...), a rarity from the game's
///     rarity table (most are Common; Legendary is about 1 in 100,000), a secure random SEED (the phone builds every
///     detail of the monster from it), and the hidden potential worked out from seed + rarity.
///
/// Only the server rolls these dice, with a cryptographic random generator, so nobody can predict or force a find.
/// The numbers live in appsettings ("Monsters" section) so they can be tuned without a new build.
/// </summary>
public sealed class MonsterFinder
{
    private readonly MonsterCatalog catalog;

    public int StepsPerChance { get; }
    public double FindChance { get; }
    public int MaxFindsPerDay { get; }

    public MonsterFinder(MonsterCatalog catalog, IConfiguration configuration)
    {
        this.catalog = catalog;
        StepsPerChance = Math.Max(100, configuration.GetValue("Monsters:StepsPerChance", 5000));
        FindChance = Math.Clamp(configuration.GetValue("Monsters:FindChance", 0.05), 0.0, 1.0);
        MaxFindsPerDay = Math.Max(0, configuration.GetValue("Monsters:MaxFindsPerDay", 1));
    }

    public MonsterCatalog Catalog => catalog;

    /// <summary>Adds newly accepted steps to the carried-over count; returns how many chances they earned.</summary>
    public int AddSteps(ref int carriedSteps, int addedSteps)
    {
        carriedSteps += Math.Max(0, addedSteps);
        int chances = carriedSteps / StepsPerChance;
        carriedSteps %= StepsPerChance;
        return chances;
    }

    /// <summary>Does this chance find a monster?</summary>
    public bool Lucky() => RandomDouble() < FindChance;

    /// <summary>The dice for one find (serial number and owner are filled in by whoever saves it).</summary>
    public (string SpeciesId, int RarityTier, long Seed, int Potential) Roll(string? biomeId, int minTier = 0)
    {
        MonsterCatalog.SpeciesEntry species = PickSpecies(biomeId);
        int tier = PickTier(Math.Max(minTier, species.MinTier), Math.Max(Math.Max(minTier, species.MinTier), species.MaxTier));
        long seed = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue) * 4294967296L + (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);
        return (species.Id, tier, seed, MonsterPotential.For(seed, tier));
    }

    /// <summary>The starter companion: always the starter species, always Common, with its own fresh seed.</summary>
    public (string SpeciesId, int RarityTier, long Seed, int Potential) RollStarter()
    {
        long seed = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue) * 4294967296L + (uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);
        string species = catalog.FindSpecies(catalog.StarterSpeciesId)?.Id ?? catalog.Species[0].Id;
        return (species, 0, seed, MonsterPotential.For(seed, 0));
    }

    // Like the game's SpeciesSelector: spawn weight, x2.5 in a favourite place, x the place's element boost.
    private MonsterCatalog.SpeciesEntry PickSpecies(string? biomeId)
    {
        MonsterCatalog.BiomeEntry? biome = catalog.Biomes.FirstOrDefault(b => b.Id == biomeId);
        var weighted = new List<(MonsterCatalog.SpeciesEntry Species, double Weight)>();

        foreach (MonsterCatalog.SpeciesEntry s in catalog.Species)
        {
            if (!s.Wild || s.SpawnWeight <= 0) continue;
            double w = s.SpawnWeight;
            if (biome != null)
            {
                if (s.FavouredBiomes.Contains(biome.Id)) w *= catalog.FavouredBiomeMultiplier;
                if (biome.Elements.TryGetValue(s.Element, out double boost)) w *= boost;
            }
            weighted.Add((s, w));
        }

        return Pick(weighted);
    }

    // Like the game's RarityTable.Roll: the table's weights, limited to the tiers this species can be.
    private int PickTier(int minTier, int maxTier)
    {
        var weighted = catalog.Rarities.Where(r => r.Tier >= minTier && r.Tier <= maxTier && r.Weight > 0).Select(r => (r.Tier, r.Weight)).ToList();
        return weighted.Count == 0 ? minTier : Pick(weighted);
    }

    private static T Pick<T>(List<(T Item, double Weight)> options)
    {
        double total = options.Sum(o => o.Weight);
        double roll = RandomDouble() * total;
        foreach ((T item, double weight) in options)
        {
            if (roll < weight) return item;
            roll -= weight;
        }
        return options[^1].Item;
    }

    private static double RandomDouble() => RandomNumberGenerator.GetInt32(0, int.MaxValue) / (double)int.MaxValue;
}
