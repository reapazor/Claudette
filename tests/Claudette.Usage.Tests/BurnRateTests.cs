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
