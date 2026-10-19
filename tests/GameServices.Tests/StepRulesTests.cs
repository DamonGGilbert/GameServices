using GameServices.Api.Walking;

namespace GameServices.Tests;

/// <summary>
/// STEP ANTI-CHEAT, as plain maths: no server, no database. These pin down exactly what the server will believe.
/// </summary>
public class StepRulesTests
{
    private static readonly DateTime Midnight = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_normal_walk_is_counted_in_full()
    {
        // Two hours into the day, 3,000 steps: easily possible.
        StepVerdict v = StepRules.Evaluate(0, null, 3000, Midnight.AddHours(2), Midnight);

        Assert.Equal(3000, v.AcceptedToday);
        Assert.Equal(3000, v.Added);
        Assert.Equal(0, v.Rejected);
        Assert.False(v.Flagged);
    }

    [Fact]
    public void An_impossible_jump_is_clipped_to_what_anyone_could_walk_and_flagged()
    {
        // 10 minutes after the last report, the phone claims 20,000 more steps. At most 200 a minute (+ slack) count.
        DateTime last = Midnight.AddHours(9);
        StepVerdict v = StepRules.Evaluate(5000, last, 25000, last.AddMinutes(10), Midnight);

        int possible = (10 * StepRules.MaxStepsPerMinute) + StepRules.SlackSteps;
        Assert.Equal(possible, v.Added);
        Assert.Equal(5000 + possible, v.AcceptedToday);
        Assert.Equal(20000 - possible, v.Rejected);
        Assert.True(v.Flagged);
    }

    [Fact]
    public void A_lower_number_than_before_changes_nothing()
    {
        // A phone restart or a duplicate report: never lose steps, never add any.
        StepVerdict v = StepRules.Evaluate(4000, Midnight.AddHours(5), 1200, Midnight.AddHours(6), Midnight);

        Assert.Equal(4000, v.AcceptedToday);
        Assert.Equal(0, v.Added);
        Assert.Equal(0, v.Rejected);
    }

    [Fact]
    public void Nobody_can_pass_the_daily_cap()
    {
        StepVerdict v = StepRules.Evaluate(StepRules.MaxStepsPerDay - 100, Midnight.AddHours(20), StepRules.MaxStepsPerDay + 5000, Midnight.AddHours(23), Midnight);

        Assert.Equal(StepRules.MaxStepsPerDay, v.AcceptedToday);
        Assert.Equal(100, v.Added);
    }

    [Fact]
    public void The_first_report_of_the_day_is_limited_by_how_long_the_day_has_lasted()
    {
        // 30 minutes after midnight nobody has walked 50,000 steps today - even if yesterday's last report was hours ago.
        StepVerdict v = StepRules.Evaluate(0, Midnight.AddHours(-5), 50000, Midnight.AddMinutes(30), Midnight);

        Assert.Equal((30 * StepRules.MaxStepsPerMinute) + StepRules.SlackSteps, v.Added);
    }

    [Fact]
    public void Finishing_yesterday_only_counts_time_before_its_midnight()
    {
        // Yesterday was last reported at 23:50. Reported again at 09:00 the next morning, only those last 10 minutes
        // of yesterday can hold new steps - not the 9 hours since.
        DateTime last = Midnight.AddHours(23).AddMinutes(50);
        StepVerdict v = StepRules.Evaluate(7000, last, 30000, Midnight.AddDays(1).AddHours(9), Midnight);

        Assert.Equal((10 * StepRules.MaxStepsPerMinute) + StepRules.SlackSteps, v.Added);
        Assert.True(v.Flagged);
    }

    [Fact]
    public void Only_today_in_the_players_own_time_zone_is_accepted()
    {
        DateTime now = new(2026, 10, 16, 23, 30, 0, DateTimeKind.Utc);

        // UK in summer (UTC+1): it's already the 17th there.
        Assert.NotNull(StepRules.DayStartUtc("2026-10-17", 60, now));
        Assert.Null(StepRules.DayStartUtc("2026-10-15", 60, now));

        // The 16th, a little after midnight: still allowed (yesterday can be finished all through today).
        Assert.NotNull(StepRules.DayStartUtc("2026-10-16", 60, now));

        // ...but not the day after that.
        Assert.Null(StepRules.DayStartUtc("2026-10-16", 60, now.AddDays(1)));

        Assert.Null(StepRules.DayStartUtc("16/10/2026", 0, now));
        Assert.Null(StepRules.DayStartUtc("2026-10-16", 20 * 60, now)); // no time zone is UTC+20
    }
}
