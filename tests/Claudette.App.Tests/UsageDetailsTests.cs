using System.Text.Json.Nodes;
using Claudette.App.Controls;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Protocol;
using Claudette.Usage;

namespace Claudette.App.Tests;

/// <summary>
/// The detailed usage header (DESIGN.md §6, "Detailed header"): the session and weekly charts from the usage history,
/// the thresholds, the "hits 90% at…" line, the burn rate, the busiest tabs and the remembered toggle.
/// </summary>
public class UsageDetailsTests
{
    [Fact]
    public async Task The_charts_show_the_session_and_the_week_from_the_usage_history()
    {
        await using var h = new TabTestHarness();
        var store = new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time);
        var weekReset = h.Time.GetUtcNow().AddDays(3);
        var sessionReset = h.Time.GetUtcNow().AddHours(8);
        // An earlier session, then samples every 20 minutes for the one that ends at sessionReset.
        Add(store, h, session: 70, sessionReset.AddHours(-5), weekly: 10, weekReset, fable: 3);
        h.Time.Advance(TimeSpan.FromHours(3) + TimeSpan.FromMinutes(10));
        for (var i = 0; i < 6; i++)
        {
            Add(store, h, session: 5 + (i * 2), sessionReset, weekly: 11 + i, weekReset, fable: 4);
            h.Time.Advance(TimeSpan.FromMinutes(20));
        }
        await using var tracker = new UsageTracker(h.Services, store);
        using var header = new UsageViewModel(h.Services, tracker);
        var now = h.Time.GetUtcNow();

        header.ToggleDetailsCommand.Execute(null);
        await TabTestHarness.Eventually(() => header.WeekPoints.Count == 8, "the week from the history");

        Assert.True(header.ShowDetails);
        // The session chart runs from the window's start to its reset, without the earlier session.
        Assert.Equal(sessionReset.AddHours(-5), header.WindowStart);
        Assert.Equal(sessionReset, header.WindowEnd);
        Assert.Equal([5, 7, 9, 11, 13, 15, 15], header.DetailSessionPoints.Select(p => p.Value));
        Assert.Equal(now, header.DetailSessionPoints[^1].Time);
        Assert.Equal(now, header.Now);
        // The week runs up to the weekly limit's reset, and ends at the latest reading.
        Assert.Equal(weekReset.AddDays(-7), header.WeekStart);
        Assert.Equal(weekReset, header.WeekEnd);
        Assert.Equal([10, 11, 12, 13, 14, 15, 16, 16], header.WeekPoints.Select(p => p.Value));
        Assert.Equal(new ChartPoint(now, 16), header.WeekPoints[^1]);
        // Its projection carries the week's average pace on to the reset.
        Assert.Equal(new ChartPoint(now, 16), header.WeekProjection[0]);
        Assert.Equal(weekReset, header.WeekProjection[^1].Time);
        var pace = 16 / (now - weekReset.AddDays(-7)).TotalHours;
        Assert.Equal(16 + (pace * (weekReset - now).TotalHours), header.WeekProjection[^1].Value, 6);
        // The model-specific limit is a thinner line, with a legend naming both.
        var fable = Assert.Single(header.WeekModelSeries);
        Assert.Equal("Fable", fable.Label);
        Assert.Equal(8, fable.Points.Count);
        Assert.Equal(3, fable.Points[0].Value);
        Assert.Equal(new ChartPoint(now, 4), fable.Points[^1]);
        Assert.Equal([("All models", "16%", 0), ("Fable", "4%", 1)], header.WeekLegend.Select(l => (l.Label, l.PercentText, l.Series)));

        // Without the model meters (Settings → Usage), there's no model line and no legend.
        h.Services.Settings.Usage.ShowModelMeters = false;
        h.Services.SaveSettings();
        Assert.Empty(header.WeekModelSeries);
        Assert.False(header.HasWeekLegend);
    }

    [Fact]
    public async Task The_threshold_lines_come_from_Settings()
    {
        await using var h = new TabTestHarness();
        await using var tracker = NewTracker(h);
        using var header = new UsageViewModel(h.Services, tracker);
        Assert.Equal((75, 90), (header.WarnPercent, header.CriticalPercent));
        var changed = new List<string?>();
        header.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        h.Services.Settings.Usage.WarnPercent = 60;
        h.Services.Settings.Usage.CriticalPercent = 80;
        h.Services.SaveSettings();

        Assert.Equal((60, 80), (header.WarnPercent, header.CriticalPercent));
        Assert.Contains(nameof(UsageViewModel.WarnPercent), changed);
        Assert.Contains(nameof(UsageViewModel.CriticalPercent), changed);
    }

    [Fact]
    public async Task The_session_chart_marks_when_the_projection_hits_the_critical_threshold()
    {
        await using var h = new TabTestHarness();
        await using var tracker = NewTracker(h);
        using var header = new UsageViewModel(h.Services, tracker);
        header.ToggleDetailsCommand.Execute(null);
        var reset = h.Time.GetUtcNow().AddHours(3);

        // 30% an hour: 50% now, so 90% in 80 minutes, 1h 20m before the reset.
        await Reading(h, tracker, header, 0.40, reset);
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await Reading(h, tracker, header, 0.45, reset);
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await Reading(h, tracker, header, 0.50, reset);
        var hits = h.Time.GetUtcNow().AddMinutes(80);

        Assert.True(header.HasCrossing);
        Assert.Equal(90, header.SessionCrossing!.Value.Value);
        Assert.Equal(hits, header.SessionCrossing.Value.Time, TimeSpan.FromSeconds(1));
        Assert.Equal($"Hits 90% at {hits.ToLocalTime():t}, 1h 20m before it resets.", header.CrossingText);
        Assert.Equal("30.0%/h", header.BurnRateText);
        Assert.Equal("Over the last 30 min", header.BurnRateNote);
        Assert.Equal("1h 40m", header.TimeToLimitText);
        Assert.Equal("Before it resets", header.TimeToLimitNote);

        // A lower critical threshold in Settings moves the mark.
        h.Services.Settings.Usage.CriticalPercent = 80;
        h.Services.SaveSettings();
        Assert.Equal(80, header.SessionCrossing!.Value.Value);
        Assert.StartsWith("Hits 80% at ", header.CrossingText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_slow_session_has_no_mark_and_says_it_resets_first()
    {
        await using var h = new TabTestHarness();
        await using var tracker = NewTracker(h);
        using var header = new UsageViewModel(h.Services, tracker) { IsDetailed = true };
        var reset = h.Time.GetUtcNow().AddHours(2);

        await Reading(h, tracker, header, 0.20, reset);
        Assert.Equal("—", header.BurnRateText);
        Assert.Equal("Not enough data yet", header.BurnRateNote);
        h.Time.Advance(TimeSpan.FromMinutes(20));
        await Reading(h, tracker, header, 0.22, reset);

        Assert.False(header.HasCrossing);
        Assert.Null(header.CrossingText);
        Assert.Equal("6.0%/h", header.BurnRateText);
        Assert.Equal("It resets first", header.TimeToLimitNote);
    }

    [Fact]
    public async Task The_busiest_tabs_are_the_top_three_by_tokens_this_window()
    {
        await using var h = new TabTestHarness();
        await using var tracker = NewTracker(h);
        var now = h.Time.GetUtcNow();
        tracker.Store.AddTurns([
            Turn(now.AddHours(-6), "old", 10_000_000),
            Turn(now.AddMinutes(-50), "api", 4_100),
            Turn(now.AddMinutes(-40), "docs", 3_000),
            Turn(now.AddMinutes(-30), "gone", 2_000),
            Turn(now.AddMinutes(-20), "tiny", 900),
        ]);
        using var header = new UsageViewModel(h.Services, tracker)
        {
            TabName = id => id switch { "api" => "refactor auth", "docs" => "write the docs", "tiny" => "a question", _ => null },
        };
        await Reading(h, tracker, header, 0.30, now.AddHours(4));
        Assert.Empty(header.BusiestTabs);

        header.ToggleDetailsCommand.Execute(null);
        await TabTestHarness.Eventually(() => header.HasBusiestTabs, "the busiest tabs");

        Assert.Equal(
            [("refactor auth", "41%"), ("write the docs", "30%"), ("A closed tab", "20%")],
            header.BusiestTabs.Select(t => (t.Name, t.ShareText)));
        Assert.Equal(0.41, header.BusiestTabs[0].Share, 6);

        // A turn somewhere else reads the tabs again.
        tracker.OnTurnCompleted("tiny", Result(20_000));
        await TabTestHarness.Eventually(() => header.BusiestTabs[0].Name == "a question", "the new turn");
    }

    [Fact]
    public async Task The_toggle_is_remembered_on_this_machine_and_matches_the_setting()
    {
        await using var h = new TabTestHarness();
        await using var tracker = NewTracker(h);
        using var header = new UsageViewModel(h.Services, tracker);
        Assert.False(header.IsDetailed);
        Assert.Equal("Expand the usage header", header.DetailsToggleText);

        header.ToggleDetailsCommand.Execute(null);

        Assert.True(header.IsDetailed);
        Assert.Equal("Collapse the usage header", header.DetailsToggleText);
        Assert.True(h.Services.State.DetailedUsageHeader);
        using (var again = new UsageViewModel(h.Services, tracker))
        {
            Assert.True(again.IsDetailed);
        }

        // Settings → Appearance is the same switch, and Reset to defaults turns it off.
        var settings = new SettingsViewModel(h.Services, null);
        Assert.True(settings.DetailedUsageHeader);
        settings.DetailedUsageHeader = false;
        Assert.False(header.IsDetailed);
        settings.DetailedUsageHeader = true;
        Assert.True(header.IsDetailed);
        settings.ResetAppearanceCommand.Execute(null);
        Assert.False(settings.DetailedUsageHeader);
        Assert.False(h.Services.State.DetailedUsageHeader);
        Assert.False(header.IsDetailed);
        Assert.Equal(new SettingsSearchResult("Appearance", "Detailed usage header"), Assert.Single(settings.SearchResultsFor("detailed usage")));
    }

    [Fact]
    public async Task Without_plan_limits_there_is_no_detailed_header()
    {
        await using var h = new TabTestHarness();
        h.Services.State.DetailedUsageHeader = true;
        await using var tracker = NewTracker(h);
        using var header = new UsageViewModel(h.Services, tracker);

        Assert.True(header.IsDetailed);
        Assert.False(header.HasData);
        Assert.False(header.ShowDetails);
        Assert.Empty(header.DetailSessionPoints);
        Assert.Empty(header.WeekPoints);

        await Reading(h, tracker, header, 0.10, h.Time.GetUtcNow().AddHours(4));
        Assert.True(header.ShowDetails);
    }

    [Fact]
    public async Task A_narrow_window_leaves_out_the_busiest_tabs_then_the_weekly_chart()
    {
        await using var h = new TabTestHarness();
        await using var tracker = NewTracker(h);
        using var header = new UsageViewModel(h.Services, tracker);
        Assert.True(header.ShowBusiestTabs && header.ShowWeekChart);

        header.SetDetailsWidth(UsageViewModel.DetailsTabsMinWidth - 1);
        Assert.False(header.ShowBusiestTabs);
        Assert.True(header.ShowWeekChart);
        Assert.Equal(1, header.SessionChartSpan);

        header.SetDetailsWidth(UsageViewModel.DetailsWeekMinWidth - 1);
        Assert.False(header.ShowWeekChart);
        Assert.Equal(2, header.SessionChartSpan);

        header.SetDetailsWidth(1400);
        Assert.True(header.ShowBusiestTabs && header.ShowWeekChart);
    }

    private static UsageTracker NewTracker(TabTestHarness h) => new(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));

    private static void Add(UsageStore store, TabTestHarness h, double session, DateTimeOffset sessionReset, double weekly, DateTimeOffset weeklyReset, double fable) =>
        Assert.True(store.AddSample(new UsageSnapshot(
            [
                new LimitReading(LimitKind.Session, "Session", session, sessionReset, null, true),
                new LimitReading(LimitKind.WeeklyAll, "Weekly", weekly, weeklyReset, null, true),
                new LimitReading(LimitKind.WeeklyModel, "Fable", fable, weeklyReset, null, true),
            ],
            h.Time.GetUtcNow(),
            UsageSource.GetUsage)));

    /// <summary>A <c>rate_limit_event</c> with the session at <paramref name="session"/> (0–1), once the header has it.</summary>
    private static async Task Reading(TabTestHarness h, UsageTracker tracker, UsageViewModel header, double session, DateTimeOffset resets)
    {
        var info = new JsonObject
        {
            ["status"] = "allowed",
            ["unifiedWindows"] = new JsonObject
            {
                ["five_hour"] = new JsonObject { ["utilization"] = session, ["resetsAt"] = resets.ToUnixTimeSeconds() },
                ["seven_day"] = new JsonObject { ["utilization"] = 0.2, ["resetsAt"] = resets.AddDays(3).ToUnixTimeSeconds() },
            },
        };
        var now = h.Time.GetUtcNow();
        tracker.OnRateLimitEvent(new RateLimitEventMessage(info, new JsonObject { ["type"] = "rate_limit_event", ["rate_limit_info"] = info.DeepClone() }));
        await TabTestHarness.Eventually(() => tracker.Current?.AsOf == now && header.Session.PercentText == $"{session * 100:0}%", "the reading");
    }

    private static TurnRecord Turn(DateTimeOffset at, string tab, long tokens) => new(at, tab, "s1", "claude-opus-5-5", tokens, 0, 0, 0, 0.01);

    private static ResultMessage Result(long input) =>
        (ResultMessage)(MessageParser.TryParse(new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["session_id"] = "s1",
            ["modelUsage"] = new JsonObject { ["claude-opus-5-5"] = new JsonObject { ["inputTokens"] = input, ["outputTokens"] = 0, ["costUSD"] = 0.01 } },
        }.ToJsonString(), out var message, out _) ? message! : throw new InvalidOperationException());
}
