namespace GameServices.Api.Walking;

/// <summary>The result of checking one step report.</summary>
public readonly record struct StepVerdict(int AcceptedToday, int Added, int Rejected, bool Flagged);

/// <summary>
/// STEP ANTI-CHEAT. The walking game reports "today I've walked N steps" about once a minute. A hacked or broken
/// client could say anything, so the SERVER decides how many of those steps are believable:
///
///   * CADENCE: nobody walks more than about 200 steps a minute for long (that's a brisk jog). Since the last report
///     (or the start of the day) you can gain at most MaxStepsPerMinute x minutes, plus a little slack for clocks.
///   * DAILY CAP: at most MaxStepsPerDay in one day - plenty for a very long hike, impossible to farm.
///   * NO GOING BACKWARDS: a lower number than before (a phone restart, a duplicate) changes nothing.
///   * FINISHING YESTERDAY: yesterday's total may be reported again for the whole of today (steps walked late in the
///     evening with the game closed, or synced later from a watch). Each day only counts time INSIDE that day -
///     from that day's last report up to its midnight - so nothing can be added to yesterday that wasn't walked then.
///
/// Steps over the limit are not added (they are "rejected"); a big rejection is FLAGGED so it can be looked at.
/// Pure maths with no database, so it is easy to test.
/// </summary>
public static class StepRules
{
    public const int MaxStepsPerMinute = 200;
    public const int MaxStepsPerDay = 60_000;

    /// <summary>A little allowance for clock differences between the phone and the server.</summary>
    public const int SlackSteps = 150;

    /// <summary>Rejecting more than this in one report marks it as suspicious.</summary>
    public const int FlagThreshold = 1_000;

    /// <summary>Reports closer together than this are refused (429), so the server isn't flooded.</summary>
    public static readonly TimeSpan MinTimeBetweenReports = TimeSpan.FromSeconds(15);

    /// <summary>Time zones run from UTC-12 to UTC+14.</summary>
    public const int MaxUtcOffsetMinutes = 14 * 60;

    /// <summary>A day may still be reported for this long after its midnight (finishing yesterday).</summary>
    public static readonly TimeSpan LateReportGrace = TimeSpan.FromHours(24);

    /// <summary>
    /// Checks a report. previousToday = what we had already accepted for this day; lastReportUtc = when this day was
    /// last reported (an earlier time is fine - only time inside the day counts), or null; dayStartUtc = midnight of
    /// that day in the player's time zone, as UTC.
    /// </summary>
    public static StepVerdict Evaluate(int previousToday, DateTime? lastReportUtc, int reportedToday, DateTime nowUtc, DateTime dayStartUtc)
    {
        int previous = Math.Clamp(previousToday, 0, MaxStepsPerDay);

        if (reportedToday <= previous)
        {
            return new StepVerdict(previous, 0, 0, false);
        }

        int claimed = reportedToday - previous;

        // How long could they have been walking since we last heard about this day - but only within the day itself?
        DateTime windowStart = lastReportUtc.HasValue && lastReportUtc.Value > dayStartUtc ? lastReportUtc.Value : dayStartUtc;
        DateTime dayEndUtc = dayStartUtc.AddDays(1);
        DateTime windowEnd = nowUtc < dayEndUtc ? nowUtc : dayEndUtc;
        double minutes = Math.Max(0, (windowEnd - windowStart).TotalMinutes);
        long possible = (long)Math.Ceiling(minutes * MaxStepsPerMinute) + SlackSteps;

        int added = (int)Math.Min(Math.Min(claimed, possible), MaxStepsPerDay - previous);
        added = Math.Max(0, added);
        int rejected = claimed - added;

        return new StepVerdict(previous + added, added, rejected, rejected > FlagThreshold);
    }

    /// <summary>
    /// Midnight of a "yyyy-MM-dd" day in the player's time zone, expressed in UTC. Returns null if the day can't be
    /// read, the offset is impossible, or "now" isn't inside that day (allowing a short grace after midnight).
    /// </summary>
    public static DateTime? DayStartUtc(string? day, int utcOffsetMinutes, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(day) || Math.Abs(utcOffsetMinutes) > MaxUtcOffsetMinutes)
        {
            return null;
        }

        if (!DateTime.TryParseExact(day.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime localMidnight))
        {
            return null;
        }

        DateTime startUtc = DateTime.SpecifyKind(localMidnight.AddMinutes(-utcOffsetMinutes), DateTimeKind.Utc);
        DateTime endUtc = startUtc.AddDays(1) + LateReportGrace;

        return nowUtc >= startUtc && nowUtc < endUtc ? startUtc : null;
    }
}
