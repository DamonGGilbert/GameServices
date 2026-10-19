namespace GameServices.Api.Walking.Monsters;

/// <summary>
/// The walking game's repeatable dice, copied EXACTLY from the game (Core/DeterministicRandom.cs and
/// Core/SeedUtility.cs). Same seed in, same numbers out, on the phone and here - which is how the server and the game
/// agree on things worked out from a monster's seed, like its potential, without ever sending them back and forth.
///
/// If the game's copies ever change, this must change with them (MonsterDiceTests checks a few known answers).
/// </summary>
public sealed class DeterministicRandom
{
    private ulong state;

    public DeterministicRandom(long seed) => state = unchecked((ulong)seed);

    public DeterministicRandom(ulong seed) => state = seed;

    /// <summary>Next raw 64-bit value (SplitMix64).</summary>
    public ulong NextULong()
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    public long NextLong() => unchecked((long)NextULong());

    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public float NextFloat() => (float)NextDouble();

    public float Range(float min, float max) => min + (max - min) * NextFloat();
}

public static class SeedUtility
{
    public static long HashString(string text)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in text ?? "")
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return (long)hash;
        }
    }

    /// <summary>An independent child seed for a named "slice" of a seed (e.g. "potential").</summary>
    public static long Derive(long seed, string streamName)
    {
        unchecked
        {
            var rng = new DeterministicRandom((ulong)seed ^ (ulong)HashString(streamName));
            rng.NextULong();
            return rng.NextLong();
        }
    }
}

/// <summary>The game's potential rule (Monsters/CompanionTraining.cs), worked out the same way here.</summary>
public static class MonsterPotential
{
    public const int PotentialPerRarityTier = 9;

    /// <summary>Hidden potential 1-100: two dice averaged, plus 9 for each rarity tier (Legendary +36).</summary>
    public static int For(long seed, int rarityTier)
    {
        var rng = new DeterministicRandom(SeedUtility.Derive(seed, "potential"));
        float roll = (rng.Range(0f, 1f) + rng.Range(0f, 1f)) * 0.5f;
        int potential = 1 + (int)Math.Round(roll * 99f) + Math.Max(0, rarityTier) * PotentialPerRarityTier;
        return Math.Clamp(potential, 1, 100);
    }
}
