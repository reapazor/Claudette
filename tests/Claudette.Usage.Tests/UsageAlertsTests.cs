using Claudette.Usage.Tests.Support;

namespace Claudette.Usage.Tests;

public sealed class UsageAlertsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowA = Now.AddHours(2);
    private static readonly DateTimeOffset WindowB = WindowA.AddHours(5);

    private readonly UsageAlerts _alerts = new();

    [Fact]
    public void Warning_then_critical_fire_once_each_per_window()
    {
        Assert.Empty(Observe(50));

        var warning = Assert.Single(Observe(76));
        Assert.Equal(UsageAlertKind.ThresholdCrossed, warning.Kind);
        Assert.Equal("Session usage passed 75%", warning.Title);
        Assert.Equal("You've used 76% of your session limit. It resets in 2h 00m.", warning.Message);

        Assert.Empty(Observe(80));
        var critical = Assert.Single(Observe(91));
        Assert.Equal("Session usage passed 90%", critical.Title);

        Assert.Empty(Observe(95));
        Assert.Empty(Observe(70));
        Assert.Empty(Observe(80));
        Assert.Empty(Observe(92));
    }

    [Fact]
    public void Jumping_past_both_thresholds_is_one_alert()
    {
        Observe(10);

        var alert = Assert.Single(Observe(95));

        Assert.Equal("Session usage passed 90%", alert.Title);
        Assert.Empty(Observe(96));
    }

    [Fact]
    public void The_first_observation_after_a_restart_seeds_silently()
    {
        Assert.Empty(Observe(80));
        Assert.Empty(Observe(85));

        Assert.Single(Observe(91));
    }

    [Fact]
    public void Crossing_downward_does_not_alert()
    {
        Observe(95);

        Assert.Empty(Observe(80));
        Assert.Empty(Observe(60));
    }

    [Fact]
    public void A_new_window_resets_the_thresholds_and_reports_the_reset()
    {
        Observe(50);
        Observe(76);

        var reset = Assert.Single(Observe(5, WindowB));
        Assert.Equal(UsageAlertKind.LimitReset, reset.Kind);
        Assert.Equal("Session limit reset", reset.Title);
        Assert.Equal("Session usage is now 5%.", reset.Message);

        Assert.Equal(UsageAlertKind.ThresholdCrossed, Assert.Single(Observe(77, WindowB)).Kind);
    }

    [Fact]
    public void Reset_times_a_minute_apart_are_the_same_window()
    {
        // /usage shows 6:59am where get_usage says 07:00:00.
        Observe(50);
        Observe(76);

        Assert.Empty(Observe(60, WindowA.AddMinutes(1)));
        Assert.Empty(Observe(78, WindowA.AddMinutes(-1)));
    }

    [Fact]
    public void A_reset_needs_both_a_later_reset_time_and_a_drop()
    {
        Observe(50);

        Assert.Empty(Observe(40));
        Assert.DoesNotContain(Observe(60, WindowB), a => a.Kind == UsageAlertKind.LimitReset);
    }

    [Fact]
    public void A_model_limit_reset_is_reported()
    {
        var week = UsageFixtures.WeekResetsAt;
        _alerts.Observe(UsageFixtures.Snapshot(Now, 10, WindowA, weekly: 57, fable: 100), null, 75, 90);

        var alerts = _alerts.Observe(
            new UsageSnapshot(
                [
                    new LimitReading(LimitKind.Session, "Session", 10, WindowA, null, false),
                    new LimitReading(LimitKind.WeeklyAll, "Weekly", 57, week, null, false),
                    new LimitReading(LimitKind.WeeklyModel, "Fable", 0, week.AddDays(7), null, false),
                ],
                Now,
                UsageSource.GetUsage),
            null,
            75,
            90);

        var reset = Assert.Single(alerts);
        Assert.Equal(UsageAlertKind.LimitReset, reset.Kind);
        Assert.Equal("Fable weekly limit reset", reset.Title);
        Assert.Equal("Fable weekly usage is now 0%.", reset.Message);
    }

    [Fact]
    public void The_projection_alerts_once_per_window()
    {
        var hitting = new BurnProjection { CurrentPercent = 40, RatePerHour = 60, HitsLimitBeforeReset = true, TimeToLimit = TimeSpan.FromHours(1), MarginBeforeReset = TimeSpan.FromHours(1) };

        var alert = Assert.Single(Observe(40, projection: hitting));
        Assert.Equal(UsageAlertKind.ProjectedToHitLimit, alert.Kind);
        Assert.Equal("At this rate you'll hit the limit in 1h 00m, 1h 00m before it resets.", alert.Message);
        Assert.Empty(Observe(45, projection: hitting));
        Assert.Empty(Observe(46, projection: hitting with { HitsLimitBeforeReset = false }));
        Assert.Empty(Observe(47, projection: hitting));

        Assert.Contains(Observe(2, WindowB, hitting), a => a.Kind == UsageAlertKind.ProjectedToHitLimit);
    }

    [Fact]
    public void An_on_track_projection_does_not_alert()
    {
        var onTrack = new BurnProjection { CurrentPercent = 40, RatePerHour = 5, PercentAtReset = 50 };

        Assert.Empty(Observe(40, projection: onTrack));
    }

    [Fact]
    public void Custom_thresholds_are_used()
    {
        Observe(10);

        Assert.Equal("Session usage passed 50%", Assert.Single(_alerts.Observe(UsageFixtures.Snapshot(Now, 55, WindowA), null, 50, 80)).Title);
    }

    [Fact]
    public void A_snapshot_without_a_session_reading_is_fine()
    {
        var weeklyOnly = new UsageSnapshot([new LimitReading(LimitKind.WeeklyAll, "Weekly", 80, null, null, false)], Now, UsageSource.RateLimitEvent);

        Assert.Empty(_alerts.Observe(weeklyOnly, null, 75, 90));
        // The session's first appearance still seeds silently.
        Assert.Empty(Observe(80));
    }

    [Fact]
    public void The_message_rounds_the_percentage_as_the_meter_does()
    {
        Observe(10);

        Assert.Equal("You've used 91% of your session limit. It resets in 2h 00m.", Assert.Single(Observe(90.6)).Message);
    }

    [Fact]
    public void A_threshold_alert_refreshes_with_the_session_reading()
    {
        Observe(10);
        var alert = Assert.Single(Observe(91));

        var later = UsageAlerts.Refresh(alert, new LimitReading(LimitKind.Session, "Session", 94.2, WindowA, null, false), Now.AddMinutes(30));

        Assert.Equal("Session usage passed 90%", later.Title);
        Assert.Equal("You've used 94% of your session limit. It resets in 1h 30m.", later.Message);
        Assert.Equal(90, later.Threshold);
    }

    [Fact]
    public void Refresh_leaves_other_alerts_and_readings_below_the_threshold_alone()
    {
        Observe(10);
        var threshold = Assert.Single(Observe(91));
        var reset = Assert.Single(Observe(3, WindowB));
        var newWindow = new LimitReading(LimitKind.Session, "Session", 3, WindowB, null, false);

        Assert.Same(threshold, UsageAlerts.Refresh(threshold, newWindow, Now));
        Assert.Same(reset, UsageAlerts.Refresh(reset, newWindow, Now));
    }

    private IReadOnlyList<UsageAlert> Observe(double session, DateTimeOffset? resetsAt = null, BurnProjection? projection = null) =>
        _alerts.Observe(UsageFixtures.Snapshot(Now, session, resetsAt ?? WindowA), projection, 75, 90);
}
