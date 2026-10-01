namespace Claudette.Usage.Tests;

public sealed class BurnRateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fast_burn_hits_the_limit_before_the_reset()
    {
        // 60% an hour from 35%: the limit in 65 minutes, 69 minutes before the reset.
        var projection = BurnRate.Project(Points((-30, 5), (-15, 20)), 35, Now + new TimeSpan(2, 14, 0), Now);

        Assert.Equal(60, projection.RatePerHour!.Value, 6);
        Assert.False(projection.IsIdle);
        Assert.True(projection.HitsLimitBeforeReset);
        Assert.Equal(65, (projection.LimitAt!.Value - Now).TotalMinutes, 6);
        Assert.Equal(69, projection.MarginBeforeReset!.Value.TotalMinutes, 6);
        Assert.Equal(100, projection.PercentAtReset);
        Assert.Equal("At this rate you'll hit the limit in 1h 05m, 1h 09m before it resets.", projection.Describe());
    }

    [Fact]
    public void Another_machines_lagging_reading_doesnt_look_like_a_reset()
    {
        // 70 and 72 here, then another machine's cached 64 for the same window, then 74: taken as they were, the 64
        // reads as a reset and the slope from it to 75 is about 130% an hour.
        var resets = Now.AddHours(2);
        UsageSample Sample(int minutes, double percent, DateTimeOffset? at = null) => new(Now.AddMinutes(minutes), percent, at ?? resets, null, null, []);
        var history = BurnRate.SessionHistory([Sample(-24, 70), Sample(-18, 72), Sample(-12, 64), Sample(-6, 74)]);

        Assert.Equal([70, 72, 72, 74], history.Select(p => p.Percent));
        var projection = BurnRate.Project(history, 75, resets, Now);
        Assert.InRange(projection.RatePerHour!.Value, 10, 15);
        Assert.False(projection.HitsLimitBeforeReset);
    }

    [Fact]
    public void A_new_window_starts_the_history_again_and_a_reading_for_an_old_one_is_left_out()
    {
        var first = Now.AddMinutes(-10);
        var second = Now.AddHours(5);
        UsageSample Sample(int minutes, double percent, DateTimeOffset at) => new(Now.AddMinutes(minutes), percent, at, null, null, []);

        var history = BurnRate.SessionHistory([Sample(-30, 90, first), Sample(-8, 2, second), Sample(-6, 91, first), Sample(-4, 4, second)]);

        Assert.Equal([90, 2, 4], history.Select(p => p.Percent));
    }

    [Fact]
    public void A_slow_burn_is_on_track()
    {
        // 10% an hour from 40%, 3 hours before the reset.
        var projection = BurnRate.Project(Points((-30, 35), (-15, 37.5)), 40, Now.AddHours(3), Now);

        Assert.False(projection.HitsLimitBeforeReset);
        Assert.Null(projection.MarginBeforeReset);
        Assert.Equal(70, projection.PercentAtReset!.Value, 6);
        Assert.Equal(Now.AddHours(6), projection.LimitAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal("On track: about 70% used when the session resets.", projection.Describe());
    }

    [Fact]
    public void No_increase_in_the_window_is_idle()
    {
        var projection = BurnRate.Project(Points((-30, 40), (-10, 40)), 40, Now.AddHours(2), Now);

        Assert.True(projection.IsIdle);
        Assert.Null(projection.LimitAt);
        Assert.False(projection.HitsLimitBeforeReset);
        Assert.Equal(40, projection.PercentAtReset);
        Assert.Equal("No recent usage.", projection.Describe());
    }

    [Fact]
    public void Jitter_between_sources_is_still_idle()
    {
        // get_usage and rate_limit_event can round the same usage differently.
        var projection = BurnRate.Project(Points((-20, 13), (-15, 12)), 13, Now.AddHours(2), Now);

        Assert.True(projection.RatePerHour > BurnRate.IdleRatePerHour);
        Assert.True(projection.IsIdle);
        Assert.Equal("No recent usage.", projection.Describe());
    }

    [Fact]
    public void Too_little_history_is_not_enough_data()
    {
        var none = BurnRate.Project([], 30, Now.AddHours(2), Now);
        var tooShort = BurnRate.Project(Points((-1, 29)), 30, Now.AddHours(2), Now);

        Assert.Null(none.RatePerHour);
        Assert.Null(tooShort.RatePerHour);
        Assert.False(tooShort.IsIdle);
        Assert.Null(tooShort.LimitAt);
        Assert.Equal("Not enough data yet.", none.Describe());
        Assert.Equal("Not enough data yet.", tooShort.Describe());
    }

    [Fact]
    public void Only_points_within_the_rate_window_count()
    {
        var history = Points((-45, 0), (-20, 30), (-10, 35));

        var recent = BurnRate.Project(history, 40, Now.AddHours(4), Now);
        var hour = BurnRate.Project(history, 40, Now.AddHours(4), Now, TimeSpan.FromHours(1));

        Assert.Equal(30, recent.RatePerHour!.Value, 6);
        Assert.True(hour.RatePerHour > 40);
    }

    [Fact]
    public void Only_points_in_the_current_session_window_count()
    {
        // The window started 10 minutes ago (it resets in 4h 50m).
        var projection = BurnRate.Project(Points((-20, 0.5), (-5, 1)), 3, Now + new TimeSpan(4, 50, 0), Now);

        Assert.Equal(24, projection.RatePerHour!.Value, 6);
    }

    [Fact]
    public void Points_before_a_reset_are_dropped_even_without_a_reset_time()
    {
        var projection = BurnRate.Project(Points((-25, 90), (-10, 2)), 5, null, Now);

        Assert.Equal(18, projection.RatePerHour!.Value, 6);
    }

    [Fact]
    public void Without_a_reset_time_the_projection_only_says_when_the_limit_is_hit()
    {
        var projection = BurnRate.Project(Points((-30, 5), (-15, 20)), 35, null, Now);

        Assert.False(projection.HitsLimitBeforeReset);
        Assert.Null(projection.PercentAtReset);
        Assert.Equal("At this rate you'll hit the limit in 1h 05m.", projection.Describe());
    }

    [Fact]
    public void At_the_limit_it_says_so()
    {
        var projection = BurnRate.Project(Points((-30, 90), (-15, 95)), 100, Now.AddHours(1), Now);

        Assert.True(projection.HitsLimitBeforeReset);
        Assert.Equal(Now, projection.LimitAt);
        Assert.Equal(TimeSpan.FromHours(1), projection.MarginBeforeReset);
        Assert.Equal("You've hit the limit.", projection.Describe());
    }

    // ---- The detailed header (DESIGN.md §6, "Detailed header") ----------------------------------------------------

    [Fact]
    public void The_projection_says_when_it_crosses_the_critical_threshold()
    {
        // 60% an hour from 35%: 90% in 55 minutes, 1h 19m before the reset.
        var reset = Now + new TimeSpan(2, 14, 0);
        var projection = BurnRate.Project(Points((-30, 5), (-15, 20)), 35, reset, Now);

        var crossing = BurnRate.FirstCrossing(projection, reset, Now, criticalPercent: 90)!;

        Assert.Equal(90, crossing.Level);
        Assert.Equal(Now.AddMinutes(55), crossing.At, TimeSpan.FromSeconds(1));
        Assert.Equal(79, crossing.BeforeReset.TotalMinutes, 3);
        Assert.Equal($"Hits 90% at {crossing.At.ToLocalTime():t}, 1h 19m before it resets.", crossing.Describe());
    }

    [Fact]
    public void Past_the_critical_threshold_the_crossing_is_the_limit()
    {
        // 20% an hour from 92%: the limit in 24 minutes.
        var reset = Now.AddHours(1);
        var projection = BurnRate.Project(Points((-30, 82), (-15, 87)), 92, reset, Now);

        var crossing = BurnRate.FirstCrossing(projection, reset, Now, criticalPercent: 90)!;

        Assert.Equal(100, crossing.Level);
        Assert.Equal(Now.AddMinutes(24), crossing.At, TimeSpan.FromSeconds(1));
        Assert.StartsWith("Hits the limit at ", crossing.Describe(), StringComparison.Ordinal);
        Assert.EndsWith(", 36m before it resets.", crossing.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_crossing_after_the_reset_or_without_a_rate_is_none()
    {
        var reset = Now.AddHours(3);
        var slow = BurnRate.Project(Points((-30, 35), (-15, 37.5)), 40, reset, Now);
        var idle = BurnRate.Project(Points((-30, 40), (-10, 40)), 40, reset, Now);
        var unknown = BurnRate.Project([], 40, reset, Now);
        var atLimit = BurnRate.Project(Points((-30, 90), (-15, 95)), 100, reset, Now);

        // 10% an hour from 40% is at 70% by the reset: it crosses a threshold of 60%, but not 90%.
        Assert.Null(BurnRate.FirstCrossing(slow, reset, Now, criticalPercent: 90));
        Assert.NotNull(BurnRate.FirstCrossing(slow, reset, Now, criticalPercent: 60));
        Assert.Null(BurnRate.FirstCrossing(idle, reset, Now, 90));
        Assert.Null(BurnRate.FirstCrossing(unknown, reset, Now, 90));
        Assert.Null(BurnRate.FirstCrossing(atLimit, reset, Now, 90));
        Assert.Null(BurnRate.FirstCrossing(slow, null, Now, 90));
    }

    [Fact]
    public void The_week_is_projected_at_its_average_pace()
    {
        // 30% in the first 3 days of the week: 10% a day, so 70% when it resets 4 days from now.
        var start = Now.AddDays(-3);
        var projection = BurnRate.ProjectAverage(30, start, Now.AddDays(4), Now);

        Assert.Equal(10.0 / 24, projection.RatePerHour!.Value, 6);
        Assert.False(projection.HitsLimitBeforeReset);
        Assert.Equal(70, projection.PercentAtReset!.Value, 6);
        Assert.Equal(Now.AddDays(7), projection.LimitAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void A_fast_week_hits_its_limit_before_it_resets()
    {
        var projection = BurnRate.ProjectAverage(60, Now.AddDays(-3), Now.AddDays(4), Now);

        Assert.True(projection.HitsLimitBeforeReset);
        Assert.Equal(Now.AddDays(2), projection.LimitAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(2, projection.MarginBeforeReset!.Value.TotalDays, 6);
        Assert.Equal(100, projection.PercentAtReset);
    }

    [Fact]
    public void An_unused_or_just_started_week_has_no_pace()
    {
        var unused = BurnRate.ProjectAverage(0, Now.AddDays(-2), Now.AddDays(5), Now);
        var justStarted = BurnRate.ProjectAverage(5, Now.AddMinutes(-1), Now.AddDays(7), Now);

        Assert.True(unused.IsIdle);
        Assert.Equal(0, unused.PercentAtReset);
        Assert.Null(justStarted.RatePerHour);
        Assert.Null(justStarted.LimitAt);
    }

    [Theory]
    [InlineData(0, UsageLevel.Normal)]
    [InlineData(74.9, UsageLevel.Normal)]
    [InlineData(75, UsageLevel.Warning)]
    [InlineData(89.99, UsageLevel.Warning)]
    [InlineData(90, UsageLevel.Critical)]
    [InlineData(100, UsageLevel.Critical)]
    public void Level_uses_the_default_thresholds(double percent, UsageLevel expected)
    {
        Assert.Equal(expected, BurnRate.Level(percent));
    }

    [Fact]
    public void Level_uses_custom_thresholds()
    {
        Assert.Equal(UsageLevel.Warning, BurnRate.Level(50, warn: 50, critical: 80));
        Assert.Equal(UsageLevel.Critical, BurnRate.Level(80, warn: 50, critical: 80));
        Assert.Equal(UsageLevel.Normal, BurnRate.Level(49, warn: 50, critical: 80));
    }

    [Theory]
    [InlineData(-120, "<1m")]
    [InlineData(0, "<1m")]
    [InlineData(59, "<1m")]
    [InlineData(59.6, "1m")]
    [InlineData(60, "1m")]
    [InlineData(12 * 60 + 59, "12m")]
    [InlineData(65 * 60, "1h 05m")]
    [InlineData(65 * 60 - 0.4, "1h 05m")]
    [InlineData(134 * 60, "2h 14m")]
    [InlineData(10 * 3600, "10h 00m")]
    [InlineData((76 * 3600) + (30 * 60), "3d 4h")]
    public void Durations_read_like_a_countdown(double seconds, string expected)
    {
        Assert.Equal(expected, BurnRate.FormatCountdown(TimeSpan.FromSeconds(seconds)));
    }

    private static UsagePoint[] Points(params (double Minutes, double Percent)[] points) =>
        [.. points.Select(p => new UsagePoint(Now.AddMinutes(p.Minutes), p.Percent))];
}
