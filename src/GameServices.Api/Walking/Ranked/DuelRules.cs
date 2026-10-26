using GameServices.Api.Walking.Monsters;

namespace GameServices.Api.Walking.Ranked;

public enum DuelStrategy { Balanced = 0, MaxPower = 1, MaxGuard = 2, MaxTrickery = 3, MaxWits = 4 }

/// <summary>How a computer-run fighter (an offline defender's plan, or a shadow monster) picks its strategy.</summary>
public enum DuelStyle { Aggressive = 0, Defensive = 1, Balanced = 2, Random = 3 }

public readonly record struct DuelStats(int Power, int Guard, int Trickery, int Wits)
{
    public int Total => Power + Guard + Trickery + Wits;

    public static DuelStats From(MonsterBattleStats s) => new(s.Power, s.Guard, s.Tricks, s.Wits);
}

/// <summary>One side of a fight, worked out.</summary>
public sealed class DuelSide
{
    public DuelStats Before { get; init; }
    public DuelStats After { get; init; }
    public DuelStrategy Strategy { get; init; }
    public int PowerAdvantage { get; set; }
    public int TrickeryAdvantage { get; set; }
    public int WalkingBonus { get; set; }
    public int ArenaBonus { get; set; }
    public int Score { get; set; }
    public float Chance { get; set; }
}

public sealed class DuelOutcome
{
    public required DuelSide Player { get; init; }
    public required DuelSide Opponent { get; init; }
    public float Roll { get; init; }
    public bool PlayerWon { get; init; }
}

/// <summary>
/// THE DUEL, ON THE SERVER: copied EXACTLY from the game (Battles/DuelRules.cs), so a ranked result worked out here
/// is the one the phone shows. Checked against results the game itself worked out (DuelRulesTests).
///
/// Strategy moves 20% of the other stats into one; Power beats Guard and Trickery beats Wits (the difference is
/// added to the score); the chance is score / both scores, kept between 5% and 95%; one roll from the seed decides.
/// Ranked passes no tonic, no walking pips and no arena boost (fair for an offline defender), but the rules keep them
/// so they stay identical to the game's.
/// </summary>
public static class DuelRules
{
    public const float StrategyShift = 0.20f;
    public const float TonicBoost = 0.15f;
    public const int StepsPerPip = 1000;
    public const int MaxPips = 10;
    public const float PipBonus = 0.02f;
    public const float ArenaBoost = 0.08f;
    public const float MinChance = 0.05f;
    public const float MaxChance = 0.95f;

    public static int PipsFor(int stepsToday) => Math.Max(0, Math.Min(MaxPips, stepsToday / StepsPerPip));

    public static DuelStats ApplyStrategy(DuelStats s, DuelStrategy strategy, bool tonic)
    {
        if (tonic)
        {
            s = new DuelStats(Boost(s.Power), Boost(s.Guard), Boost(s.Trickery), Boost(s.Wits));
        }

        if (strategy == DuelStrategy.Balanced)
        {
            return s;
        }

        int Take(int value) => (int)Math.Round(value * StrategyShift);

        int p = strategy == DuelStrategy.MaxPower ? 0 : Take(s.Power);
        int g = strategy == DuelStrategy.MaxGuard ? 0 : Take(s.Guard);
        int t = strategy == DuelStrategy.MaxTrickery ? 0 : Take(s.Trickery);
        int w = strategy == DuelStrategy.MaxWits ? 0 : Take(s.Wits);
        int pot = p + g + t + w;

        return new DuelStats(
            s.Power - p + (strategy == DuelStrategy.MaxPower ? pot : 0),
            s.Guard - g + (strategy == DuelStrategy.MaxGuard ? pot : 0),
            s.Trickery - t + (strategy == DuelStrategy.MaxTrickery ? pot : 0),
            s.Wits - w + (strategy == DuelStrategy.MaxWits ? pot : 0));
    }

    private static int Boost(int value) => (int)Math.Round(value * (1f + TonicBoost));

    public static DuelOutcome Resolve(long seed,
        DuelStats playerStats, DuelStrategy playerStrategy, bool playerTonic, int playerSteps, bool playerArena,
        DuelStats opponentStats, DuelStrategy opponentStrategy, bool opponentTonic, int opponentSteps, bool opponentArena)
    {
        DuelSide me = Fill(playerStats, playerStrategy, playerTonic);
        DuelSide them = Fill(opponentStats, opponentStrategy, opponentTonic);

        me.PowerAdvantage = Math.Max(0, me.After.Power - them.After.Guard);
        me.TrickeryAdvantage = Math.Max(0, me.After.Trickery - them.After.Wits);
        them.PowerAdvantage = Math.Max(0, them.After.Power - me.After.Guard);
        them.TrickeryAdvantage = Math.Max(0, them.After.Trickery - me.After.Wits);

        Score(me, PipsFor(playerSteps), playerArena);
        Score(them, PipsFor(opponentSteps), opponentArena);

        float chance = (me.Score + them.Score) > 0 ? me.Score / (float)(me.Score + them.Score) : 0.5f;
        me.Chance = Math.Max(MinChance, Math.Min(MaxChance, chance));
        them.Chance = 1f - me.Chance;

        float roll = new DeterministicRandom(SeedUtility.Derive(seed, "spin")).NextFloat();
        return new DuelOutcome { Player = me, Opponent = them, Roll = roll, PlayerWon = roll < me.Chance };
    }

    /// <summary>A ranked fight: no tonics, no walking bonus, no arena boost.</summary>
    public static DuelOutcome ResolveRanked(long seed, DuelStats attacker, DuelStrategy attackerStrategy, DuelStats defender, DuelStrategy defenderStrategy) =>
        Resolve(seed, attacker, attackerStrategy, false, 0, false, defender, defenderStrategy, false, 0, false);

    private static DuelSide Fill(DuelStats stats, DuelStrategy strategy, bool tonic) =>
        new() { Before = stats, Strategy = strategy, After = ApplyStrategy(stats, strategy, tonic) };

    private static void Score(DuelSide side, int pips, bool arena)
    {
        int baseScore = side.After.Total + side.PowerAdvantage + side.TrickeryAdvantage;
        side.WalkingBonus = (int)Math.Round(baseScore * pips * PipBonus);
        side.ArenaBonus = arena ? (int)Math.Round(baseScore * ArenaBoost) : 0;
        side.Score = baseScore + side.WalkingBonus + side.ArenaBonus;
    }

    /// <summary>How a computer-run fighter picks: by its style, with a little surprise, and a look at the other monster.</summary>
    public static DuelStrategy PickForComputer(DuelStyle style, DuelStats mine, DuelStats theirs, DeterministicRandom rng)
    {
        switch (style)
        {
            case DuelStyle.Aggressive:
                return rng.Chance(0.7f) ? DuelStrategy.MaxPower : DuelStrategy.MaxTrickery;
            case DuelStyle.Defensive:
                return rng.Chance(0.7f) ? DuelStrategy.MaxGuard : DuelStrategy.MaxWits;
            case DuelStyle.Random:
                return (DuelStrategy)rng.NextInt(0, 5);
            default:
                if (rng.Chance(0.3f)) return DuelStrategy.Balanced;
                int powerGap = mine.Power - theirs.Guard;
                int trickGap = mine.Trickery - theirs.Wits;
                return powerGap >= trickGap ? DuelStrategy.MaxPower : DuelStrategy.MaxTrickery;
        }
    }
}
