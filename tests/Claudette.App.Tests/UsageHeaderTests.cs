using System.Text.Json.Nodes;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Protocol;
using Claudette.Usage;

namespace Claudette.App.Tests;

/// <summary>The usage header (DESIGN.md §6), fed by rate-limit events rather than polling.</summary>
public class UsageHeaderTests
{
    [Fact]
    public async Task A_rate_limit_event_fills_the_meters()
    {
        await using var h = new TabTestHarness();
        await using var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));
        using var header = new UsageViewModel(h.Services, tracker);
        var resets = h.Time.GetUtcNow().AddHours(2).AddMinutes(14);

        tracker.OnRateLimitEvent(Event(0.62, resets, 0.38));

        await TabTestHarness.Eventually(() => header.HasData, "the meters");
        Assert.Equal("62%", header.Session.PercentText);
        Assert.Equal("resets in 2h 14m", header.Session.ResetText);
        var weekly = Assert.Single(header.Weekly);
        Assert.Equal("Weekly", weekly.Label);
        Assert.Equal("38%", weekly.PercentText);
    }

    [Fact]
    public async Task Crossing_the_warning_threshold_raises_an_alert()
    {
        await using var h = new TabTestHarness();
        await using var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));
        using var header = new UsageViewModel(h.Services, tracker);
        var resets = h.Time.GetUtcNow().AddHours(3);

        tracker.OnRateLimitEvent(Event(0.70, resets, 0.40));
        await TabTestHarness.Eventually(() => header.HasData, "the first reading");
        Assert.False(header.HasAlert);

        h.Time.Advance(TimeSpan.FromMinutes(5));
        tracker.OnRateLimitEvent(Event(0.80, resets, 0.41));

        await TabTestHarness.Eventually(() => header.HasAlert, "the alert");
        Assert.True(header.Session.IsWarning);
        header.DismissAlertCommand.Execute(null);
        Assert.False(header.HasAlert);
    }

    [Fact]
    public async Task A_tab_turn_is_recorded_for_the_usage_panel()
    {
        await using var h = new TabTestHarness();
        await using var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));
        var recorded = new TaskCompletionSource();
        tracker.TurnRecorded += () => recorded.TrySetResult();

        tracker.OnTurnCompleted("tab-1", Result(100, 20));
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var sum = Assert.Single(tracker.Store.GetTokensByTab(h.Time.GetUtcNow().AddHours(-1)));
        Assert.Equal("tab-1", sum.TabId);
        Assert.Equal(120, sum.Total);
    }

    private static RateLimitEventMessage Event(double session, DateTimeOffset sessionResets, double weekly)
    {
        var info = new JsonObject
        {
            ["status"] = "allowed",
            ["unifiedWindows"] = new JsonObject
            {
                ["five_hour"] = new JsonObject { ["utilization"] = session, ["resetsAt"] = sessionResets.ToUnixTimeSeconds() },
                ["seven_day"] = new JsonObject { ["utilization"] = weekly, ["resetsAt"] = sessionResets.AddDays(3).ToUnixTimeSeconds() },
            },
        };
        return new RateLimitEventMessage(info, new JsonObject { ["type"] = "rate_limit_event", ["rate_limit_info"] = info.DeepClone() });
    }

    private static ResultMessage Result(long input, long output) =>
        (ResultMessage)(MessageParser.TryParse(new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["session_id"] = "s1",
            ["modelUsage"] = new JsonObject { ["claude-opus-5-5"] = new JsonObject { ["inputTokens"] = input, ["outputTokens"] = output, ["costUSD"] = 0.01 } },
        }.ToJsonString(), out var message, out _) ? message! : throw new InvalidOperationException());
}
