using GameServices.Api.Walking.Ranked;

namespace GameServices.Api.Walking.Monsters;

public enum Clan { Fire, Water, Earth, Wind, Light, Darkness }

public enum ClanMatchup { Even, Strong, Weak, Rivals }

/// <summary>
/// THE SIX CLANS. A monster's clan comes from its seed, so nothing about it needs storing and the game always agrees:
/// more often than not its species' home clan, otherwise one of the other five. Same creature, six colours.
///
/// Every clan is strong against two others, weak against two, and level with the last, so no clan comes out on top:
///     Fire      beats  Wind, Darkness      loses to  Water, Light      level with Earth
///     Water     beats  Fire, Darkness      loses to  Earth, Light      level with Wind
///     Earth     beats  Water, Light        loses to  Wind, Darkness    level with Fire
///     Wind      beats  Earth, Light        loses to  Fire, Darkness    level with Water
///     Light     beats  Fire, Water         loses to  Earth, Wind       rivals of Darkness
///     Darkness  beats  Earth, Wind         loses to  Fire, Water       rivals of Light
///
/// In a battle (InBattle) the strong side gets +15% to every stat and the weak side -10%; Light and Darkness both get +10%
/// Power and Trickery. These stats are what's stored with the fight, so the phone replays it with the same numbers. On a
/// monster's card (Lean, used by MonsterStats) each clan's way of fighting moves its stats a little, whoever it's facing.
///
/// THE GAME HAS THE SAME RULES (Monsters/ElementClans.cs). Whole numbers only, and tested with the same numbers on both sides.
/// </summary>
public static class Clans
{
    public const int HomePercent = 55;
    public const int StrongPercent = 15;
    public const int WeakPercent = 10;
    public const int RivalPercent = 10;

    public static readonly Clan[] All = { Clan.Fire, Clan.Water, Clan.Earth, Clan.Wind, Clan.Light, Clan.Darkness };

    public static Clan Of(MonsterCatalog catalog, string speciesId, long seed) => Of(catalog.FindSpecies(speciesId)?.Element, seed);

    /// <param name="homeClan">Its species' home clan, by name (as the game exports it).</param>
    public static Clan Of(string? homeClan, long seed)
    {
        var rng = new DeterministicRandom(SeedUtility.Derive(seed, "clan"));
        int roll = rng.NextInt(0, 100);
        int other = rng.NextInt(0, 5);
        if (!Enum.TryParse(homeClan, out Clan home)) return All[roll % All.Length];
        if (roll < HomePercent) return home;

        List<Clan> others = All.Where(c => c != home).ToList();
        return others[other];
    }

    /// <summary>A clan from its exact name, or null: "Water" is one, and "3" and "water" aren't.</summary>
    public static Clan? Parse(string? name)
    {
        foreach (Clan clan in All)
        {
            if (clan.ToString() == name?.Trim()) return clan;
        }
        return null;
    }

    public static bool Beats(Clan a, Clan b) => a switch
    {
        Clan.Fire => b is Clan.Wind or Clan.Darkness,
        Clan.Water => b is Clan.Fire or Clan.Darkness,
        Clan.Earth => b is Clan.Water or Clan.Light,
        Clan.Wind => b is Clan.Earth or Clan.Light,
        Clan.Light => b is Clan.Fire or Clan.Water,
        Clan.Darkness => b is Clan.Earth or Clan.Wind,
        _ => false,
    };

    public static ClanMatchup Matchup(Clan mine, Clan theirs)
    {
        if (Beats(mine, theirs)) return ClanMatchup.Strong;
        if (Beats(theirs, mine)) return ClanMatchup.Weak;
        return (mine, theirs) is (Clan.Light, Clan.Darkness) or (Clan.Darkness, Clan.Light) ? ClanMatchup.Rivals : ClanMatchup.Even;
    }

    /// <summary>A monster's stats in a battle against this clan.</summary>
    public static DuelStats InBattle(DuelStats s, Clan mine, Clan theirs) => Matchup(mine, theirs) switch
    {
        ClanMatchup.Strong => new DuelStats(Percent(s.Power, 100 + StrongPercent), Percent(s.Guard, 100 + StrongPercent), Percent(s.Trickery, 100 + StrongPercent), Percent(s.Wits, 100 + StrongPercent)),
        ClanMatchup.Weak => new DuelStats(Percent(s.Power, 100 - WeakPercent), Percent(s.Guard, 100 - WeakPercent), Percent(s.Trickery, 100 - WeakPercent), Percent(s.Wits, 100 - WeakPercent)),
        ClanMatchup.Rivals => new DuelStats(Percent(s.Power, 100 + RivalPercent), s.Guard, Percent(s.Trickery, 100 + RivalPercent), s.Wits),
        _ => s,
    };

    /// <summary>The same, keeping its health (which battles don't use).</summary>
    public static MonsterBattleStats InBattle(MonsterBattleStats s, Clan mine, Clan theirs)
    {
        DuelStats d = InBattle(DuelStats.From(s), mine, theirs);
        return new MonsterBattleStats(d.Power, d.Guard, d.Trickery, d.Wits, s.Health);
    }

    public static int LeanPower(int value, Clan clan) => Percent(value, 100 + clan switch { Clan.Fire => 15, Clan.Water => -5, Clan.Light => -5, Clan.Darkness => 5, _ => 0 });
    public static int LeanGuard(int value, Clan clan) => Percent(value, 100 + clan switch { Clan.Earth => 15, Clan.Light => 10, Clan.Fire => -5, Clan.Darkness => -5, _ => 0 });
    public static int LeanTricks(int value, Clan clan) => Percent(value, 100 + clan switch { Clan.Wind => 15, Clan.Darkness => 10, Clan.Earth => -5, _ => 0 });
    public static int LeanWits(int value, Clan clan) => Percent(value, 100 + clan switch { Clan.Water => 15, Clan.Light => 5, Clan.Wind => -5, _ => 0 });

    // Whole numbers only, rounding halves up (floats round differently from one machine to the next).
    private static int Percent(int value, int percent) => Math.Max(1, (int)((value * (long)percent + 50) / 100));
}
