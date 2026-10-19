using GameServices.Api.Data;

namespace GameServices.Api.Walking.Monsters;

/// <summary>
/// Brings monsters made under older game rules up to date, once, when the server starts.
///
/// Rules version 3 cut the rarities from seven to five:
///   Common 0, Uncommon 1, Rare 2, Super Rare 3 -> Epic 3, Ultra Rare 4 -> Epic 3, Mythic 5 -> Legendary 4, Legendary 6 -> Legendary 4.
/// Potential depends on rarity, so it's worked out again from the seed (exactly as the game does).
/// </summary>
public static class MonsterRulesUpgrade
{
    public const int CurrentRulesVersion = 3;

    /// <summary>Old seven-tier rarity -> the five-tier one.</summary>
    public static int TierFromVersion2(int oldTier) => oldTier switch
    {
        <= 2 => Math.Max(0, oldTier),
        3 or 4 => 3,
        _ => 4,
    };

    public static int Run(AppDbContext db)
    {
        List<WalkingMonster> old = db.WalkingMonsters.Where(m => m.RulesVersion < CurrentRulesVersion).ToList();
        foreach (WalkingMonster m in old)
        {
            m.RarityTier = TierFromVersion2(m.RarityTier);
            m.Potential = MonsterPotential.For(m.Seed, m.RarityTier);
            m.RulesVersion = CurrentRulesVersion;
        }

        if (old.Count > 0) db.SaveChanges();
        return old.Count;
    }
}
