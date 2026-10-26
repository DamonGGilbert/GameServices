using GameServices.Api.Walking.Monsters;

namespace GameServices.Tests;

/// <summary>
/// The server's copy of the stat maths (Walking/Monsters/MonsterStats.cs) must give EXACTLY the numbers the game's
/// cards show. The golden values below were worked out by the game itself (MonsterGenerationService + the card).
/// </summary>
public class MonsterStatsTests
{
    private static readonly MonsterCatalog Catalog = MonsterCatalog.LoadFile(Path.Combine(AppContext.BaseDirectory, "Walking", "monster-catalog.json"));

    [Fact]
    public void The_catalog_has_what_stats_need()
    {
        Assert.Equal(0.12f, Catalog.StatGrowthPerLevel);
        Assert.Equal(180f, Catalog.Rarities.Single(r => r.Tier == 4).StatBonusPercent);
        Assert.Equal(8, Catalog.Personalities.Count);
        Assert.All(Catalog.Species, s => Assert.True(s.BaseStats.Power > 0 && s.BaseStats.Guard > 0 && s.BaseStats.Health > 0 && s.BaseStats.Tricks > 0));
    }

    // Worked out by the game itself (a monster generated from the seed, then its card), October 2026.
    [Theory]
    [InlineData("MON_0001", 42L, 0, 1, 0, 0, 0, 0, 51, 30, 40, 49, 49)]
    [InlineData("MON_0001", 42L, 4, 1, 0, 0, 0, 0, 142, 84, 112, 136, 138)]
    [InlineData("MON_0002", 123456789L, 2, 37, 5, 3, 8, 2, 266, 303, 239, 234, 458)]
    [InlineData("MON_0016", 7L, 4, 100, 12, 12, 13, 13, 2563, 2331, 2743, 2615, 2602)]
    [InlineData("MON_0010", -99L, 3, 64, 0, 20, 0, 0, 625, 728, 865, 608, 813)]
    [InlineData("MON_0014", long.MaxValue, 1, 12, 1, 1, 1, 1, 153, 148, 148, 160, 169)]
    [InlineData("MON_0005", 1L, 0, 7, 0, 0, 0, 0, 71, 52, 81, 55, 79)]
    public void Stats_match_the_game_exactly(string species, long seed, int tier, int level, int tp, int tg, int tt, int tw,
        int power, int guard, int tricks, int wits, int health)
    {
        MonsterBattleStats s = MonsterStats.For(Catalog, species, seed, tier, level, tp, tg, tt, tw);
        Assert.Equal((power, guard, tricks, wits, health), (s.Power, s.Guard, s.Tricks, s.Wits, s.Health));
    }

    [Fact]
    public void Levels_rarity_and_training_all_raise_the_BST()
    {
        int common = MonsterStats.For(Catalog, "MON_0001", 42, 0, 1, 0, 0, 0, 0).Total;
        int legendary = MonsterStats.For(Catalog, "MON_0001", 42, 4, 1, 0, 0, 0, 0).Total;
        int levelled = MonsterStats.For(Catalog, "MON_0001", 42, 0, 50, 0, 0, 0, 0).Total;
        int trained = MonsterStats.For(Catalog, "MON_0001", 42, 0, 1, 10, 10, 10, 10).Total;

        Assert.True(legendary > common * 2);   // +180%
        Assert.True(levelled > common * 6);    // x6.88 at level 50
        Assert.Equal(common + 40, trained);    // +1 per point at level 1
    }

    [Fact]
    public void A_fully_grown_Legendary_is_around_ten_thousand()
    {
        int bst = MonsterStats.For(Catalog, "MON_0016", 7, 4, 100, 12, 12, 13, 13).Total;
        Assert.InRange(bst, 7000, 14000);
    }
}
