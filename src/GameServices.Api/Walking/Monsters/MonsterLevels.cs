using GameServices.Api.Data;

namespace GameServices.Api.Walking.Monsters;

/// <summary>
/// HOW MONSTERS LEVEL UP. The server hands out experience itself: for battles (Walking/Battles) and for steps with their
/// walker. A monster takes on at most MaxXpPerDay a day - more than a very keen walker earns (25 battle wins and 20,000
/// steps together come to about 7,750), and it stops anyone running up a monster's level with a script.
///
/// The phone has one claim of its own: progress a starter made before its walker signed in, once, within the same limit.
///
/// The curve must match the game's ProgressionConfig (monsterXpBase 40, monsterXpExponent 1.25, monsterMaxLevel 100).
/// </summary>
public static class MonsterLevels
{
    public const int MaxLevel = 100;
    public const int MaxXpPerDay = 8000;

    private const double XpBase = 40;
    private const double XpExponent = 1.25;

    /// <summary>Experience needed to go from this level to the next.</summary>
    public static int XpForLevel(int level) => Math.Max(1, (int)Math.Round(XpBase * Math.Pow(level, XpExponent)));

    /// <summary>All the experience it takes to be at this level with this much towards the next.</summary>
    public static long TotalXp(int level, int experience)
    {
        long total = Math.Max(0, experience);
        for (int l = 1; l < Math.Clamp(level, 1, MaxLevel); l++) total += XpForLevel(l);
        return total;
    }

    public static (int Level, int Experience) FromTotal(long total)
    {
        int level = 1;
        while (level < MaxLevel && total >= XpForLevel(level))
        {
            total -= XpForLevel(level);
            level++;
        }

        return (level, level >= MaxLevel ? 0 : (int)total);
    }

    /// <summary>Gives a monster experience, as far as today's limit allows. Returns how much it took.</summary>
    public static int Grant(WalkingMonster monster, long experience, string today)
    {
        if (experience <= 0) return 0;

        if (monster.XpDay != today)
        {
            monster.XpDay = today;
            monster.XpToday = 0;
        }

        int gain = (int)Math.Min(experience, Math.Max(0, MaxXpPerDay - monster.XpToday));
        if (gain <= 0) return 0;

        monster.XpToday += gain;
        (monster.Level, monster.Experience) = FromTotal(TotalXp(monster.Level, monster.Experience) + gain);
        return gain;
    }

    /// <summary>A starter's progress from before signing in: taken once, up to today's limit, and never lowers a level.</summary>
    public static void TakeGuestProgress(WalkingMonster monster, int claimedLevel, int claimedExperience, string today) =>
        Grant(monster, TotalXp(claimedLevel, claimedExperience) - TotalXp(monster.Level, monster.Experience), today);
}
