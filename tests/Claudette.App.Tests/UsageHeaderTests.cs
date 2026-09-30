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
    public async Task A_stale_reading_does_not_take_the_meter_back_and_the_alert_follows_the_meter()
    {
        await using var h = new TabTestHarness();
        await using var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));
        using var header = new UsageViewModel(h.Services, tracker);
        // Close enough that the projection doesn't alert too.
        var resets = h.Time.GetUtcNow().AddMinutes(90);
        var shown = new List<string>();
        header.Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MeterViewModel.PercentText))
            {
                shown.Add(header.Session.PercentText);
            }
        };

        tracker.OnRateLimitEvent(Event(0.70, resets, 0.40));
        await TabTestHarness.Eventually(() => header.HasData, "the first reading");
        h.Time.Advance(TimeSpan.FromMinutes(30));
        tracker.OnRateLimitEvent(Event(0.756, resets, 0.40));
        await TabTestHarness.Eventually(() => header.HasAlert, "the alert");
        Assert.Equal("Session usage passed 75%", header.Alert!.Title);
        Assert.Equal("You've used 76% of your session limit. It resets in 1h 00m.", header.Alert.Message);

        // get_usage answering from Claude Code's cached reading, then the next real one.
        h.Time.Advance(TimeSpan.FromMinutes(1));
        tracker.OnRateLimitEvent(Event(0.70, resets, 0.40));
        h.Time.Advance(TimeSpan.FromMinutes(29));
        tracker.OnRateLimitEvent(Event(0.78, resets, 0.40));

        await TabTestHarness.Eventually(() => header.Session.PercentText == "78%", "the next reading");
        Assert.Equal(["70%", "76%", "78%"], shown);
        Assert.Equal("Session usage passed 75%", header.Alert!.Title);
        Assert.Equal("You've used 78% of your session limit. It resets in 30m.", header.Alert.Message);
    }

    [Fact]
    public async Task The_usage_panel_lists_every_past_session_a_page_at_a_time()
    {
        await using var h = new TabTestHarness();
        await using var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));
        using var header = new UsageViewModel(h.Services, tracker);
        for (var i = 0; i < 70; i++)
        {
            tracker.Store.AddSample(UsageSnapshotAt(h.Time.GetUtcNow(), 10 + (i % 50), h.Time.GetUtcNow().AddMinutes(1)));
            h.Time.Advance(TimeSpan.FromHours(5));
        }

        var panel = new UsagePanelViewModel(h.Services, tracker, header, _ => null);

        Assert.Equal(UsagePanelViewModel.SessionsPage, panel.PastSessions.Count);
        Assert.Equal("Show 30 more (40 left)", panel.MoreSessionsText);
        panel.ShowMoreSessionsCommand.Execute(null);
        panel.ShowMoreSessionsCommand.Execute(null);
        Assert.Equal(70, panel.PastSessions.Count);
        Assert.False(panel.HasMoreSessions);
    }

    // ---- Sharing across machines (DESIGN.md §6) --------------------------------------------------------------------

    [Fact]
    public async Task Machines_signed_in_to_the_same_account_chart_each_others_readings()
    {
        await using var first = SharingMachine("me@example.com", out var library);
        await using var second = SharingMachine("Me@Example.com", out _, library);
        await using var firstTracker = new UsageTracker(first.Services, new UsageStore(Path.Combine(first.Root, "usage.db"), first.Time));
        await using var secondTracker = new UsageTracker(second.Services, new UsageStore(Path.Combine(second.Root, "usage.db"), second.Time));
        using var secondHeader = new UsageViewModel(second.Services, secondTracker);
        var shared = Path.Combine(library, "usage", first.Services.State.MachineId + ".json");

        // The first machine takes a reading: it's shared straight away.
        firstTracker.OnRateLimitEvent(Event(0.62, first.Time.GetUtcNow().AddHours(2), 0.38));
        await TabTestHarness.Eventually(() => File.Exists(shared) && File.ReadAllText(shared).Contains("\"session\":62", StringComparison.Ordinal), "the shared reading");
        Assert.DoesNotContain("example.com", File.ReadAllText(shared), StringComparison.OrdinalIgnoreCase);

        await secondTracker.ShareAsync();

        await TabTestHarness.Eventually(() => secondHeader.SessionPoints.Any(p => p.Value == 62), "the other machine's reading in the trendline");
        Assert.Empty(secondTracker.Store.GetOwnSamples(DateTimeOffset.MinValue));
    }

    [Fact]
    public async Task Another_accounts_readings_arent_charted()
    {
        await using var first = SharingMachine("me@example.com", out var library);
        await using var second = SharingMachine("someone.else@example.com", out _, library);
        await using var firstTracker = new UsageTracker(first.Services, new UsageStore(Path.Combine(first.Root, "usage.db"), first.Time));
        await using var secondTracker = new UsageTracker(second.Services, new UsageStore(Path.Combine(second.Root, "usage.db"), second.Time));
        var shared = Path.Combine(library, "usage", first.Services.State.MachineId + ".json");
        firstTracker.OnRateLimitEvent(Event(0.62, first.Time.GetUtcNow().AddHours(2), 0.38));
        await TabTestHarness.Eventually(() => File.Exists(shared) && File.ReadAllText(shared).Contains("\"session\":62", StringComparison.Ordinal), "the shared reading");

        await secondTracker.ShareAsync();

        Assert.Empty(secondTracker.Store.GetSamples(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public async Task Nothing_is_shared_until_it_is_turned_on()
    {
        await using var h = SharingMachine("me@example.com", out var library);
        h.Services.Settings.Usage.ShareThroughLibrary = false;
        await using var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));
        tracker.OnRateLimitEvent(Event(0.62, h.Time.GetUtcNow().AddHours(2), 0.38));
        await TabTestHarness.Eventually(() => tracker.Store.GetLatestSample() is not null, "the reading");
        await tracker.ShareAsync(publish: true);

        Assert.False(Directory.Exists(Path.Combine(library, "usage")));

        // Turned on in Settings: shared at once.
        h.Services.Settings.Usage.ShareThroughLibrary = true;
        h.Services.SaveSettings();
        await TabTestHarness.Eventually(() => File.Exists(Path.Combine(library, "usage", h.Services.State.MachineId + ".json")), "the shared file");
    }

    /// <summary>A machine that shares its usage through <paramref name="library"/>, signed in as <paramref name="email"/>.</summary>
    private static TabTestHarness SharingMachine(string email, out string library, string? sharedLibrary = null)
    {
        var h = new TabTestHarness();
        library = sharedLibrary ?? Path.Combine(h.Root, "shared-library");
        h.Services.Settings.Sessions.LibraryFolder = library;
        h.Services.Settings.Usage.ShareThroughLibrary = true;
        h.Services.Library.OnSettingsChanged();
        h.Services.RemoteControl.UseAccount(new Core.Auth.AuthStatus(true, "claude.ai", null, email, "Acme", "max", null, null));
        return h;
    }

    private static UsageSnapshot UsageSnapshotAt(DateTimeOffset asOf, double session, DateTimeOffset resetsAt) =>
        new([new LimitReading(LimitKind.Session, "Session", session, resetsAt, null, false)], asOf, UsageSource.GetUsage);

    [Fact]
    public async Task A_tab_turn_is_recorded_for_the_usage_panel()
    {
        await using var h = new TabTestHarness();
        await using var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));
        var recorded = new TaskCompletionSource();
        tracker.TurnRecorded += () => recorded.TrySetResult();

        tracker.OnTurnCompleted("tab-1", "api", Result(100, 20));
        await recorded.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var sum = Assert.Single(tracker.Store.GetTokensByTab(h.Time.GetUtcNow().AddHours(-1)));
        Assert.Equal(("tab-1", "api"), (sum.TabId, sum.Name));
        Assert.Equal(120, sum.Total);
    }

    [Fact]
    public async Task Names_are_recorded_in_the_order_they_came()
    {
        await using var h = new TabTestHarness();
        var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));

        // Each rename lands after the turn before it, however the writes are scheduled.
        for (var i = 0; i < 20; i++)
        {
            tracker.OnTurnCompleted($"tab-{i}", "api", Result(100, 20));
            tracker.OnTabRenamed($"tab-{i}", "refactor auth");
            tracker.OnTabRenamed($"tab-{i}", $"refactor auth {i}");
        }
        await tracker.DisposeAsync();

        Assert.All(tracker.Store.GetTokensByTab(DateTimeOffset.MinValue), s => Assert.Equal($"refactor auth {s.TabId[4..]}", s.Name));
        Assert.Equal(20, tracker.Store.GetTokensByTab(DateTimeOffset.MinValue).Count);
        tracker.Store.Dispose();
    }

    [Fact]
    public async Task A_closed_tab_keeps_its_last_name_in_the_usage_panel()
    {
        await using var h = new TabTestHarness();
        var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));
        h.Services.UseUsageTracker(tracker);
        using var header = new UsageViewModel(h.Services, tracker);
        var panel = new UsagePanelViewModel(h.Services, tracker, header, id => h.Shell.AllTabs.FirstOrDefault(t => t.Id == id)?.DisplayName);
        var tab = await h.OpenTabAsync();
        h.Transport.EmitTurn();
        await Row("work", "the turn");

        // Renamed after its last turn, then closed: the panel still names it.
        tab.StartRenameCommand.Execute(null);
        tab.RenameText = "refactor auth";
        await tab.CommitRenameCommand.ExecuteAsync(null);
        h.Shell.CloseTabCommand.Execute(tab);
        await TabTestHarness.Eventually(() => !h.Shell.AllTabs.Any(), "the tab to close");

        await Row("refactor auth", "the closed tab's last name");

        async Task Row(string name, string what) =>
            await TabTestHarness.Eventually(() =>
            {
                panel.Refresh();
                return panel.Tabs is [{ } row] && row.Name == name;
            }, what);
    }

    [Fact]
    public void A_row_is_named_after_the_open_tab_then_the_last_name_kept()
    {
        var rows = TabBurnRow.From(
            [
                new TabTokenSum("open", 300, 0, 0, 0, 0, 1, "its old name"),
                new TabTokenSum("closed", 200, 0, 0, 0, 0, 1, "fix the build"),
                new TabTokenSum("recorded-before-names", 100, 0, 0, 0, 0, 1),
            ],
            id => id == "open" ? "its new name" : null);

        Assert.Equal(["its new name", "fix the build", "A closed tab"], rows.Select(r => r.Name));
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
