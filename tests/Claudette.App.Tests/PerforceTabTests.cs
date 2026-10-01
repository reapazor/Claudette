using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Diffs;
using Claudette.Core.Perforce;
using Claudette.Core.Settings;
using Claudette.Testing;

namespace Claudette.App.Tests;

/// <summary>Perforce ticket handling and the changelist in the tab title, in a tab (DESIGN.md §18).</summary>
public class PerforceTabTests
{
    private const string StoredKey = "perforce/ssl:perforce:1666/matt";

    /// <summary>A harness whose processes are the fake p4, with ticket handling on unless told otherwise.</summary>
    private static (TabTestHarness H, FakeP4 P4, FakeCredentialStore Store) Harness(Action<PerforceSettings>? configure = null, bool enabled = true)
    {
        var p4 = new FakeP4();
        var h = new TabTestHarness(settings =>
        {
            settings.Perforce.Enabled = enabled;
            configure?.Invoke(settings.Perforce);
        }, launcher: p4);
        p4.Time = h.Time;
        p4.Root = h.WorkFolder;
        var store = new FakeCredentialStore();
        h.Services.Perforce.Credentials = store;
        h.Services.Perforce.Probe = new NoFiles();
        return (h, p4, store);
    }

    // ---- Detecting a workspace -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_tab_gets_the_note_the_hook_and_the_ticket_in_its_info_card()
    {
        var (h, p4, _) = Harness();
        await using var _h = h;
        p4.TicketExpires = h.Time.GetUtcNow().AddHours(11);

        var tab = await h.OpenTabAsync();

        var launch = h.Factory.Launches.Single();
        Assert.Contains("Perforce workspace", launch.AppendSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("ssl:perforce:1666", launch.AppendSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("matt-ws", launch.AppendSystemPrompt, StringComparison.Ordinal);
        var hook = Assert.Single(launch.Hooks);
        Assert.Equal(("PreToolUse", "Bash", PerforceService.HookTimeout), (hook.Event, hook.Matcher, hook.Timeout));
        var initialize = h.Transport.Sent.First(m => m["request"]?["subtype"]?.GetValue<string>() == "initialize");
        Assert.Equal("Bash", initialize["request"]!["hooks"]!["PreToolUse"]![0]!["matcher"]!.GetValue<string>());
        await TabTestHarness.Eventually(() => tab.InfoRows.Any(r => r.Label == "Perforce" && r.Value == "matt @ ssl:perforce:1666, ticket expires in 11h"), "the Perforce row");
        Assert.Empty(p4.Logins);
    }

    [Fact]
    public async Task Ticket_handling_is_off_by_default()
    {
        var (h, p4, _) = Harness(enabled: false);
        await using var _h = h;

        var tab = await h.OpenTabAsync();

        Assert.False(new AppSettings().Perforce.Enabled);
        Assert.Empty(p4.Runs);
        Assert.Null(h.Factory.Launches.Single().AppendSystemPrompt);
        Assert.Empty(h.Factory.Launches.Single().Hooks);
        Assert.DoesNotContain(tab.InfoRows, r => r.Label == "Perforce");
    }

    [Fact]
    public async Task A_folder_outside_a_workspace_gets_nothing()
    {
        var (h, p4, _) = Harness();
        await using var _h = h;
        p4.IsWorkspace = false;

        await h.OpenTabAsync();

        Assert.Single(p4.Runs, r => r.Command == "info");
        Assert.Empty(h.Factory.Launches.Single().Hooks);
        Assert.Equal(0, p4.StatusChecks);
    }

    [Fact]
    public async Task A_folder_override_changes_the_server_and_user()
    {
        var (h, p4, store) = Harness(configure: null);
        await using var _h = h;
        h.Services.Settings.Perforce.FolderOverrides.Add(new PerforceFolderOverride { Folder = h.Root, Server = "other:1666", User = "build" });
        store.Secrets["perforce/other:1666/build"] = "s3cret";

        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => p4.Logins.Count == 1, "the login");

        Assert.Equal(("other:1666", "build"), (p4.Runs.First(r => r.Command == "info").Port, p4.Runs.First(r => r.Command == "info").User));
        Assert.Equal(["-p", "other:1666", "-u", "build", "login"], p4.Logins.Single().Spec.Arguments);
        await TabTestHarness.Eventually(() => tab.PerforceStatusText?.StartsWith("build @ other:1666, ticket expires in", StringComparison.Ordinal) == true, "the status");
    }

    // ---- Keeping the ticket fresh ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_stored_password_renews_the_ticket_when_the_tab_starts()
    {
        var (h, p4, store) = Harness();
        await using var _h = h;
        store.Secrets[StoredKey] = "s3cret";

        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => p4.Logins.Count == 1, "the login");

        var login = p4.Logins.Single();
        Assert.Equal(["s3cret"], login.Input);
        Assert.DoesNotContain(p4.Runs.SelectMany(r => r.Spec.Arguments), a => a.Contains("s3cret", StringComparison.Ordinal));
        Assert.Null(tab.PerforcePrompt);
    }

    [Fact]
    public async Task The_hook_renews_an_expired_ticket_before_a_p4_command_and_never_changes_it()
    {
        var (h, p4, store) = Harness();
        await using var _h = h;
        p4.TicketExpires = h.Time.GetUtcNow().AddHours(11);
        store.Secrets[StoredKey] = "s3cret";
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => p4.StatusChecks == 1 && tab.PerforceStatusText?.Contains("11h", StringComparison.Ordinal) == true, "the first check");
        // Later, the ticket has gone (for example p4 logout in a terminal).
        h.Time.Advance(TimeSpan.FromMinutes(6));
        p4.TicketExpires = h.Time.GetUtcNow().AddMinutes(-1);

        h.Transport.Emit(HookCallback("hc1", "cd src && p4 edit a.cpp"));
        var answer = await WaitForAnswerAsync(h, "hc1");

        Assert.Equal("""{"continue":true}""", answer.ToJsonString());
        Assert.Equal(["s3cret"], p4.Logins.Single().Input);
    }

    [Fact]
    public async Task The_hook_lets_other_commands_straight_through()
    {
        var (h, p4, _) = Harness();
        await using var _h = h;
        p4.TicketExpires = h.Time.GetUtcNow().AddHours(11);
        await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => p4.StatusChecks == 1, "the first check");

        h.Transport.Emit(HookCallback("hc2", "ls -la && echo p4"));
        var answer = await WaitForAnswerAsync(h, "hc2");

        Assert.Equal("""{"continue":true}""", answer.ToJsonString());
        Assert.Equal(1, p4.StatusChecks);
    }

    [Fact]
    public async Task A_failed_p4_command_logs_in_again_and_asks_Claude_to_retry_once()
    {
        var (h, p4, store) = Harness();
        await using var _h = h;
        p4.TicketExpires = h.Time.GetUtcNow().AddHours(11);
        store.Secrets[StoredKey] = "s3cret";
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.PerforceStatusText?.Contains("11h", StringComparison.Ordinal) == true, "the first check");
        h.Transport.Emit(Init());
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "the turn");
        // Whether the turn's own check sees it or recovery does, there's one login and one retry message.
        p4.TicketExpires = h.Time.GetUtcNow().AddMinutes(-1);

        EmitBash(h, "b1", "p4 sync //depot/...", "Your session has expired, please login again.", isError: true);
        await TabTestHarness.Eventually(() => h.Transport.SentUserTexts.Contains(TabViewModel.PerforceRetryMessage), "the retry message");
        EmitBash(h, "b2", "p4 sync //depot/...", "Your session has expired, please login again.", isError: true);
        await Waiting.NeverAsync(() => p4.Logins.Count > 1 || h.Transport.SentUserTexts.Count(t => t == TabViewModel.PerforceRetryMessage) > 1,
            "a second login or retry message");

        Assert.Single(p4.Logins);
        Assert.Single(h.Transport.SentUserTexts, t => t == TabViewModel.PerforceRetryMessage);
        Assert.Contains(tab.Items.OfType<NoteItem>(), n => n.Text.Contains("retry the last p4 command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ask_each_time_shows_a_prompt_and_logs_in_with_the_answer()
    {
        var (h, p4, store) = Harness(p => p.PasswordSource = PerforcePasswordSource.AskEachTime);
        await using var _h = h;

        var tab = await OpenAsync(h);
        await TabTestHarness.Eventually(() => tab.PerforcePrompt is not null && tab.Status == TabStatus.NeedsInput, "the prompt");

        var prompt = tab.PerforcePrompt!;
        Assert.False(prompt.OfferToSave);
        Assert.Contains("matt @ ssl:perforce:1666", prompt.Message, StringComparison.Ordinal);
        Assert.Equal($"Perforce needs your password to log in as matt @ ssl:perforce:1666.", h.Notifier.Last("NeedsInput:")?.Body);
        Assert.Equal(1, h.Notifier.Badge);

        InlineDispatcher.Read(() =>
        {
            prompt.Password = "s3cret";
            prompt.LogInCommand.Execute(null);
            return 0;
        });
        await TabTestHarness.Eventually(() => tab.PerforcePrompt is null && tab.Status == TabStatus.Idle, "the prompt to close");
        await TabTestHarness.Eventually(() => p4.Logins.Count == 1, "the login");

        Assert.Equal(["s3cret"], p4.Logins.Single().Input);
        Assert.Empty(store.Secrets);
        Assert.Equal("", prompt.Password);
    }

    [Fact]
    public async Task A_first_stored_login_asks_and_saves_the_password()
    {
        var (h, p4, store) = Harness();
        await using var _h = h;

        var tab = await OpenAsync(h);
        await TabTestHarness.Eventually(() => tab.PerforcePrompt is not null, "the prompt");
        var prompt = tab.PerforcePrompt!;
        Assert.True(prompt.OfferToSave);
        Assert.Equal("Save it in Test Keychain", prompt.SaveText);

        Answer(prompt, "s3cret");
        await TabTestHarness.Eventually(() => store.Secrets.ContainsKey(StoredKey), "the saved password");

        Assert.Equal("s3cret", store.Secrets[StoredKey]);
        Assert.Equal("Claudette: Perforce matt @ ssl:perforce:1666", store.Labels[StoredKey]);
        Assert.Single(p4.Logins);
    }

    [Fact]
    public async Task A_refused_password_asks_again_with_the_reason()
    {
        var (h, p4, _) = Harness(p => p.PasswordSource = PerforcePasswordSource.AskEachTime);
        await using var _h = h;
        var tab = await OpenAsync(h);
        await TabTestHarness.Eventually(() => tab.PerforcePrompt is not null, "the prompt");
        var first = tab.PerforcePrompt!;

        Answer(first, "wrong");
        await TabTestHarness.Eventually(() => tab.PerforcePrompt is { } p && p != first, "the second prompt");

        Assert.Equal("Password invalid. Try again.", tab.PerforcePrompt!.Error);
        Answer(tab.PerforcePrompt!, "s3cret");
        await TabTestHarness.Eventually(() => tab.PerforcePrompt is null && p4.Logins.Count == 2, "the second login");
        Assert.Equal(TabStatus.Idle, tab.Status);
    }

    [Fact]
    public async Task Cancelling_the_prompt_leaves_a_note()
    {
        var (h, p4, _) = Harness(p => p.PasswordSource = PerforcePasswordSource.AskEachTime);
        await using var _h = h;
        var tab = await OpenAsync(h);
        await TabTestHarness.Eventually(() => tab.PerforcePrompt is not null, "the prompt");

        InlineDispatcher.Read(() =>
        {
            tab.PerforcePrompt!.CancelCommand.Execute(null);
            return 0;
        });

        await TabTestHarness.Eventually(() => tab.Items.OfType<NoteItem>().Any(n => n.Text.Contains("password prompt was cancelled", StringComparison.Ordinal)), "the note");
        Assert.Null(tab.PerforcePrompt);
        Assert.Empty(p4.Logins);
        Assert.Equal(TabStatus.Idle, tab.Status);
    }

    [Fact]
    public async Task The_P4PASSWD_source_reads_Perforces_own_configuration()
    {
        var (h, p4, _) = Harness(p => p.PasswordSource = PerforcePasswordSource.PerforceConfig);
        await using var _h = h;
        p4.ConfiguredPassword = "s3cret";

        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => p4.Logins.Count == 1, "the login");

        Assert.Contains(p4.Runs, r => r.Command == "set" && r.Arguments.SequenceEqual(["-q", "P4PASSWD"]));
        Assert.Equal(["s3cret"], p4.Logins.Single().Input);
        Assert.Null(tab.PerforcePrompt);
    }

    [Fact]
    public async Task Without_P4PASSWD_the_config_source_says_so()
    {
        var (h, p4, _) = Harness(p => p.PasswordSource = PerforcePasswordSource.PerforceConfig);
        await using var _h = h;

        var tab = await h.OpenTabAsync();

        await TabTestHarness.Eventually(() => tab.Items.OfType<NoteItem>().Any(n => n.Text.Contains("P4PASSWD isn't set", StringComparison.Ordinal)), "the note");
        Assert.Empty(p4.Logins);
    }

    [Fact]
    public async Task Single_sign_on_asks_the_user_to_log_in_themselves()
    {
        var (h, p4, _) = Harness();
        await using var _h = h;
        p4.SingleSignOn = true;

        var tab = await h.OpenTabAsync();

        await TabTestHarness.Eventually(() => h.Notifier.Last("NeedsInput:") is not null, "the notification");
        Assert.Equal("Perforce needs you to log in: run p4 login in a terminal, or log in with P4V.", h.Notifier.Last("NeedsInput:")!.Body);
        Assert.Contains(tab.Items.OfType<NoteItem>(), n => n.Text.Contains("Claudette checks again every minute", StringComparison.Ordinal));
        Assert.Contains("log in yourself", tab.PerforceStatusText, StringComparison.Ordinal);
        Assert.Empty(p4.Logins);
        Assert.Null(tab.PerforcePrompt);
    }

    // ---- The changelist ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_changelist_is_in_the_info_card_and_on_the_tab_when_turned_on()
    {
        var (h, _, _) = Harness(enabled: false);
        await using var _h = h;
        var tab = await h.OpenTabAsync();

        EmitBash(h, "b1", "p4 edit -c 12345 src/login.cpp", "//depot/src/login.cpp#7 - opened for edit");
        await TabTestHarness.Eventually(() => tab.ChangelistBadge == "CL 12345", "the changelist");

        Assert.False(tab.ShowChangelistBadge);
        Assert.Contains(tab.InfoRows, r => r is { Label: "Changelist", Value: "CL 12345" });
        Assert.Equal(12345, tab.State.Changelists.Single().Number);

        InlineDispatcher.Read(() =>
        {
            h.Services.Settings.Perforce.ShowChangelistOnTabs = true;
            h.Services.SaveSettings();
            return 0;
        });
        Assert.True(tab.ShowChangelistBadge);
        Assert.Equal("fix login bug", InlineDispatcher.Read(() =>
        {
            tab.StartRenameCommand.Execute(null);
            tab.RenameText = "fix login bug";
            tab.CommitRenameCommand.Execute(null);
            return tab.DisplayName;
        }));
        Assert.Equal("CL 12345", tab.ChangelistBadge);
    }

    [Fact]
    public async Task Several_changelists_are_all_listed_and_submitting_or_deleting_updates_the_badge()
    {
        var (h, _, _) = Harness(p => p.ShowChangelistOnTabs = true, enabled: false);
        await using var _h = h;
        var tab = await h.OpenTabAsync();

        EmitBash(h, "b1", "p4 change -i < spec", "Change 100 created with 1 open file(s).");
        EmitBash(h, "b2", "p4 reopen -c 200 a.cpp", "//depot/a.cpp#1 - reopened; change 200");
        await TabTestHarness.Eventually(() => tab.ChangelistBadge == "CL 200", "the second changelist");
        Assert.Equal("CL 200; earlier: CL 100", tab.InfoRows.Single(r => r.Label == "Changelist").Value);

        EmitBash(h, "b3", "p4 submit -c 200", "Submitting change 200.\nChange 200 submitted.");
        await TabTestHarness.Eventually(() => tab.ChangelistBadge == "CL 200 · submitted", "submitted");

        EmitBash(h, "b4", "p4 change -d 100", "Change 100 deleted.");
        await TabTestHarness.Eventually(() => tab.ChangelistBadge is null, "the badge to go");
        Assert.False(tab.ShowChangelistBadge);
        Assert.Equal(["CL 200 · submitted"], tab.ChangelistRows.Select(r => r.Text));
    }

    [Fact]
    public async Task A_restored_tab_shows_its_changelist_again()
    {
        var (h, _, _) = Harness(p => p.ShowChangelistOnTabs = true, enabled: false);
        await using var _h = h;
        h.Services.State.Tabs = [new TabState
        {
            Folder = h.WorkFolder,
            IsPinned = true,
            Changelists = [new TrackedChangelist { Number = 777, State = ChangelistState.Pending, LastUsed = h.Time.GetUtcNow() }],
        }];

        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        Assert.Equal("CL 777", tab.ChangelistBadge);
        Assert.True(tab.ShowChangelistBadge);
        var saved = JsonFileStoreRoundTrip(h.Services.State);
        Assert.Equal(777, saved.Tabs.Single().Changelists.Single().Number);
    }

    [Fact]
    public async Task The_changelist_can_be_copied_and_opened_in_P4V()
    {
        var (h, p4, _) = Harness();
        await using var _h = h;
        p4.TicketExpires = h.Time.GetUtcNow().AddHours(11);
        var tab = await h.OpenTabAsync();
        EmitBash(h, "b1", "p4 shelve -c 12345", "Change 12345 files shelved.");
        await TabTestHarness.Eventually(() => tab.ChangelistBadge == "CL 12345", "the changelist");

        await InlineDispatcher.Read(() => tab.CopyChangelistCommand.ExecuteAsync(null));
        InlineDispatcher.Read(() =>
        {
            tab.OpenChangelistInP4VCommand.Execute(12345L);
            return 0;
        });

        Assert.Equal("12345", h.Platform.Clipboard);
        var p4v = p4.Started.Single(s => s.FileName == "p4v");
        Assert.Equal(["-p", "ssl:perforce:1666", "-u", "matt", "-c", "matt-ws", "-cmd", "open changelist 12345"], p4v.Arguments);
        Assert.True(p4v.Detached);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>Opens the tab without waiting for it to be idle: a password prompt can come first.</summary>
    private static async Task<TabViewModel> OpenAsync(TabTestHarness h)
    {
        await h.Shell.OpenFolderAsync(h.WorkFolder);
        var tab = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => tab.Status is TabStatus.Idle or TabStatus.NeedsInput, "the tab to start");
        return tab;
    }

    private static AppState JsonFileStoreRoundTrip(AppState state) =>
        System.Text.Json.JsonSerializer.Deserialize<AppState>(JsonFileStore<AppState>.Serialize(state), JsonFileStore<AppState>.Options)!;

    private static void Answer(PerforcePasswordPrompt prompt, string password) => InlineDispatcher.Read(() =>
    {
        prompt.Password = password;
        prompt.LogInCommand.Execute(null);
        return 0;
    });

    private static JsonObject Init() => new() { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "s1", ["model"] = "claude-opus-5-5", ["permissionMode"] = "default" };

    private static string HookCallback(string requestId, string command) => new JsonObject
    {
        ["type"] = "control_request",
        ["request_id"] = requestId,
        ["request"] = new JsonObject
        {
            ["subtype"] = "hook_callback",
            ["callback_id"] = "hook_0",
            ["input"] = new JsonObject
            {
                ["hook_event_name"] = "PreToolUse",
                ["tool_name"] = "Bash",
                ["tool_input"] = new JsonObject { ["command"] = command },
                ["tool_use_id"] = "toolu_" + requestId,
            },
            ["tool_use_id"] = "toolu_" + requestId,
        },
    }.ToJsonString();

    private static async Task<JsonObject> WaitForAnswerAsync(TabTestHarness h, string requestId)
    {
        JsonObject? answer = null;
        await TabTestHarness.Eventually(() => (answer = h.Transport.Sent.LastOrDefault(m =>
            m["type"]?.GetValue<string>() == "control_response" && m["response"]?["request_id"]?.GetValue<string>() == requestId)) is not null, "the hook's answer");
        Assert.Equal("success", answer!["response"]!["subtype"]!.GetValue<string>());
        return answer["response"]!["response"]!.AsObject();
    }

    /// <summary>A Bash tool call and its result, as Claude Code reports them.</summary>
    private static void EmitBash(TabTestHarness h, string id, string command, string output, bool isError = false)
    {
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = command } }) },
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = output, ["is_error"] = isError }) },
            ["tool_use_result"] = new JsonObject { ["stdout"] = isError ? "" : output, ["stderr"] = isError ? output : "", ["interrupted"] = false },
        });
    }

    private sealed class NoFiles : IFileProbe
    {
        public bool FileExists(string path) => false;

        public string? FindOnPath(string fileName) => null;

        public string ExpandEnvironmentVariables(string path) => path;
    }
}
