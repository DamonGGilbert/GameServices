using System.Text.Json;

namespace GameServices.Api.Walking.Monsters;

/// <summary>
/// The walking game's monster design data that the server needs to roll a find: every species (element, how common,
/// favourite places, which rarities it can be), the rarity table, the kinds of place and the starter.
///
/// It's exported from Unity (Wanderkin > Export Monster Catalog) to Walking/monster-catalog.json. For ranked fights it
/// also holds what decides a monster's stats (base stats, personalities, rarity bonuses, level growth - see
/// MonsterStats). Looks aren't here - the game builds those from the seed.
/// </summary>
public sealed class MonsterCatalog
{
    public int RulesVersion { get; init; }
    public string StarterSpeciesId { get; init; } = "";
    public double FavouredBiomeMultiplier { get; init; } = 2.0;
    public float StatGrowthPerLevel { get; init; } = 0.12f;
    public List<PersonalityEntry> Personalities { get; init; } = new();
    public List<RarityEntry> Rarities { get; init; } = new();
    public List<SpeciesEntry> Species { get; init; } = new();
    public List<BiomeEntry> Biomes { get; init; } = new();

    public sealed class RarityEntry
    {
        public int Tier { get; init; }
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public double Weight { get; init; }
        public float StatBonusPercent { get; init; }
    }

    public sealed class SpeciesEntry
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Element { get; init; } = "";
        public double SpawnWeight { get; init; }
        public int MinTier { get; init; }
        public int MaxTier { get; init; } = 4;
        public bool Wild { get; init; }
        public List<string> FavouredBiomes { get; init; } = new();
        public BaseStatsEntry BaseStats { get; init; } = new();
        /// <summary>This species' own personalities (ids); empty = the full list.</summary>
        public List<string> Personalities { get; init; } = new();
    }

    public sealed class BaseStatsEntry
    {
        public int Power { get; init; }
        public int Guard { get; init; }
        public int Health { get; init; }
        public int Tricks { get; init; }
    }

    /// <summary>A personality: how likely it is, and the % it adds to (or takes from) each stat.</summary>
    public sealed class PersonalityEntry
    {
        public string Id { get; init; } = "";
        public float Weight { get; init; } = 1f;
        public int Power { get; init; }
        public int Guard { get; init; }
        public int Health { get; init; }
        public int Tricks { get; init; }
    }

    public sealed class BiomeEntry
    {
        public string Id { get; init; } = "";
        public Dictionary<string, double> Elements { get; init; } = new();
    }

    public SpeciesEntry? FindSpecies(string id) => Species.FirstOrDefault(s => s.Id == id);

    public static MonsterCatalog LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The walking game's monster catalog is missing. Export it from Unity (Wanderkin > Export Monster Catalog).", path);
        }

        MonsterCatalog? catalog = JsonSerializer.Deserialize<MonsterCatalog>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (catalog == null || catalog.Species.Count == 0 || catalog.Rarities.Count == 0)
        {
            throw new InvalidDataException("The monster catalog has no species or rarities: " + path);
        }
        return catalog;
    }
}
