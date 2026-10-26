using GameServices.Api.Walking.Monsters;
using GameServices.Api.Walking.Ranked;

namespace GameServices.Tests;

/// <summary>
/// The server's copy of the duel (Walking/Ranked/DuelRules.cs) must give EXACTLY the game's results. The expected
/// numbers were worked out by the game's own Battles/DuelRules.cs.
/// </summary>
public class DuelRulesTests
{
    [Theory]
    [InlineData(1L, 84, 45, 81, 55, DuelStrategy.MaxPower, 40, 35, 40, 37, DuelStrategy.Balanced, "120,36,65,44", 378, 156, 0.7078652f, 0.53996867f, true)]
    [InlineData(42L, 2500, 1900, 2300, 2100, DuelStrategy.MaxTrickery, 2400, 2200, 2000, 2050, DuelStrategy.MaxGuard, "2000,1520,3600,1680", 10760, 9050, 0.54316f, 0.83232605f, false)]
    [InlineData(-7L, 300, 300, 300, 300, DuelStrategy.Balanced, 300, 300, 300, 300, DuelStrategy.Balanced, "300,300,300,300", 1200, 1200, 0.5f, 0.7497977f, false)]
    [InlineData(123456789012345L, 9000, 500, 100, 100, DuelStrategy.MaxPower, 100, 100, 100, 9000, DuelStrategy.MaxWits, "9140,400,80,80", 18760, 9300, 0.66856736f, 0.06703159f, true)]
    [InlineData(long.MaxValue, 1234, 987, 1500, 1111, DuelStrategy.MaxWits, 1300, 1000, 1400, 1200, DuelStrategy.MaxPower, "987,790,1200,1855", 5259, 6130, 0.46176136f, 0.33737376f, true)]
    public void A_ranked_duel_matches_the_game_exactly(long seed, int ap, int ag, int at, int aw, DuelStrategy aStrategy,
        int bp, int bg, int bt, int bw, DuelStrategy bStrategy, string after, int score, int opponentScore, float chance, float roll, bool won)
    {
        DuelOutcome o = DuelRules.ResolveRanked(seed, new DuelStats(ap, ag, at, aw), aStrategy, new DuelStats(bp, bg, bt, bw), bStrategy);

        Assert.Equal(after, $"{o.Player.After.Power},{o.Player.After.Guard},{o.Player.After.Trickery},{o.Player.After.Wits}");
        Assert.Equal(score, o.Player.Score);
        Assert.Equal(opponentScore, o.Opponent.Score);
        Assert.Equal(chance, o.Player.Chance);
        Assert.Equal(roll, o.Roll);
        Assert.Equal(won, o.PlayerWon);
    }

    [Theory]
    [InlineData(DuelStyle.Aggressive, "MaxPower,MaxPower,MaxPower")]
    [InlineData(DuelStyle.Defensive, "MaxGuard,MaxGuard,MaxGuard")]
    [InlineData(DuelStyle.Balanced, "MaxTrickery,MaxTrickery,Balanced")]
    [InlineData(DuelStyle.Random, "Balanced,Balanced,MaxTrickery")]
    public void A_computer_fighter_picks_like_the_game(DuelStyle style, string expected)
    {
        var picks = new List<string>();
        for (long seed = 1; seed <= 3; seed++)
        {
            picks.Add(DuelRules.PickForComputer(style, new DuelStats(500, 300, 450, 320), new DuelStats(400, 420, 380, 300), new DeterministicRandom(seed)).ToString());
        }

        Assert.Equal(expected, string.Join(",", picks));
    }

    [Fact]
    public void The_favourite_never_has_more_than_95_percent()
    {
        DuelOutcome o = DuelRules.ResolveRanked(5, new DuelStats(10000, 10000, 10000, 10000), DuelStrategy.Balanced, new DuelStats(10, 10, 10, 10), DuelStrategy.Balanced);
        Assert.Equal(DuelRules.MaxChance, o.Player.Chance);
    }
}
