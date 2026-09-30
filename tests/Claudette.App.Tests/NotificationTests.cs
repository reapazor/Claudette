using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>OS notifications and the Dock/taskbar badge (DESIGN.md §10).</summary>
public class NotificationTests
{
    private const string BashRequest = """
        {"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"Bash","tool_use_id":"t1","input":{"command":"npm test"}}}
        """;

    [Fact]
    public async Task A_finished_turn_notifies_while_Claudette_is_in_the_background()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Transport.EmitTurn("Fixed the login bug.\nDetails follow.");
        await TabTestHarness.Eventually(() => h.Notifier.Last("TurnFinished:") is not null, "the notification");

        var notification = h.Notifier.Last("TurnFinished:")!;
        Assert.Equal($"TurnFinished:{tab.Id}", notification.Id);
        Assert.Equal(tab.DisplayName, notification.Title);
        Assert.Equal("Fixed the login bug.", notification.Body);
    }

    [Fact]
    public async Task Nothing_is_sent_about_the_selected_tab_while_Claudette_is_in_front()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Services.Notifications.SetAppActive(true);

        h.Transport.EmitTurn("done");
        await TabTestHarness.Eventually(() => tab.State.Tokens.Turns == 1, "the turn");

        Assert.Empty(h.Notifier.Shown);
    }

    [Fact]
    public async Task A_background_tab_notifies_even_while_Claudette_is_in_front()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Services.Notifications.SetAppActive(true);
        h.Shell.SelectedTab = null;

        h.Transport.EmitTurn("done");
        await TabTestHarness.Eventually(() => h.Notifier.Last("TurnFinished:") is not null, "the notification");

        Assert.Equal(TabStatus.Unread, tab.Status);
    }

    [Fact]
    public async Task A_waiting_prompt_notifies_badges_and_clears_when_answered()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Transport.Emit(BashRequest);
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.NeedsInput, "needs input");

        var notification = h.Notifier.Last("NeedsInput:")!;
        Assert.Equal("Allow this command? npm test", notification.Body);
        Assert.Equal(1, h.Notifier.Badge);

        tab.Items.OfType<PermissionItem>().Single().AllowCommand.Execute(null);
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.NeedsInput, "the prompt to clear");

        Assert.Contains(notification.Id, h.Notifier.Removed);
        Assert.Equal(0, h.Notifier.Badge);
    }

    [Fact]
    public async Task Questions_and_plans_say_what_is_waiting()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Transport.Emit("""{"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Which database?","header":"DB","options":[{"label":"Postgres"}],"multiSelect":false}]}}}""");
        await TabTestHarness.Eventually(() => h.Notifier.Last("NeedsInput:") is not null, "the question");
        Assert.Equal("Claude has a question: Which database?", h.Notifier.Last("NeedsInput:")!.Body);

        h.Transport.Emit("""{"type":"control_request","request_id":"p2","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","input":{"plan":"1. Fix"}}}""");
        await TabTestHarness.Eventually(() => h.Notifier.Last("NeedsInput:")!.Body.Contains("plan", StringComparison.Ordinal), "the plan");
        Assert.Equal("Claude has a plan for you to review.", h.Notifier.Last("NeedsInput:")!.Body);
    }

    [Fact]
    public async Task A_type_turned_off_in_settings_is_not_sent()
    {
        await using var h = new TabTestHarness(settings => settings.Notifications.TurnFinished = false);
        var tab = await h.OpenTabAsync();

        h.Transport.EmitTurn("done");
        await TabTestHarness.Eventually(() => tab.State.Tokens.Turns == 1, "the turn");

        Assert.Empty(h.Notifier.Shown);
    }

    [Fact]
    public async Task An_unexpected_exit_notifies()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Transport.Exit(3);
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Error, "the error");

        Assert.Equal("Claude Code stopped unexpectedly (exit code 3).", h.Notifier.Last("ProcessError:")!.Body);
    }

    [Fact]
    public async Task Clicking_a_notification_reports_where_it_goes()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        NotificationTarget? clicked = null;
        h.Services.Notifications.Activated += target => clicked = target;
        h.Transport.Emit(BashRequest);
        await TabTestHarness.Eventually(() => h.Notifier.Last("NeedsInput:") is not null, "the notification");

        h.Notifier.Click($"NeedsInput:{tab.Id}");

        Assert.Equal(new NotificationTarget(NotificationKind.NeedsInput, tab.Id), clicked);
    }

    [Fact]
    public async Task Selecting_a_tab_from_a_notification_expands_its_group()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var group = h.Shell.Groups.Single();
        h.Shell.ToggleGroupCollapsedCommand.Execute(group);
        h.Shell.SelectedTab = null;

        Assert.True(h.Shell.SelectTab(tab.Id));

        Assert.Same(tab, h.Shell.SelectedTab);
        Assert.False(group.IsCollapsed);
        Assert.False(h.Shell.SelectTab("gone"));
    }

    [Fact]
    public async Task Looking_at_a_tab_takes_its_notifications_away()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.EmitTurn("done");
        await TabTestHarness.Eventually(() => h.Notifier.Last("TurnFinished:") is not null, "the notification");

        h.Services.Notifications.SetAppActive(true);

        Assert.Contains($"TurnFinished:{tab.Id}", h.Notifier.Removed);
    }

    [Fact]
    public async Task Check_ins_notify_only_when_asked()
    {
        await using var h = new TabTestHarness(settings => settings.CheckIns = new CheckInSettings { RunTimeMinutes = 0, QuietTimeMinutes = 5, Message = "Status?", Notify = true });
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "long job";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5"}""");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "working");

        // The quiet time, then the countdown before it's sent.
        for (var i = 0; i < 34; i++)
        {
            h.Time.Advance(CheckInMonitor.TickInterval);
        }

        await TabTestHarness.Eventually(() => h.Notifier.Last("CheckIn:") is not null, "the check-in notification");
        Assert.Contains("asked how it's going", h.Notifier.Last("CheckIn:")!.Body);
    }

    [Fact]
    public async Task App_wide_notifications_are_skipped_while_Claudette_is_in_front()
    {
        await using var h = new TabTestHarness();
        var notifications = h.Services.Notifications;

        notifications.SetAppActive(true);
        Assert.False(notifications.Notify(NotificationKind.UsageAlert, "Session at 75%", "…", key: "ThresholdCrossed"));

        notifications.SetAppActive(false);
        Assert.True(notifications.Notify(NotificationKind.UsageAlert, "Session at 75%", "…", key: "ThresholdCrossed"));
        Assert.True(notifications.Notify(NotificationKind.UsageAlert, "On pace to hit the session limit", "…", key: "ProjectedToHitLimit"));

        Assert.Equal(["UsageAlert:ThresholdCrossed", "UsageAlert:ProjectedToHitLimit"], h.Notifier.Shown.Select(n => n.Id));
    }

    [Fact]
    public async Task Nothing_is_sent_where_notifications_are_unavailable()
    {
        await using var h = new TabTestHarness();
        h.Notifier.IsAvailable = false;

        Assert.False(h.Services.Notifications.Notify(NotificationKind.SignIn, "Sign in", "…"));
        Assert.Empty(h.Notifier.Shown);
    }

    [Fact]
    public async Task The_badge_can_be_turned_off()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(BashRequest);
        await TabTestHarness.Eventually(() => h.Notifier.Badge == 1, "the badge");

        h.Services.Settings.Notifications.Badge = false;
        h.Services.SaveSettings();

        Assert.Equal(0, h.Notifier.Badge);
        Assert.Equal(TabStatus.NeedsInput, tab.Status);
    }

    [Fact]
    public async Task An_update_is_announced_once_per_version()
    {
        var updater = new FakeClaudeUpdater(new Version(2, 1, 284)) { Kind = Core.Installation.ClaudeUpdateKind.Homebrew, Available = new Version(2, 1, 290) };
        await using var h = new TabTestHarness(updater: updater);
        using var updates = new ClaudeUpdateViewModel(h.Services, h.Services.ClaudeUpdates!, () => h.Shell.RunningVersions);

        await h.Services.ClaudeUpdates!.CheckNowAsync();
        await h.Services.ClaudeUpdates.CheckNowAsync();

        var update = Assert.Single(h.Notifier.Shown);
        Assert.Equal("UpdateReady", update.Id);
        Assert.Equal("Claude Code 2.1.290 is ready", update.Title);

        // Remembered on this machine: a restart doesn't announce the same version again.
        using var afterRestart = new ClaudeUpdateViewModel(h.Services, h.Services.ClaudeUpdates!, () => h.Shell.RunningVersions);
        await h.Services.ClaudeUpdates.CheckNowAsync();
        Assert.Single(h.Notifier.Shown);
        Assert.Equal("2.1.290", h.Services.State.NotifiedClaudeUpdate);
    }
}
