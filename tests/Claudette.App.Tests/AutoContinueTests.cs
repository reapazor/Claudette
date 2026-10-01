using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>Continuing a task a usage limit stopped, once the limit resets (DESIGN.md §6, "Continuing after a limit resets").</summary>
public class AutoContinueTests
{
    [Fact]
    public async Task A_task_the_limit_stopped_continues_when_it_resets()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var reset = h.Time.GetUtcNow() + TimeSpan.FromHours(2);

        await HitLimitAsync(h, tab, reset);

        Assert.True(tab.WillContinueAfterLimit);
        Assert.StartsWith("You've hit your session limit. The task continues by itself when it resets, at ", tab.LimitWaitText);
        Assert.StartsWith("Usage limit · continues at ", tab.RowDetail);
        Assert.Contains(tab.InfoRows, r => r.Label == "Usage limit");
        Assert.Equal(reset, tab.State.LimitWait?.ResetsAt);

        await AdvanceToAsync(h, reset + AutoContinueMonitor.Grace);

        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains(AutoContinueMonitor.Message), "the continue");
        var sent = tab.Items.OfType<UserMessageItem>().Last();
        Assert.True(sent.IsAutoContinue);
        Assert.Equal("Automatic continue after the usage limit reset", sent.AutomaticLabel);
        Assert.False(tab.HasLimitWait);
        Assert.Null(tab.State.LimitWait);
        Assert.Equal(tab.ModelBadge, tab.RowDetail);
    }

    [Fact]
    public async Task A_tab_turned_off_says_when_it_resets_and_continues_when_asked()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.State.Overrides.ContinueAfterLimitReset = false;
        var reset = h.Time.GetUtcNow() + TimeSpan.FromHours(2);

        await HitLimitAsync(h, tab, reset);

        Assert.False(tab.WillContinueAfterLimit);
        Assert.True(tab.CanContinueAfterLimit);
        Assert.Equal("Continue when it resets", tab.ContinueAfterLimitText);
        Assert.StartsWith("You've hit your session limit. It resets at ", tab.LimitWaitText);
        Assert.Equal(tab.ModelBadge, tab.RowDetail);

        await AdvanceToAsync(h, reset + TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(AutoContinueMonitor.Message, h.Transport.SentUserTexts);
        Assert.Equal("Your session limit has reset.", tab.LimitWaitText);
        Assert.Equal("Continue", tab.ContinueAfterLimitText);

        tab.ContinueAfterLimitCommand.Execute(null);

        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains(AutoContinueMonitor.Message), "the continue");
        Assert.False(tab.HasLimitWait);
    }

    [Fact]
    public async Task Settings_turn_it_off_and_a_tab_can_turn_it_back_on()
    {
        await using var h = new TabTestHarness(s => s.Usage.ContinueAfterLimitReset = false);
        var tab = await h.OpenTabAsync();
        var reset = h.Time.GetUtcNow() + TimeSpan.FromHours(2);
        await HitLimitAsync(h, tab, reset);
        Assert.False(tab.WillContinueAfterLimit);

        var settings = new TabSettingsViewModel(h.Services, tab, () => { });
        Assert.Equal("Default (off)", settings.SelectedAutoContinue.Label);
        settings.SelectedAutoContinue = settings.AutoContinueChoices.Single(c => c.Label == "On");
        await settings.ApplyCommand.ExecuteAsync(null);

        Assert.True(tab.State.Overrides.ContinueAfterLimitReset);
        Assert.True(tab.HasOverrides);
        Assert.True(tab.WillContinueAfterLimit);
        await AdvanceToAsync(h, reset + AutoContinueMonitor.Grace);
        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains(AutoContinueMonitor.Message), "the continue");

        // Use defaults goes back to Settings → Usage.
        settings = new TabSettingsViewModel(h.Services, tab, () => { });
        Assert.Equal("On", settings.SelectedAutoContinue.Label);
        settings.UseDefaultsCommand.Execute(null);
        await settings.ApplyCommand.ExecuteAsync(null);
        Assert.Null(tab.State.Overrides.ContinueAfterLimitReset);
    }

    [Fact]
    public async Task Turning_it_off_in_settings_stops_a_waiting_tab()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var reset = h.Time.GetUtcNow() + TimeSpan.FromHours(2);
        await HitLimitAsync(h, tab, reset);

        var settings = new SettingsViewModel(h.Services, null);
        settings.ContinueAfterLimitReset = false;

        await TabTestHarness.Eventually(() => !tab.WillContinueAfterLimit, "the wait to follow the setting");
        await AdvanceToAsync(h, reset + AutoContinueMonitor.Grace);
        Assert.DoesNotContain(AutoContinueMonitor.Message, h.Transport.SentUserTexts);
    }

    [Fact]
    public async Task Dont_continue_leaves_the_task_stopped()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var reset = h.Time.GetUtcNow() + TimeSpan.FromHours(2);
        await HitLimitAsync(h, tab, reset);

        tab.DontContinueCommand.Execute(null);

        await TabTestHarness.Eventually(() => tab.CanContinueAfterLimit, "the wait to stop");
        await AdvanceToAsync(h, reset + TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(AutoContinueMonitor.Message, h.Transport.SentUserTexts);

        tab.DismissLimitWaitCommand.Execute(null);
        await TabTestHarness.Eventually(() => !tab.HasLimitWait, "the bar to close");
    }

    [Fact]
    public async Task Sending_a_message_ends_the_wait()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var reset = h.Time.GetUtcNow() + TimeSpan.FromHours(2);
        await HitLimitAsync(h, tab, reset);

        tab.ComposerText = "Try something else";
        await tab.SendCommand.ExecuteAsync(null);

        await TabTestHarness.Eventually(() => !tab.HasLimitWait, "the wait to end");
        await AdvanceToAsync(h, reset + AutoContinueMonitor.Grace);
        Assert.DoesNotContain(AutoContinueMonitor.Message, h.Transport.SentUserTexts);
    }

    [Fact]
    public async Task A_restored_tab_keeps_waiting_and_continues()
    {
        await using var h = new TabTestHarness();
        var reset = h.Time.GetUtcNow() + TimeSpan.FromHours(1);
        h.Services.State.Tabs =
        [
            new TabState
            {
                Folder = h.WorkFolder,
                IsPinned = true,
                LimitWait = new LimitWait(reset, "five_hour", reset + AutoContinueMonitor.Grace, LimitWaitHold.None),
            },
        ];
        h.Shell.Restore(null);
        var tab = h.Shell.SelectedTab!;

        Assert.True(tab.WillContinueAfterLimit);
        // An hour can't pass while claude starts: the start's own requests would time out on the fake clock.
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the tab to start");
        await AdvanceToAsync(h, reset + AutoContinueMonitor.Grace);

        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains(AutoContinueMonitor.Message), "the continue");
    }

    [Fact]
    public async Task A_restored_tab_whose_limit_reset_long_ago_waits_for_the_user()
    {
        await using var h = new TabTestHarness();
        var reset = h.Time.GetUtcNow() - TimeSpan.FromHours(3);
        h.Services.State.Tabs =
        [
            new TabState
            {
                Folder = h.WorkFolder,
                IsPinned = true,
                LimitWait = new LimitWait(reset, "five_hour", reset + AutoContinueMonitor.Grace, LimitWaitHold.None),
            },
        ];
        h.Shell.Restore(null);
        var tab = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the tab to start");

        Assert.True(tab.CanContinueAfterLimit);
        Assert.StartsWith("Your session limit reset ", tab.LimitWaitText);
        Assert.Contains("while this computer was asleep or Claudette was closed", tab.LimitWaitText);
        Assert.DoesNotContain(AutoContinueMonitor.Message, h.Transport.SentUserTexts);
    }

    [Fact]
    public void The_wait_is_saved_in_state_json()
    {
        var state = new AppState { Tabs = [new TabState { Folder = "x", LimitWait = new LimitWait(DateTimeOffset.Parse("2026-09-28T14:00:00Z"), "seven_day", DateTimeOffset.Parse("2026-09-28T14:01:00Z"), LimitWaitHold.TooFar) }] };

        var json = JsonFileStore<AppState>.Serialize(state);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppState>(json, JsonFileStore<AppState>.Options)!;

        Assert.Equal(state.Tabs[0].LimitWait, restored.Tabs[0].LimitWait);
        Assert.Contains("\"hold\": \"tooFar\"", json);
        Assert.DoesNotContain("willContinue", json);
    }

    /// <summary>Sends a message, and the turn runs into the limit, as Claude Code reports it.</summary>
    internal static async Task HitLimitAsync(TabTestHarness h, TabViewModel tab, DateTimeOffset reset)
    {
        tab.ComposerText = "Refactor the parser";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "rate_limit_event",
            ["rate_limit_info"] = new JsonObject
            {
                ["status"] = "rejected", ["resetsAt"] = reset.ToUnixTimeSeconds(), ["rateLimitType"] = "five_hour",
                ["overageStatus"] = "rejected", ["isUsingOverage"] = false,
            },
            ["uuid"] = "u1",
            ["session_id"] = "s1",
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "You've hit your session limit · resets 2pm" }) },
            ["error"] = "rate_limit",
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "result", ["subtype"] = "success", ["is_error"] = true, ["result"] = "You've hit your session limit · resets 2pm",
            ["api_error_status"] = 429, ["terminal_reason"] = "api_error", ["session_id"] = "s1",
        });
        await TabTestHarness.Eventually(() => tab.HasLimitWait && tab.Status == TabStatus.Idle, "the limit to stop the turn");
    }

    /// <summary>Moves the clock on a tick at a time, as it passes while the computer is awake.</summary>
    internal static async Task AdvanceToAsync(TabTestHarness h, DateTimeOffset time)
    {
        while (h.Time.GetUtcNow() < time)
        {
            var step = time - h.Time.GetUtcNow();
            h.Time.Advance(step < AutoContinueMonitor.TickInterval ? step : AutoContinueMonitor.TickInterval);
        }
        h.Time.Advance(AutoContinueMonitor.TickInterval);
        await Task.Yield();
    }
}
