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
    /// <summary>The one loaded when the server started (it never changes after that).</summary>
    public static MonsterCatalog Shared { get; private set; } = new();

    public int RulesVersion { get; init; }
    public string StarterSpeciesId { get; init; } = "";
    public double FavouredBiomeMultiplier { get; init; } = 2.0;
    public float StatGrowthPerLevel { get; init; } = 0.12f;
    public List<PersonalityEntry> Personalities { get; init; } = new();
    public List<RarityEntry> Rarities { get; init; } = new();
    public List<SpeciesEntry> Species { get; init; } = new();
    public List<BiomeEntry> Biomes { get; init; } = new();
    public List<TrainerEntry> Trainers { get; init; } = new();
    public BattleEntry Battle { get; init; } = new();

    /// <summary>The rules of an explorer's progress (Walking/Progress).</summary>
    public ProgressionEntry Progression { get; init; } = new();

    /// <summary>What turns up on the map (Walking/Progress/Discoveries).</summary>
    public DiscoveryEntry Discovery { get; init; } = new();

    /// <summary>One thing in a reward. Type is the game's RewardType.</summary>
    public sealed class RewardEntry
    {
        public string Type { get; init; } = "";
        public int Amount { get; init; }
        public int MinTier { get; init; }
        public string ItemId { get; init; } = "";
        public float Multiplier { get; init; } = 1f;
    }

    public sealed class MilestoneEntry
    {
        public int Steps { get; init; }
        public List<RewardEntry> Reward { get; init; } = new();
    }

    public sealed class StreakRewardEntry
    {
        public int Days { get; init; }
        public List<RewardEntry> Reward { get; init; } = new();
    }

    public sealed class QuestEntry
    {
        public string Id { get; init; } = "";
        public int Stat { get; init; }
        public int Target { get; init; }
        public double Weight { get; init; } = 1;
        public bool Always { get; init; }
        public List<RewardEntry> Reward { get; init; } = new();
    }

    public sealed class AchievementEntry
    {
        public string Id { get; init; } = "";
        public int Stat { get; init; }
        public long Target { get; init; }
        public string Badge { get; init; } = "";
        public string Title { get; init; } = "";
        public bool HasReward { get; init; }
        public List<RewardEntry> Reward { get; init; } = new();
    }

    public sealed class MedalEntry
    {
        public string Id { get; init; } = "";
        public string Noun { get; init; } = "";
        public int Stat { get; init; }
        public List<long> Rungs { get; init; } = new();
        public long Step { get; init; }

        /// <summary>How many of its medals this value has earned (past the last set rung, it keeps going up in equal steps).</summary>
        public int Reached(long value)
        {
            if (Rungs.Count == 0 || value < Rungs[0]) return 0;
            if (value < Rungs[^1])
            {
                int n = 0;
                while (n < Rungs.Count && value >= Rungs[n]) n++;
                return n;
            }
            return Rungs.Count + (int)((value - Rungs[^1]) / Math.Max(1, Step));
        }
    }

    public sealed class ProgressionEntry
    {
        public int PlayerMaxLevel { get; init; } = 60;

        /// <summary>The experience to go from each level to the next, starting at level 1.</summary>
        public List<int> XpForLevel { get; init; } = new();
        public List<int> SlotLevels { get; init; } = new();
        public int StepsPerXp { get; init; } = 50;
        public int XpPerDiscovery { get; init; }
        public int XpPerNewSpecies { get; init; }
        public int XpPerTrade { get; init; }
        public int DailyGoalSteps { get; init; } = 6000;
        public int DailyRewardStepCap { get; init; } = 40000;
        public int RestDaysPerWeek { get; init; } = 1;
        public int DailyQuestCount { get; init; } = 3;
        public int GlimmerPerPack { get; init; } = 150;
        public int GlimmerInsteadOfMonster { get; init; } = 150;
        public List<MilestoneEntry> Milestones { get; init; } = new();
        public List<StreakRewardEntry> StreakRewards { get; init; } = new();
        public List<QuestEntry> Quests { get; init; } = new();
        public List<AchievementEntry> Achievements { get; init; } = new();
        public List<string> MedalTiers { get; init; } = new();
        public int MedalTitleFromRung { get; init; } = 2;
        public int MedalRewardFirst { get; init; } = 50;
        public int MedalRewardPerRung { get; init; } = 50;
        public int MedalRewardMost { get; init; } = 500;
        public List<MedalEntry> Medals { get; init; } = new();
        public List<FrameEntry> Frames { get; init; } = new();
    }

    /// <summary>A frame for a profile picture, and what earns it (any one that's set).</summary>
    public sealed class FrameEntry
    {
        public string Id { get; init; } = "";
        public string Cosmetic { get; init; } = "";
        public string Achievement { get; init; } = "";
        public int MedalRung { get; init; } = -1;
        public int Level { get; init; }
    }

    public sealed class WeightEntry
    {
        public string Type { get; init; } = "";
        public double Weight { get; init; }
    }

    public sealed class DiscoveryEntry
    {
        /// <summary>The steps in a day that bring each find.</summary>
        public List<int> Steps { get; init; } = new();
        public int MonsterPercent { get; init; }
        public int PityAfter { get; init; } = 15;
        public int GoldenGlimmer { get; init; }
        public int GoldenFragments { get; init; }
        public int GoldenXp { get; init; }
        public List<WeightEntry> Weights { get; init; } = new();
        public List<RewardEntry> Treasure { get; init; } = new();
        public List<RewardEntry> WalkingBonus { get; init; } = new();
        public List<RewardEntry> Fragments { get; init; } = new();

        public int Earned(int stepsToday)
        {
            int earned = 0;
            while (earned < Steps.Count && stepsToday >= Steps[earned]) earned++;
            return earned;
        }

        public int NextAt(int stepsToday)
        {
            int earned = Earned(stepsToday);
            return earned < Steps.Count ? Steps[earned] : 0;
        }
    }

    /// <summary>A trainer: how they fight, how strong against you, and their monsters (none = three wild ones, new each day).</summary>
    public sealed class TrainerEntry
    {
        public string Id { get; init; } = "";
        public string Style { get; init; } = "Balanced";
        public int LevelOffset { get; init; }
        public List<string> Team { get; init; } = new();
        public List<RewardEntry> WinReward { get; init; } = new();
    }

    /// <summary>What a battle against a wild monster or a trainer pays.</summary>
    public sealed class BattleEntry
    {
        public int GlimmerPerWin { get; init; } = 30;
        public int GlimmerPerLoss { get; init; } = 5;
        public int PlayerXpPerWin { get; init; } = 35;
        public int PlayerXpPerLoss { get; init; } = 10;
        public int MonsterXpPerBattle { get; init; } = 150;
        public int CompanionStepsPerXp { get; init; } = 5;
    }

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
        Shared = catalog;
        return catalog;
    }
}
