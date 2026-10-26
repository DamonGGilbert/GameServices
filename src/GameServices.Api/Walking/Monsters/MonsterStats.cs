namespace GameServices.Api.Walking.Monsters;

/// <summary>A monster's battle stats, exactly as its card shows them in the game.</summary>
public readonly record struct MonsterBattleStats(int Power, int Guard, int Tricks, int Wits, int Health)
{
    /// <summary>BST (base stat total): what ranked matchmaking compares.</summary>
    public int Total => Power + Guard + Tricks + Wits + Health;
}

/// <summary>
/// WORKS OUT A MONSTER'S STATS ON THE SERVER - the same maths as the game, so ranked fights never have to trust a phone.
///
/// Copied EXACTLY from the game (and checked against numbers the game itself worked out - MonsterStatsTests):
///   * Monsters/MonsterGenerationService.cs - the personality (picked by weight from the seed's "personality" stream)
///     and the four rolled stats (the "stats" stream: base x 0.85-1.15 x rarity bonus x personality)
///   * Cards/MonsterCardInstance.cs        - Wits (from the "wits" stream) and the card's numbers: training points
///     added, then everything grown by level
///   * Progression/ProgressionConfig.cs    - ScaleStat: value x (1 + (level - 1) x growth)
/// Floats on purpose, like the game: the same sums in the same order give the same numbers.
/// </summary>
public static class MonsterStats
{
    private const float StatRollMin = 0.85f;
    private const float StatRollMax = 1.15f;

    public static MonsterBattleStats For(MonsterCatalog catalog, string speciesId, long seed, int rarityTier, int level,
        int trainedPower, int trainedGuard, int trainedTricks, int trainedWits)
    {
        MonsterCatalog.SpeciesEntry species = catalog.FindSpecies(speciesId)
            ?? throw new ArgumentException("Unknown species " + speciesId, nameof(speciesId));
        MonsterCatalog.RarityEntry? rarity = catalog.Rarities.FirstOrDefault(r => r.Tier == rarityTier);

        // Personality: the species' own list, or everyone's.
        List<MonsterCatalog.PersonalityEntry> pool = species.Personalities.Count > 0
            ? species.Personalities.Select(id => catalog.Personalities.FirstOrDefault(p => p.Id == id)).Where(p => p != null).Select(p => p!).ToList()
            : catalog.Personalities;
        var personalityRng = new DeterministicRandom(SeedUtility.Derive(seed, "personality"));
        MonsterCatalog.PersonalityEntry? personality = personalityRng.Pick(pool, p => p.Weight);

        // The four rolled stats, in the game's order: attack (Power), defence (Guard), health, speed (Tricks).
        var statRng = new DeterministicRandom(SeedUtility.Derive(seed, "stats"));
        float rarityBonus = 1f + (rarity != null ? rarity.StatBonusPercent : 0f) / 100f;
        int power = RollStat(species.BaseStats.Power, statRng, rarityBonus, personality?.Power ?? 0);
        int guard = RollStat(species.BaseStats.Guard, statRng, rarityBonus, personality?.Guard ?? 0);
        int health = RollStat(species.BaseStats.Health, statRng, rarityBonus, personality?.Health ?? 0);
        int tricks = RollStat(species.BaseStats.Tricks, statRng, rarityBonus, personality?.Tricks ?? 0);

        // Wits: around the average of the other three.
        var witsRng = new DeterministicRandom(SeedUtility.Derive(seed, "wits"));
        int wits = Math.Max(1, (int)Math.Round((power + guard + tricks) / 3f * witsRng.Range(0.75f, 1.25f)));

        int lv = Math.Clamp(level, 1, 100);
        float growth = catalog.StatGrowthPerLevel;
        int Scale(int value) => Math.Max(1, (int)Math.Round(value * (1f + (lv - 1) * growth)));

        return new MonsterBattleStats(
            Scale(power + Math.Max(0, trainedPower)),
            Scale(guard + Math.Max(0, trainedGuard)),
            Scale(tricks + Math.Max(0, trainedTricks)),
            Scale(wits + Math.Max(0, trainedWits)),
            Scale(health));
    }

    private static int RollStat(int baseValue, DeterministicRandom rng, float rarityBonus, int personalityPercent)
    {
        float roll = rng.Range(StatRollMin, StatRollMax);
        float value = baseValue * roll * rarityBonus * (1f + personalityPercent / 100f);
        return Math.Max(1, (int)Math.Round(value));
    }
}
