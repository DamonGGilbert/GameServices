namespace GameServices.Api.Economy;

public record CosmeticDefinition(string Id, string Name, string Kind, int Price);

/// <summary>
/// The playmats and sleeves you can BUY with Duel Points. Like pack prices, they live here on the server so a player
/// can't edit them. The id is the picture's sprite name in the game (file name + "_0").
/// Who owns what is read from the ledger (Kind "cosmetic", the id in Detail), so there is nothing extra to store.
/// </summary>
public static class Cosmetics
{
    public const string LedgerKind = "cosmetic";

    public static readonly IReadOnlyList<CosmeticDefinition> All = new[]
    {
        new CosmeticDefinition("SakuraPetals_0", "Sakura Petals", "playmat", 900),
        new CosmeticDefinition("MoonlitLake_0", "Moonlit Lake", "playmat", 1200),
        new CosmeticDefinition("VolcanoCore_0", "Volcano Core", "playmat", 1500),
        new CosmeticDefinition("Starfield_0", "Starfield", "playmat", 1800),
        new CosmeticDefinition("NeonSunset_0", "Neon Sunset", "playmat", 2100),
        new CosmeticDefinition("RoyalCourt_0", "Royal Court", "playmat", 2400),
        new CosmeticDefinition("CherryBlossomSleeve_0", "Cherry Blossom", "sleeve", 600),
        new CosmeticDefinition("OceanWaveSleeve_0", "Ocean Wave", "sleeve", 800),
        new CosmeticDefinition("LavaFlowSleeve_0", "Lava Flow", "sleeve", 1000),
        new CosmeticDefinition("GalaxySleeve_0", "Galaxy", "sleeve", 1400),
        new CosmeticDefinition("RainbowFoilSleeve_0", "Rainbow Foil", "sleeve", 1800),
        new CosmeticDefinition("RoyalCrownSleeve_0", "Royal Crown", "sleeve", 2200),
        new CosmeticDefinition("AutumnLeaves_0", "Autumn Leaves", "playmat", 700),
        new CosmeticDefinition("FrostedPeaks_0", "Frosted Peaks", "playmat", 1000),
        new CosmeticDefinition("DesertDunes_0", "Desert Dunes", "playmat", 1300),
        new CosmeticDefinition("ToxicSwamp_0", "Toxic Swamp", "playmat", 1600),
        new CosmeticDefinition("CyberCity_0", "Cyber City", "playmat", 1900),
        new CosmeticDefinition("ThunderStorm_0", "Thunder Storm", "playmat", 2200),
        new CosmeticDefinition("CelestialGate_0", "Celestial Gate", "playmat", 2600),
        new CosmeticDefinition("DragonScale_0", "Dragon Scale", "playmat", 3000),
        new CosmeticDefinition("MintLeafSleeve_0", "Mint Leaf", "sleeve", 500),
        new CosmeticDefinition("SunburstSleeve_0", "Sunburst", "sleeve", 700),
        new CosmeticDefinition("FrostSleeve_0", "Frost", "sleeve", 900),
        new CosmeticDefinition("ToxicSleeve_0", "Toxic", "sleeve", 1100),
        new CosmeticDefinition("CircuitSleeve_0", "Circuit", "sleeve", 1300),
        new CosmeticDefinition("ThunderSleeve_0", "Thunder", "sleeve", 1600),
        new CosmeticDefinition("DragonScaleSleeve_0", "Dragon Scale", "sleeve", 2000),
        new CosmeticDefinition("CelestialSleeve_0", "Celestial", "sleeve", 2600),
        new CosmeticDefinition("ClassicCheckers_0", "Classic Checkers", "playmat", 600),
        new CosmeticDefinition("CoralReef_0", "Coral Reef", "playmat", 1100),
        new CosmeticDefinition("GalaxySpiral_0", "Galaxy Spiral", "playmat", 2000),
        new CosmeticDefinition("PrismWaves_0", "Prism Waves", "playmat", 2400),
        new CosmeticDefinition("CheckerSleeve_0", "Checker", "sleeve", 500),
        new CosmeticDefinition("CoralSleeve_0", "Coral", "sleeve", 800),
        new CosmeticDefinition("SpiralSleeve_0", "Spiral", "sleeve", 1200),
        new CosmeticDefinition("PrismWaveSleeve_0", "Prism Wave", "sleeve", 2000),
    };

    public static CosmeticDefinition? Find(string? id) => All.FirstOrDefault(c => c.Id == id);
}
