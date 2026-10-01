using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Auth;
using Claudette.Core.Development;
using Claudette.Core.RemoteControl;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>
/// Remote Control, a tab's connection to the Claude app (DESIGN.md §18): the per-tab switch, connecting with the
/// <c>remote_control</c> request and the <c>/remote-control</c> fallback, what Claude Code reports about the
/// connection, prompts answered on the phone, the presence file and keeping the computer awake.
/// </summary>
public class RemoteControlTests
{
    private const string SessionUrl = "https://claude.ai/code/session_01AbCdEf";

    private const string BashRequest = """
        {"type":"control_request","request_id":"p1","request":{"subtype":"can_use_tool","tool_name":"Bash","tool_use_id":"t1","input":{"command":"npm test"}}}
        """;

    // ---- Connecting ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_tab_with_the_switch_on_connects_as_it_starts_before_any_prompt()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        // Named after its folder until Claude Code names it after the first prompt.
        Assert.Equal("work", tab.DisplayName);
        tab.ComposerText = "hello";
        await tab.SendCommand.ExecuteAsync(null);

        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");
        var request = RemoteRequests(h).Single();
        Assert.True(request["enabled"]!.GetValue<bool>());
        Assert.Equal("work", request["name"]!.GetValue<string>());
        // Sent before the prompt, and never as a message of its own.
        var sent = h.Transport.Sent.ToList();
        Assert.True(sent.FindIndex(IsRemoteRequest) < sent.FindIndex(m => m["type"]?.GetValue<string>() == "user"));
        Assert.Equal(["hello"], h.Transport.SentUserTexts);
        Assert.Equal(["hello"], InlineDispatcher.Read(() => tab.Items.OfType<UserMessageItem>().Select(m => m.Text).ToList()));

        var note = InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Single(n => n.HasLink));
        Assert.Equal("Connected to the Claude app.", note.Text);
        Assert.Equal(SessionUrl, note.Link);
        Assert.Equal(new RemoteControlStatus(RemoteControlState.Connected, SessionUrl), tab.RemoteControl.Status);
        Assert.True(tab.RemoteControl.ShowIcon);
        Assert.False(tab.RemoteControl.IsSettling);
        Assert.Equal("Connected to the Claude app", tab.RemoteControl.StatusTip);
        Assert.Contains(tab.InfoRows, r => r is { Label: "Claude app", Value: $"Connected: {SessionUrl}" });

        Assert.True(tab.RemoteControl.OpenInClaudeAppCommand.CanExecute(null));
        await tab.RemoteControl.OpenInClaudeAppCommand.ExecuteAsync(null);
        Assert.Equal([SessionUrl], h.Platform.OpenedUrls);
    }

    [Fact]
    public async Task A_tab_with_the_switch_off_sends_nothing()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.Items.OfType<TurnSummaryItem>().Any(), "a turn");

        Assert.False(tab.RemoteControl.IsOn);
        Assert.Empty(RemoteRequests(h));
        Assert.Equal(RemoteControlStatus.NotConnected, tab.RemoteControl.Status);
        Assert.False(tab.RemoteControl.ShowIcon);
        Assert.DoesNotContain(tab.InfoRows, r => r.Label == "Claude app");
        Assert.False(tab.RemoteControl.OpenInClaudeAppCommand.CanExecute(null));
    }

    [Fact]
    public async Task Claude_Codes_reason_shows_when_it_cant_connect()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        const string reason = "Remote Control is only available when using Claude via api.anthropic.com.";
        h.Transport.Answers["remote_control"] = _ => throw new InvalidOperationException(reason);
        var tab = await h.OpenTabAsync();

        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.State == RemoteControlState.Unavailable, "the answer");

        Assert.Equal(reason, tab.RemoteControl.Status.Detail);
        var note = InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last());
        Assert.Equal($"Couldn't connect to the Claude app: {reason}", note.Text);
        Assert.Equal(NoteKind.Warning, note.Kind);
        Assert.Contains(tab.InfoRows, r => r is { Label: "Claude app", Value: $"Not available: {reason}" });
        Assert.False(tab.RemoteControl.ShowIcon);
        Assert.False(h.SleepBlocker.IsBlocking);
        // The switch stays on: the next start tries again.
        Assert.True(tab.RemoteControl.IsOn);
    }

    [Fact]
    public async Task Bridge_states_move_the_connection_and_one_it_doesnt_know_changes_nothing()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");

        h.Transport.Emit(BridgeState("reconnecting"));
        await TabTestHarness.Eventually(() => tab.RemoteControl.IsSettling, "reconnecting");
        Assert.True(tab.RemoteControl.Status.IsConnected);
        Assert.Equal("Connected to the Claude app, reconnecting…", tab.RemoteControl.StatusTip);
        Assert.True(h.SleepBlocker.IsBlocking);

        h.Transport.Emit(BridgeState("connected"));
        await TabTestHarness.Eventually(() => !tab.RemoteControl.IsSettling, "reconnected");

        h.Transport.Emit(BridgeState("exploded", "who knows"));
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.Items.OfType<TurnSummaryItem>().Any(), "a turn");
        Assert.Equal(new RemoteControlStatus(RemoteControlState.Connected, SessionUrl), tab.RemoteControl.Status);

        h.Transport.Emit(BridgeState("failed", "Remote Control could not verify the signed-in account"));
        await TabTestHarness.Eventually(() => !tab.RemoteControl.Status.IsConnected, "the failure");
        Assert.Equal("Remote Control could not verify the signed-in account", tab.RemoteControl.Status.Detail);
        Assert.Equal("Disconnected from the Claude app: Remote Control could not verify the signed-in account",
            InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last().Text));
        Assert.False(h.SleepBlocker.IsBlocking);

        // Claude Code gets the link back by itself: the same session, at the same address.
        h.Transport.Emit(BridgeState("ready"));
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the link back");
        Assert.Equal(SessionUrl, tab.RemoteControl.Status.Url);

        h.Transport.Emit(BridgeState("policy_disabled", "Remote Control is disabled by your organization's policy."));
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.State == RemoteControlState.Unavailable, "the policy");
        Assert.Equal("Remote Control stopped: Remote Control is disabled by your organization's policy.", InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last().Text));
    }

    [Fact]
    public async Task Worker_shutting_down_disconnects_but_not_a_tab_that_isnt_connected()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");

        h.Transport.Emit("""{"type":"system","subtype":"worker_shutting_down","reason":"heartbeat_lost","uuid":"u1","session_id":"s1"}""");

        await TabTestHarness.Eventually(() => !tab.RemoteControl.Status.IsConnected, "the shutdown");
        Assert.Equal("Claude Code closed the connection (heartbeat lost).", tab.RemoteControl.Status.Detail);
        Assert.Contains(tab.InfoRows, r => r is { Label: "Claude app", Value: "Not connected: Claude Code closed the connection (heartbeat lost)." });
        Assert.False(h.SleepBlocker.IsBlocking);
        var notes = InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Count());

        // One replayed from earlier in a resumed session changes nothing.
        h.Transport.Emit("""{"type":"system","subtype":"worker_shutting_down","reason":"host_exit","uuid":"u2","session_id":"s1"}""");
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.Items.OfType<TurnSummaryItem>().Any(), "a turn");
        Assert.Equal("Claude Code closed the connection (heartbeat lost).", tab.RemoteControl.Status.Detail);
        Assert.Equal(notes, InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Count()));
    }

    [Fact]
    public async Task Turned_off_by_a_policy_no_tab_tries_again_until_another_account_signs_in()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        AnswerConnected(h);
        h.Services.RemoteControl.UseAccount(new AuthStatus(true, "claude.ai", "firstParty", "me@example.com", "Example", "max", null, null));
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");

        // As 2.1.286 disconnects a session when the organization's policy turns Remote Control off.
        h.Transport.Emit("""{"type":"system","subtype":"worker_shutting_down","reason":"remote_control_disabled","uuid":"u1","session_id":"s1"}""");

        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.State == RemoteControlState.Unavailable, "the policy");
        Assert.Equal(RemoteControlProtocol.TurnedOffByPolicy, tab.RemoteControl.Status.Detail);
        Assert.Equal("Remote Control stopped: Remote Control is turned off by a policy.", InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last().Text));
        Assert.Equal(RemoteControlProtocol.TurnedOffByPolicy, h.Services.RemoteControl.PolicyReason);
        Assert.False(h.SleepBlocker.IsBlocking);
        // The tab keeps the user's choice; it just doesn't try.
        Assert.True(tab.RemoteControl.IsOn);
        var requests = RemoteRequests(h).Count;

        // A new tab doesn't try either, and says why.
        await h.Shell.CloseTabCommand.ExecuteAsync(tab);
        await TabTestHarness.Eventually(() => !h.Shell.HasTabs && !tab.IsProcessRunning, "the first tab to close");
        var second = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => second.RemoteControl.Status.State == RemoteControlState.Unavailable, "the second tab");
        Assert.Equal(requests, RemoteRequests(h).Count);
        Assert.Contains("Not connecting to the Claude app: Remote Control is turned off by a policy.", InlineDispatcher.Read(() => second.Items.OfType<NoteItem>().Select(n => n.Text).ToList()));
        Assert.False(second.RemoteControl.CanToggle && !second.RemoteControl.IsOn);

        // The same account checked again changes nothing; another one may be allowed, so the tabs try again.
        h.Services.RemoteControl.UseAccount(new AuthStatus(true, "claude.ai", "firstParty", "me@example.com", "Example", "max", null, null));
        Assert.NotNull(h.Services.RemoteControl.PolicyReason);
        h.Services.RemoteControl.UseAccount(new AuthStatus(true, "claude.ai", "firstParty", "me@example.org", "Other", "max", null, null));
        Assert.Null(h.Services.RemoteControl.PolicyReason);
        await TabTestHarness.Eventually(() => second.RemoteControl.Status.IsConnected, "the second tab to connect");
    }

    [Fact]
    public async Task Disconnecting_from_here_isnt_taken_for_a_policy()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        // Connecting is answered; disconnecting is left waiting, to be answered below.
        h.Transport.Answers["remote_control"] = request => request["enabled"]!.GetValue<bool>() ? new JsonObject { ["session_url"] = SessionUrl } : null;
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");

        // Claude Code says why the worker shuts down before it answers the request to disconnect.
        var leaving = tab.RemoteControl.ToggleCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => DisconnectRequestId(h) is not null, "the request to disconnect");
        h.Transport.Emit("""{"type":"system","subtype":"worker_shutting_down","reason":"remote_control_disabled","uuid":"u1","session_id":"s1"}""");
        await TabTestHarness.Eventually(() => !tab.RemoteControl.Status.IsConnected, "the shutdown");
        h.Transport.Emit(Claudette.Core.Protocol.OutgoingMessages.ControlSuccess(DisconnectRequestId(h)!, []).ToJsonString());
        await leaving;

        Assert.Equal(RemoteControlState.NotConnected, tab.RemoteControl.Status.State);
        Assert.Null(h.Services.RemoteControl.PolicyReason);

        static string? DisconnectRequestId(TabTestHarness h) => h.Transport.Sent
            .LastOrDefault(m => IsRemoteRequest(m) && !m["request"]!["enabled"]!.GetValue<bool>())?["request_id"]?.GetValue<string>();
    }

    // ---- The switch -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_switch_in_the_tab_menu_connects_an_idle_tab_and_disconnects_it()
    {
        await using var h = new TabTestHarness();
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        Assert.Empty(RemoteRequests(h));

        await tab.RemoteControl.ToggleCommand.ExecuteAsync(null);

        Assert.True(tab.RemoteControl.IsOn);
        Assert.True(tab.State.RemoteControl);
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");
        Assert.True(h.SleepBlocker.IsBlocking);

        await tab.RemoteControl.ToggleCommand.ExecuteAsync(null);

        Assert.False(tab.RemoteControl.IsOn);
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status == RemoteControlStatus.NotConnected, "the disconnection");
        Assert.Equal([true, false], RemoteRequests(h).Select(r => r["enabled"]!.GetValue<bool>()));
        Assert.Equal("Disconnected from the Claude app.", InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last().Text));
        Assert.False(h.SleepBlocker.IsBlocking);
        Assert.Equal([true, false], h.SleepBlocker.Changes);
        // Only the one session: nothing restarted.
        Assert.Single(h.Factory.Launches);
        Assert.Empty(h.Transport.SentUserTexts);
    }

    [Fact]
    public async Task Turning_it_on_or_off_while_Claude_works_waits_for_the_turn_to_end()
    {
        await using var h = new TabTestHarness();
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "work";
        await tab.SendCommand.ExecuteAsync(null);

        await tab.RemoteControl.SetAsync(true);

        Assert.Empty(RemoteRequests(h));
        Assert.Contains(tab.InfoRows, r => r is { Label: "Claude app", Value: "Connects when Claude finishes this turn" });
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection after the turn");

        tab.ComposerText = "more";
        await tab.SendCommand.ExecuteAsync(null);
        await tab.RemoteControl.SetAsync(false);

        Assert.Single(RemoteRequests(h));
        Assert.True(tab.RemoteControl.Status.IsConnected);
        Assert.Contains(tab.InfoRows, r => r.Label == "Claude app" && r.Value.EndsWith("Disconnects when Claude finishes this turn.", StringComparison.Ordinal));
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => !tab.RemoteControl.Status.IsConnected, "the disconnection after the turn");
        Assert.Equal([true, false], RemoteRequests(h).Select(r => r["enabled"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Switched_off_while_Claude_works_it_says_when_it_disconnects_and_dims_the_icon_until_it_has()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");
        tab.ComposerText = "work";
        await tab.SendCommand.ExecuteAsync(null);
        // Claude Code answers once it has closed the connection, which takes a moment.
        h.Transport.Answers["remote_control"] = _ => null;

        await tab.RemoteControl.SetAsync(false);

        Assert.Equal("Disconnecting from the Claude app when Claude finishes this turn.", InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last().Text));
        Assert.True(tab.RemoteControl.ShowIcon);
        Assert.True(tab.RemoteControl.IsLeaving);
        Assert.Equal("Connected to the Claude app. Disconnects when Claude finishes this turn.", tab.RemoteControl.StatusTip);

        // Turned back on before the turn ends: nothing to do, and the conversation says so.
        await tab.RemoteControl.SetAsync(true);

        Assert.Equal("Staying connected to the Claude app.", InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last().Text));
        Assert.False(tab.RemoteControl.IsLeaving);
        Assert.Equal("Connected to the Claude app", tab.RemoteControl.StatusTip);

        await tab.RemoteControl.SetAsync(false);
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => RemoteRequests(h).Count == 2, "the request to disconnect");

        Assert.True(tab.RemoteControl.IsLeaving);
        Assert.Equal("Disconnecting from the Claude app…", tab.RemoteControl.StatusTip);
        Assert.Contains(tab.InfoRows, r => r.Label == "Claude app" && r.Value.EndsWith(". Disconnecting…", StringComparison.Ordinal));

        var id = h.Transport.Sent.Last(IsRemoteRequest)["request_id"]!.GetValue<string>();
        h.Transport.Emit(Core.Protocol.OutgoingMessages.ControlSuccess(id, null));
        await TabTestHarness.Eventually(() => !tab.RemoteControl.Status.IsConnected, "the disconnection");

        Assert.False(tab.RemoteControl.ShowIcon);
        Assert.False(tab.RemoteControl.IsLeaving);
        Assert.Equal("Disconnected from the Claude app.", InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last().Text));
    }

    [Fact]
    public async Task Switched_back_on_while_Claude_Code_disconnects_it_connects_again_once_it_has()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");
        h.Transport.Answers["remote_control"] = _ => null;

        var off = tab.RemoteControl.SetAsync(false);
        await TabTestHarness.Eventually(() => RemoteRequests(h).Count == 2, "the request to disconnect");
        await tab.RemoteControl.SetAsync(true);

        // Claude Code is closing the connection all the same.
        Assert.True(tab.RemoteControl.IsLeaving);
        Assert.Equal("Disconnecting from the Claude app…", tab.RemoteControl.StatusTip);

        // Off and on again while it does: the request that's out is enough.
        await tab.RemoteControl.SetAsync(false);
        await tab.RemoteControl.SetAsync(true);
        Assert.Equal(2, RemoteRequests(h).Count);

        AnswerConnected(h);
        var id = h.Transport.Sent.Last(IsRemoteRequest)["request_id"]!.GetValue<string>();
        h.Transport.Emit(Core.Protocol.OutgoingMessages.ControlSuccess(id, null));
        await off;
        await TabTestHarness.Eventually(() => RemoteRequests(h).Count == 3 && tab.RemoteControl.Status.IsConnected, "the connection again");

        Assert.Equal([true, false, true], RemoteRequests(h).Select(r => r["enabled"]!.GetValue<bool>()));
        Assert.Equal(["Disconnected from the Claude app.", "Connected to the Claude app."],
            InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Select(n => n.Text).TakeLast(2).ToList()));
        Assert.False(tab.RemoteControl.IsLeaving);
        Assert.Equal("Connected to the Claude app", tab.RemoteControl.StatusTip);
        Assert.True(h.SleepBlocker.IsBlocking);
    }

    [Fact]
    public async Task When_Claude_Code_cant_disconnect_it_restarts_on_the_same_session_without_connecting()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        h.Transport.Answers["remote_control"] = request => request["enabled"]!.GetValue<bool>()
            ? new JsonObject { ["session_url"] = SessionUrl }
            : throw new InvalidOperationException("Remote Control teardown failed");
        var tab = await h.OpenTabAsync();
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected && tab.State.SessionId == "s1", "the connection and a turn");

        await tab.RemoteControl.SetAsync(false);

        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2 && tab.Status == TabStatus.Idle && tab.IsSettled, "the restart");
        Assert.Equal("s1", h.Factory.Launches[1].Resume);
        Assert.Contains("Restarting Claude Code to disconnect from the Claude app. The conversation carries on.",
            InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Select(n => n.Text).ToList()));
        // The new claude isn't asked to connect.
        Assert.Equal([true, false], RemoteRequests(h).Select(r => r["enabled"]!.GetValue<bool>()));
        Assert.Equal(RemoteControlStatus.NotConnected, tab.RemoteControl.Status);
        Assert.False(h.SleepBlocker.IsBlocking);
    }

    [Fact]
    public async Task The_switch_survives_saving_and_a_restart_and_a_restored_tab_that_has_it_on_reconnects()
    {
        var saved = new List<TabState>();
        RestartSnapshot? snapshot;
        await using (var h = new TabTestHarness())
        {
            AnswerConnected(h);
            var tab = await h.OpenTabAsync();
            await tab.RemoteControl.SetAsync(true);
            await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");
            await h.Services.FlushAsync();
            saved.AddRange(new JsonFileStore<AppState>(h.Services.Paths.StateFile).Load().Tabs);
            var captured = h.Shell.CaptureForRestart();
            captured.Nonce = "n";
            captured.CreatedAt = h.Time.GetUtcNow();
            captured.Save(h.Services.Paths.RestartFile);
            snapshot = RestartSnapshot.Load(h.Services.Paths.RestartFile, "n", h.Time.GetUtcNow());
        }
        Assert.True(Assert.Single(saved).RemoteControl);
        Assert.True(Assert.Single(snapshot!.Tabs).RemoteControl);

        await using (var on = new TabTestHarness(s => s.Sessions.RestoreUnpinnedTabs = true))
        {
            AnswerConnected(on);
            // The first run's folder went with it.
            on.Services.State.Tabs = [.. saved.Select(t => { t.Folder = on.WorkFolder; return t; })];
            on.Shell.Restore(null);
            var restored = on.Shell.SelectedTab!;
            Assert.True(restored.RemoteControl.IsOn);
            await TabTestHarness.Eventually(() => restored.RemoteControl.Status.IsConnected, "the restored tab to reconnect");
            Assert.Single(RemoteRequests(on));
        }

        await using var off = new TabTestHarness(s => s.Sessions.RestoreUnpinnedTabs = true);
        off.Services.State.Tabs = [new TabState { Folder = off.WorkFolder, RemoteControl = false }];
        off.Shell.Restore(null);
        var other = off.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => other.Status == TabStatus.Idle && other.IsSettled, "the restored tab to start");
        Assert.False(other.RemoteControl.IsOn);
        Assert.Empty(RemoteRequests(off));
    }

    [Fact]
    public async Task Connect_new_tabs_only_sets_the_switch_for_tabs_opened_afterwards()
    {
        await using var h = new TabTestHarness();
        AnswerConnected(h);
        var first = await h.OpenTabAsync();
        var settings = new SettingsViewModel(h.Services, null);

        settings.ClaudeCode.ConnectNewTabsToClaudeApp = true;

        Assert.True(h.Services.Settings.ClaudeCode.ConnectNewTabsToClaudeApp);
        Assert.False(first.RemoteControl.IsOn);
        Assert.Empty(RemoteRequests(h));

        await h.Shell.CloseTabCommand.ExecuteAsync(first);
        await TabTestHarness.Eventually(() => !h.Shell.HasTabs && !first.IsProcessRunning, "the first tab to close");
        var second = await h.OpenTabAsync();
        Assert.True(second.RemoteControl.IsOn);
        await TabTestHarness.Eventually(() => second.RemoteControl.Status.IsConnected, "the new tab to connect");

        // Turning it off doesn't change open tabs either.
        settings.ClaudeCode.ConnectNewTabsToClaudeApp = false;
        Assert.True(second.RemoteControl.IsOn);
        Assert.True(second.RemoteControl.Status.IsConnected);
    }

    [Fact]
    public async Task Tab_settings_turns_the_switch_on_and_Use_defaults_leaves_it()
    {
        await using var h = new TabTestHarness();
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        var settings = new TabSettingsViewModel(h.Services, tab, () => { });
        Assert.False(settings.RemoteControl);
        Assert.True(settings.CanChangeRemoteControl);

        settings.RemoteControl = true;
        settings.UseDefaultsCommand.Execute(null);
        await settings.ApplyCommand.ExecuteAsync(null);

        Assert.True(tab.RemoteControl.IsOn);
        Assert.False(tab.HasOverrides);
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");
        Assert.True(new TabSettingsViewModel(h.Services, tab, () => { }).RemoteControl);
    }

    [Fact]
    public async Task An_API_key_account_disables_the_switch_and_the_setting_with_the_reason()
    {
        await using var h = new TabTestHarness();
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        var account = new AccountViewModel(h.Services);

        account.Status = new AuthStatus(true, "api_key", null, null, null, null, null, null);

        const string reason = "Claude Code is signed in with an API key. Remote Control needs a claude.ai subscription sign-in.";
        Assert.False(h.Services.RemoteControl.IsAvailable);
        Assert.False(tab.RemoteControl.CanToggle);
        Assert.False(tab.RemoteControl.ToggleCommand.CanExecute(null));
        Assert.Equal(reason, tab.RemoteControl.ToggleTip);
        await tab.RemoteControl.SetAsync(true);
        Assert.False(tab.RemoteControl.IsOn);
        Assert.Empty(RemoteRequests(h));

        var tabSettings = new TabSettingsViewModel(h.Services, tab, () => { });
        Assert.False(tabSettings.CanChangeRemoteControl);
        Assert.Equal(reason, tabSettings.RemoteControlUnavailableText);
        using var settings = new SettingsViewModel(h.Services, null) { SelectedCategory = "Claude Code" };
        Assert.False(settings.ClaudeCode.CanUseRemoteControl);
        Assert.Equal($"Not available: {reason}", settings.ClaudeCode.RemoteControlUnavailableText);

        // Signing in with a subscription makes it available again, in the open Settings window too.
        account.Status = new AuthStatus(true, "claude.ai", "firstParty", "me@example.com", null, "max", null, null);
        Assert.True(tab.RemoteControl.CanToggle);
        Assert.True(tab.RemoteControl.ToggleCommand.CanExecute(null));
        Assert.True(settings.ClaudeCode.CanUseRemoteControl);
        Assert.False(settings.ClaudeCode.HasRemoteControlUnavailableText);
    }

    [Theory]
    [InlineData("third_party", null, null)]
    [InlineData("claude.ai", "bedrock", null)]
    [InlineData("claude.ai", null, "http://llm-gateway.internal:4000")]
    public void A_cloud_provider_or_a_custom_endpoint_isnt_eligible(string method, string? provider, string? baseUrl)
    {
        var reason = RemoteControlEligibility.Check(new AuthStatus(true, method, provider, null, null, null, null, null),
            name => name == "ANTHROPIC_BASE_URL" ? baseUrl : null);

        Assert.NotNull(reason);
        Assert.Null(RemoteControlEligibility.Check(new AuthStatus(true, "claude.ai", "firstParty", null, null, "max", null, null),
            name => name == "ANTHROPIC_BASE_URL" ? "https://api.anthropic.com" : null));
        Assert.Null(RemoteControlEligibility.Check(null, _ => null));
    }

    [Fact]
    public async Task A_tab_with_the_switch_on_that_the_account_cant_connect_says_why_and_sends_nothing()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        h.Services.RemoteControl.EnvironmentVariable = name => name == "ANTHROPIC_BASE_URL" ? "http://127.0.0.1:8787" : null;
        var tab = await h.OpenTabAsync();

        Assert.Equal(RemoteControlState.Unavailable, tab.RemoteControl.Status.State);
        Assert.Equal("ANTHROPIC_BASE_URL points Claude Code at 127.0.0.1. Remote Control only works through api.anthropic.com.", tab.RemoteControl.Status.Detail);
        Assert.Empty(RemoteRequests(h));
        Assert.StartsWith("Not connecting to the Claude app: ANTHROPIC_BASE_URL", InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last().Text), StringComparison.Ordinal);
        // It can still be turned off.
        Assert.True(tab.RemoteControl.CanToggle);
    }

    [Fact]
    public async Task A_rename_still_goes_to_Claude_Code_while_the_tab_is_connected()
    {
        await using var h = new TabTestHarness(s =>
        {
            s.ClaudeCode.ConnectNewTabsToClaudeApp = true;
            s.General.RenameInClaudeCode = true;
        });
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");

        tab.StartRenameCommand.Execute(null);
        tab.RenameText = "Fix the login bug";
        await tab.CommitRenameCommand.ExecuteAsync(null);

        var rename = h.Transport.Sent.Last(m => m["request"]?["subtype"]?.GetValue<string>() == "rename_session");
        Assert.Equal("Fix the login bug", rename["request"]!["title"]!.GetValue<string>());
        Assert.True(tab.RemoteControl.Status.IsConnected);
    }

    // ---- The /remote-control fallback ------------------------------------------------------------------------------

    [Fact]
    public async Task Without_the_request_the_hidden_slash_command_is_sent_and_its_reply_is_a_note()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        var tab = await OpenRejectingTheRequestAsync(h);

        Assert.Equal([$"/remote-control {tab.DisplayName}"], h.Transport.SentUserTexts);
        Assert.Equal(RemoteControlState.Connecting, tab.RemoteControl.Status.State);
        // Claude Code 2.1.284 in -p mode: a local command's reply, then a result with no turns.
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default"}""");
        h.Transport.Emit("""
            {"type":"assistant","message":{"model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"/remote-control isn't available in this environment."}]},
             "parent_tool_use_id":null,"local_command_source":"<local-command-stdout>/remote-control isn't available in this environment.</local-command-stdout>",
             "local_command_outcome":{"kind":"unavailable_headless"},"session_id":"s1"}
            """.ReplaceLineEndings(""));
        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"num_turns":0,"result":"/remote-control isn't available in this environment.","session_id":"s1","usage":{"input_tokens":0,"output_tokens":0},"modelUsage":{}}""");

        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.State == RemoteControlState.Unavailable && tab.Status == TabStatus.Idle, "the reply");
        Assert.Equal("/remote-control isn't available in this environment.", tab.RemoteControl.Status.Detail);
        InlineDispatcher.Read(() =>
        {
            // Neither the command nor its reply shows as a message, and it isn't a turn.
            Assert.Empty(tab.Items.OfType<UserMessageItem>());
            Assert.Empty(tab.Items.OfType<AssistantTextItem>());
            Assert.Empty(tab.Items.OfType<TurnSummaryItem>());
            Assert.Equal("Couldn't connect to the Claude app: /remote-control isn't available in this environment.", tab.Items.OfType<NoteItem>().Last().Text);
            return true;
        });
        Assert.Equal(0, tab.State.Tokens.Turns);
        Assert.Empty(h.Notifier.Shown);

        // The next turn is an ordinary one again.
        tab.ComposerText = "hello";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.EmitTurn("hi there");
        await TabTestHarness.Eventually(() => tab.Items.OfType<AssistantTextItem>().Any() && tab.Items.OfType<TurnSummaryItem>().Any(), "the reply");
    }

    [Fact]
    public async Task A_fallback_reply_with_a_session_address_connects()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        var tab = await OpenRejectingTheRequestAsync(h);

        h.Transport.EmitTurn($"Remote Control connected. Continue at {SessionUrl}.");

        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");
        Assert.Equal(SessionUrl, tab.RemoteControl.Status.Url);
        var note = InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last());
        Assert.Equal("Connected to the Claude app.", note.Text);
        Assert.Equal(SessionUrl, note.Link);
        Assert.Empty(InlineDispatcher.Read(() => tab.Items.OfType<AssistantTextItem>().ToList()));
    }

    // ---- Prompts answered on the phone -----------------------------------------------------------------------------

    [Fact]
    public async Task A_prompt_withdrawn_while_connected_reads_Answered_in_the_Claude_app()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");
        var prompt = await PromptAsync(h, tab, BashRequest);

        // The phone answered it: Claude Code withdraws Claudette's copy.
        h.Transport.Emit("""{"type":"control_cancel_request","request_id":"p1"}""");

        await TabTestHarness.Eventually(() => !prompt.IsPending, "the withdrawal");
        Assert.Equal(RemoteControlViewModel.AnsweredInClaudeApp, prompt.Outcome);
        Assert.Equal(PermissionState.Cancelled, prompt.State);
        await TabTestHarness.Eventually(() => tab.Status != TabStatus.NeedsInput, "the status to clear");
        // Not an error, and nothing is answered from here.
        Assert.DoesNotContain(h.Transport.Sent, m => m["type"]?.GetValue<string>() == "control_response");
    }

    [Fact]
    public async Task A_prompt_withdrawn_after_Stop_here_keeps_the_usual_words()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");
        var prompt = await PromptAsync(h, tab, BashRequest);

        await tab.StopCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"control_cancel_request","request_id":"p1"}""");

        await TabTestHarness.Eventually(() => !prompt.IsPending, "the withdrawal");
        Assert.Equal(PromptItem.WithdrawnOutcome, prompt.Outcome);
    }

    // ---- The presence file and keeping the computer awake -----------------------------------------------------------

    [Fact]
    public async Task The_presence_file_is_there_while_Claudette_is_in_front_and_every_claude_is_told_about_it()
    {
        await using var h = new TabTestHarness();
        var path = h.Services.Paths.PresenceFile;
        await h.OpenTabAsync();
        Assert.Equal(path, h.Factory.Launches.Single().EnvironmentOverrides[PresenceFile.Variable]);
        Assert.Equal(path, h.Services.RemoteControl.ClaudeVariables[PresenceFile.Variable]);
        Assert.False(File.Exists(path));

        h.Services.Notifications.SetAppActive(true);
        Assert.True(File.Exists(path));

        h.Services.Notifications.SetAppActive(false);
        Assert.False(File.Exists(path));

        h.Services.Notifications.SetAppActive(true);
        Assert.True(File.Exists(path));

        // Closing Claudette: nobody is at it any more.
        h.Services.RemoteControl.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task The_computer_is_kept_awake_while_a_tab_is_connected_if_Settings_allow()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.ConnectNewTabsToClaudeApp = true);
        AnswerConnected(h);
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.IsConnected, "the connection");
        Assert.True(h.SleepBlocker.IsBlocking);
        Assert.StartsWith("1 tab is connected to the Claude app.", h.Services.RemoteControl.DescribeKeepAwake(), StringComparison.Ordinal);

        using var settings = new SettingsViewModel(h.Services, null);
        settings.ClaudeCode.KeepAwakeWhileConnected = false;
        Assert.False(h.SleepBlocker.IsBlocking);
        Assert.Contains("turned off in Settings", settings.Advanced.KeepAwakeText, StringComparison.Ordinal);
        Assert.Contains("Keeping the computer awake:", settings.Advanced.DiagnosticsReport(includeHeader: true), StringComparison.Ordinal);

        settings.ClaudeCode.KeepAwakeWhileConnected = true;
        Assert.True(h.SleepBlocker.IsBlocking);

        // Closing the tab disconnects it.
        await h.Shell.CloseTabCommand.ExecuteAsync(tab);
        await TabTestHarness.Eventually(() => !h.SleepBlocker.IsBlocking, "the tab to close");
        Assert.Equal([true, false, true, false], h.SleepBlocker.Changes);

        // Closing Claudette lets go of the OS's side too.
        h.Services.RemoteControl.Dispose();
        Assert.True(h.SleepBlocker.IsDisposed);
    }

    [Fact]
    public async Task The_settings_reset_and_are_found_by_search()
    {
        await using var h = new TabTestHarness(s =>
        {
            s.ClaudeCode.ConnectNewTabsToClaudeApp = true;
            s.ClaudeCode.KeepAwakeWhileConnected = false;
        });
        using var settings = new SettingsViewModel(h.Services, null);
        Assert.True(settings.ClaudeCode.CanUseRemoteControl);

        settings.ClaudeCode.ResetCommand.Execute(null);

        Assert.False(h.Services.Settings.ClaudeCode.ConnectNewTabsToClaudeApp);
        Assert.True(h.Services.Settings.ClaudeCode.KeepAwakeWhileConnected);
        Assert.False(settings.ClaudeCode.ConnectNewTabsToClaudeApp);
        Assert.True(settings.ClaudeCode.KeepAwakeWhileConnected);
        Assert.Contains(settings.SearchResultsFor("claude app"), r => r is { Category: "Claude Code", Label: "Connect new tabs to the Claude app (Remote Control)" });
        Assert.Single(settings.SearchResultsFor("keep awake"));
        Assert.Single(settings.SearchResultsFor("push notifications"));

        await settings.ClaudeCode.OpenPushNotificationsDocsCommand.ExecuteAsync(null);
        Assert.Equal(["https://code.claude.com/docs/en/remote-control#mobile-push-notifications"], h.Platform.OpenedUrls);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private static void AnswerConnected(TabTestHarness h) =>
        h.Transport.Answers["remote_control"] = request => request["enabled"]!.GetValue<bool>()
            ? new JsonObject { ["session_url"] = SessionUrl, ["connect_url"] = "https://claude.ai/code?environment=env_1", ["environment_id"] = "env_1", ["bridge_session_id"] = "cse_1", ["bridge_epoch"] = 1 }
            : new JsonObject();

    /// <summary>
    /// A new tab whose Claude Code doesn't know the <c>remote_control</c> request. It's rejected once the tab has
    /// started, since the fallback's turn keeps the tab working until its reply.
    /// </summary>
    private static async Task<TabViewModel> OpenRejectingTheRequestAsync(TabTestHarness h)
    {
        h.Transport.Answers["remote_control"] = _ => null;
        var tab = await h.OpenTabAsync();
        var id = h.Transport.Sent.Single(IsRemoteRequest)["request_id"]!.GetValue<string>();
        h.Transport.Emit(Core.Protocol.OutgoingMessages.ControlError(id, "Unsupported control request subtype: remote_control"));
        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Any(), "the command");
        return tab;
    }

    private static bool IsRemoteRequest(JsonObject message) =>
        message["type"]?.GetValue<string>() == "control_request" && message["request"]?["subtype"]?.GetValue<string>() == "remote_control";

    private static List<JsonObject> RemoteRequests(TabTestHarness h) =>
        h.Transport.Sent.Where(IsRemoteRequest).Select(m => m["request"]!.AsObject()).ToList();

    private static string BridgeState(string state, string? detail = null) =>
        new JsonObject { ["type"] = "system", ["subtype"] = "bridge_state", ["state"] = state, ["detail"] = detail, ["session_id"] = "s1" }.ToJsonString();

    private static async Task<PermissionItem> PromptAsync(TabTestHarness h, TabViewModel tab, string request)
    {
        h.Transport.Emit(request.ReplaceLineEndings(""));
        await TabTestHarness.Eventually(() => tab.Items.OfType<PermissionItem>().Any(p => p.Request.RequestId == "p1"), "the prompt");
        return InlineDispatcher.Read(() => tab.Items.OfType<PermissionItem>().Single(p => p.Request.RequestId == "p1"));
    }
}
