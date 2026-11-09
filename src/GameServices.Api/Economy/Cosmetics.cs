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
    };

    public static CosmeticDefinition? Find(string? id) => All.FirstOrDefault(c => c.Id == id);
}
