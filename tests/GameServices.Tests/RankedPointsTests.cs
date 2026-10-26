using GameServices.Api.Walking.Ranked;

namespace GameServices.Tests;

/// <summary>Ranked Points: big, rare and upset wins are worth most; losses to stronger monsters cost little.</summary>
public class RankedPointsTests
{
    [Fact]
    public void An_even_win_against_a_common_is_worth_the_base()
    {
        // 10 base + 0 rarity + 1 per 1,000 BST (none at 900) + no upset.
        Assert.Equal(10, RankedPoints.WinPoints(1000, 900, 0, false));
    }

    [Fact]
    public void Rare_and_powerful_monsters_are_worth_more()
    {
        Assert.Equal(10 + 12 + 10, RankedPoints.WinPoints(10000, 10000, 4, false));   // a 10,000 BST Legendary
        Assert.Equal(10 + 9 + 5, RankedPoints.WinPoints(5000, 5000, 3, false));        // a 5,000 BST Epic
    }

    [Fact]
    public void Beating_a_stronger_monster_is_an_upset_bonus_up_to_ten()
    {
        Assert.Equal(10 + 1 + 4, RankedPoints.WinPoints(1000, 1100, 0, false));   // 10% higher: +4
        Assert.Equal(10 + 1 + 10, RankedPoints.WinPoints(1000, 1900, 0, false));  // 90% higher: capped at +10
    }

    [Fact]
    public void Shadows_count_half()
    {
        Assert.Equal((10 + 12 + 10 + 1) / 2, RankedPoints.WinPoints(10000, 10000, 4, true));
        Assert.Equal(RankedPoints.GentleLoss, RankedPoints.LossPoints(1000, 900, true));
    }

    [Fact]
    public void Losing_to_a_much_stronger_monster_costs_less()
    {
        Assert.Equal(RankedPoints.Loss, RankedPoints.LossPoints(1000, 1050, false));
        Assert.Equal(RankedPoints.GentleLoss, RankedPoints.LossPoints(1000, 1100, false));
    }

    [Fact]
    public void High_value_wins_are_upsets_or_epic_and_legendary_opponents()
    {
        Assert.True(RankedPoints.IsHighValue(1000, 1001, 0));
        Assert.True(RankedPoints.IsHighValue(1000, 500, 3));
        Assert.False(RankedPoints.IsHighValue(1000, 1000, 2));
    }

    [Fact]
    public void Defenders_gain_or_lose_half()
    {
        PointsChange win = RankedPoints.For(true, 1000, 0, 1000, 0, false);
        Assert.Equal(11, win.Attacker);
        Assert.Equal(-3, win.Defender);   // half of 5, rounded up

        PointsChange loss = RankedPoints.For(false, 1000, 0, 1000, 0, false);
        Assert.Equal(-5, loss.Attacker);
        Assert.Equal(6, loss.Defender);   // half of 11, rounded up

        PointsChange shadow = RankedPoints.For(true, 1000, 0, 1000, 0, true);
        Assert.Equal(0, shadow.Defender);
    }

    [Theory]
    [InlineData(0, RankedTier.Bronze)]
    [InlineData(99, RankedTier.Bronze)]
    [InlineData(100, RankedTier.Silver)]
    [InlineData(300, RankedTier.Gold)]
    [InlineData(700, RankedTier.Platinum)]
    [InlineData(1500, RankedTier.Diamond)]
    public void Tiers_come_from_points(int points, RankedTier tier) => Assert.Equal(tier, RankedPoints.TierFor(points));

    [Fact]
    public void Legend_is_the_world_top_100_of_Diamond()
    {
        Assert.Equal(RankedTier.Legend, RankedPoints.TierFor(1600, 100));
        Assert.Equal(RankedTier.Diamond, RankedPoints.TierFor(1600, 101));
        Assert.Equal(RankedTier.Gold, RankedPoints.TierFor(400, 1));   // first place, but not Diamond yet
    }

    [Fact]
    public void Seasons_are_months_and_start_from_a_quarter_of_last_season()
    {
        Assert.Equal("2026-10", RankedPoints.SeasonOf(new DateTime(2026, 10, 31, 23, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-09", RankedPoints.PreviousSeason("2026-10"));
        Assert.Equal("2025-12", RankedPoints.PreviousSeason("2026-01"));
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), RankedPoints.SeasonEndsUtc("2026-10"));
        Assert.Equal(250, RankedPoints.CarryOver(1000));
        Assert.Equal("October 2026", RankedPoints.SeasonName("2026-10"));
    }

    [Fact]
    public void A_monster_scores_its_BST_plus_100_a_win() => Assert.Equal(5000 + 700, RankedPoints.MonsterScore(5000, 7));
}
