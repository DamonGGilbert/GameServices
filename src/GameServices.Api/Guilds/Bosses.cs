namespace GameServices.Api.Guilds;

/// <param name="HpPerMember">Boss health = this x the number of guild members (at least 3), so big guilds face tougher bosses.</param>
/// <param name="RewardPool">Duel Points shared between everyone who hit the boss, in proportion to damage dealt.</param>
public record BossDefinition(string Key, string Name, string Description, int HpPerMember, int RewardPool, int DurationHours);

/// <summary>The bosses a guild can summon. Like pack odds, they live on the server so players can't edit them.</summary>
public static class Bosses
{
    public static readonly IReadOnlyList<BossDefinition> All = new[]
    {
        new BossDefinition("shadow-scout", "Shadow Scout", "A cheeky shadow-realm scout. A warm-up for new guilds.", 4000, 900, 24),
        new BossDefinition("stone-colossus", "Stone Colossus", "Slow, enormous and very hard to chip away at.", 9000, 2000, 48),
        new BossDefinition("dragon-overlord", "Dragon Overlord", "Only a guild that fights together will bring it down.", 16000, 4000, 72)
    };

    public static BossDefinition? Find(string key) => All.FirstOrDefault(b => b.Key == key);
}
