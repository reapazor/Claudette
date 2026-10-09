using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Composer;

namespace Claudette.App.Tests;

/// <summary>
/// A tab among the Claude Code sessions on this machine, which message each other: its session named after the tab
/// (DESIGN.md §13, "Session naming"), its ID and the name it's reached by on the info card, and the sessions and
/// agents the composer's <c>@</c> names (DESIGN.md §5, "Autocomplete"). Claude Code's sessions folder is a temporary
/// one with entries written here.
/// </summary>
public class MessagingTests
{
    // ---- Naming the session after the tab ------------------------------------------------------------------------

    [Fact]
    public async Task A_rename_names_the_session_and_every_later_start_passes_the_name()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        Assert.Null(h.Factory.Launches[0].Name);

        await RenameAsync(tab, "Art page");

        Assert.Equal("Art page", LastRename(h));
        // Claude Code forgets the name when a session resumes, so it's given again.
        h.Transport.Exit(1);
        await TabTestHarness.Eventually(() => tab.CanRestart, "the exit");
        await tab.RestartCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2 && tab.Status == TabStatus.Idle && tab.IsSettled, "the restart");
        Assert.Equal("Art page", h.Factory.Launches[1].Name);
        Assert.Equal(1, h.Transport.SentControlSubtypes.Count(s => s == "rename_session"));
    }

    [Fact]
    public async Task The_generated_name_goes_to_Claude_Code_too()
    {
        await using var h = new TabTestHarness();
        h.Transport.Answers["generate_session_title"] = _ => new JsonObject { ["title"] = "Fix the login bug" };
        var tab = await h.OpenTabAsync();

        tab.ComposerText = "the login page rejects good passwords";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.EmitTurn();

        await TabTestHarness.Eventually(() => LastRename(h) == "Fix the login bug", "the rename");
        Assert.Equal("Fix the login bug", tab.DisplayName);
    }

    [Fact]
    public async Task Turned_off_Claude_Code_names_the_session_after_its_folder()
    {
        await using var h = new TabTestHarness(s => s.General.RenameInClaudeCode = false);
        var tab = await h.OpenTabAsync();

        await RenameAsync(tab, "Art page");
        h.Transport.Exit(1);
        await TabTestHarness.Eventually(() => tab.CanRestart, "the exit");
        await tab.RestartCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 2 && tab.Status == TabStatus.Idle && tab.IsSettled, "the restart");

        Assert.DoesNotContain("rename_session", h.Transport.SentControlSubtypes);
        Assert.Null(h.Factory.Launches[1].Name);
    }

    [Fact]
    public async Task Turned_on_a_running_session_takes_the_tabs_name_at_once()
    {
        await using var h = new TabTestHarness(s => s.General.RenameInClaudeCode = false);
        var tab = await h.OpenTabAsync();
        await RenameAsync(tab, "Art page");

        h.Services.Settings.General.RenameInClaudeCode = true;
        h.Services.SaveSettings();

        await TabTestHarness.Eventually(() => LastRename(h) == "Art page", "the rename");
    }

    [Fact]
    public void The_setting_is_on_for_a_new_install() => Assert.True(new Core.Settings.AppSettings().General.RenameInClaudeCode);

    // ---- The info card -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_info_card_shows_the_session_and_the_name_it_is_reached_by()
    {
        await using var h = new TabTestHarness();
        var sessions = new SessionsFolder(h);
        // Claude Code gave the name a variant: another session had it.
        sessions.Add(4242, "s1", "Art page-joyful-stearns", h.WorkFolder);
        var tab = await h.OpenTabAsync();

        h.Transport.EmitTurn();

        // Found by the process from its start; its ID comes with the turn.
        await TabTestHarness.Eventually(() => Row(tab, "Reached as") == "Art page-joyful-stearns" && Row(tab, "Session") == "s1", "the session's ID and name");
        await tab.CopyInfoRowCommand.ExecuteAsync(tab.InfoRows.Single(r => r.Label == "Session"));
        Assert.Equal("s1", h.Platform.Clipboard);
        Assert.False(tab.InfoRows.Single(r => r.Label == "Folder").CanCopy);
    }

    // ---- Mentions ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_at_lists_the_other_tabs_the_other_sessions_and_the_subagents()
    {
        await using var h = new TabTestHarness();
        var (plan, _) = await TwoTabsAsync(h);

        plan.Completions.Update("tell @", 6);

        await TabTestHarness.Eventually(() => Titles(plan).Contains("api-worker"), "the other sessions");
        Assert.Equal(["Art page", "api-worker", "Find the loader"], Titles(plan).Take(3));
        Assert.Equal(["Tab", @"Session · C:\work\api", "Subagent · Explore"], plan.Completions.Items.Take(3).Select(i => i.Detail));
        Assert.Equal("Sessions and files", plan.Completions.Header);
        // Not this tab's own session, nor Claudette's utility session.
        Assert.DoesNotContain("Plan", Titles(plan));
        Assert.DoesNotContain("utility-64", Titles(plan));

        plan.Completions.Update("tell @art", 9);
        var edit = plan.Completions.Accept();
        Assert.Equal("tell @\"Art page\" ", edit!.Value.Text);
    }

    [Fact]
    public async Task A_mention_reaches_Claude_without_its_at_and_with_how_to_message_it()
    {
        await using var h = new TabTestHarness();
        var (plan, _) = await TwoTabsAsync(h);
        await TabTestHarness.Eventually(() => plan.MentionTargets().Any(t => t.Kind == MentionKind.Subagent), "the subagent");

        plan.ComposerText = "tell @\"Art page\" the build is green, and @\"Find the loader\" to stop";
        await plan.SendCommand.ExecuteAsync(null);

        var sent = h.Transport.SentUserTextsTo(0).Last();
        Assert.StartsWith("tell \"Art page\" the build is green, and \"Find the loader\" to stop", sent, StringComparison.Ordinal);
        Assert.EndsWith(SessionMentions.Note([
            new MentionTarget("Art page", "Art page", MentionKind.Tab),
            new MentionTarget("Find the loader", "task-a", MentionKind.Subagent, "Explore")]), sent, StringComparison.Ordinal);
        var card = plan.Items.OfType<UserMessageItem>().Last();
        Assert.Equal("tell @\"Art page\" the build is green, and @\"Find the loader\" to stop", card.Text);
        Assert.Equal("Told Claude how to reach Art page and Find the loader", card.MentionNoteText);
        Assert.False(card.HasSuffix);
    }

    [Fact]
    public async Task A_message_without_mentions_goes_as_typed()
    {
        await using var h = new TabTestHarness();
        var (plan, _) = await TwoTabsAsync(h);

        plan.ComposerText = "mail me@example.com about @src/main.cs";
        await plan.SendCommand.ExecuteAsync(null);

        Assert.Equal("mail me@example.com about @src/main.cs", h.Transport.SentUserTextsTo(0).Last());
        Assert.False(plan.Items.OfType<UserMessageItem>().Last().HasMentionNote);
    }

    [Fact]
    public async Task A_thread_names_its_sub_threads_as_it_knows_them()
    {
        await using var h = new TabTestHarness();
        var (plan, art) = await TwoTabsAsync(h);
        h.Shell.MakeThreadCommand.Execute(plan);
        art.AssignToThreadCommand.Execute(plan);

        var target = plan.MentionTargets().Single(t => t.Name == "Art page");

        Assert.Equal(new MentionTarget("Art page", "Art page", MentionKind.SubThread), target);
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Two tabs, each on a process of its own: Plan, which ran a subagent, and Art page, whose session Claude Code lists
    /// with another session and Claudette's utility session. The processes share a PID here, so tabs are found by ID.
    /// </summary>
    private static async Task<(TabViewModel Plan, TabViewModel Art)> TwoTabsAsync(TabTestHarness h)
    {
        h.Factory.ProcessPerSession = true;
        h.Transport.ProcessId = null;
        var sessions = new SessionsFolder(h);
        sessions.Add(1, "s-plan", "Plan", h.WorkFolder);
        sessions.Add(2, "s-art", "Art page", h.WorkFolder);
        sessions.Add(3, "s-other", "api-worker", @"C:\work\api");
        sessions.Add(4, "s-utility", "utility-64", h.Services.Paths.UtilityDirectory);
        var plan = await ThreadScript.OpenAsync(h, "Plan");
        var art = await ThreadScript.OpenAsync(h, "Art page");
        h.Transport.EmitTo(0, Init("s-plan"));
        h.Transport.EmitTo(0, Wire.Agent("a1", "Find the loader", "Find where the config loads", "Explore"));
        h.Transport.EmitTo(0, Wire.TaskStarted("task-a", "a1"));
        h.Transport.EmitTo(1, Init("s-art"));
        await TabTestHarness.Eventually(() => plan.State.SessionId == "s-plan" && art.State.SessionId == "s-art" && plan.Agents.HasSubagents, "both sessions");
        h.Services.LiveSessions.Refresh();
        await TabTestHarness.Eventually(() => art.ReachedAs == "Art page", "the sessions folder");
        return (plan, art);
    }

    private static string Init(string sessionId) =>
        new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = sessionId, ["model"] = "claude-opus-5-5", ["permissionMode"] = "default" }.ToJsonString();

    private static async Task RenameAsync(TabViewModel tab, string name)
    {
        tab.StartRenameCommand.Execute(null);
        tab.RenameText = name;
        await tab.CommitRenameCommand.ExecuteAsync(null);
    }

    private static string? LastRename(TabTestHarness h) =>
        h.Transport.Sent.LastOrDefault(m => m["request"]?["subtype"]?.GetValue<string>() == "rename_session")?["request"]?["title"]?.GetValue<string>();

    private static string? Row(TabViewModel tab, string label) => tab.InfoRows.FirstOrDefault(r => r.Label == label)?.Value;

    private static List<string> Titles(TabViewModel tab) => [.. tab.Completions.Items.Select(i => i.Title)];

    /// <summary>Claude Code's sessions folder, in a config folder of the test's own.</summary>
    private sealed class SessionsFolder
    {
        private readonly string _folder;

        public SessionsFolder(TabTestHarness h)
        {
            h.Services.ClaudeConfigDirectory = Path.Combine(h.Root, "claude-config");
            _folder = Path.Combine(h.Services.ClaudeConfigDirectory, "sessions");
            Directory.CreateDirectory(_folder);
        }

        public void Add(int pid, string sessionId, string name, string cwd) =>
            File.WriteAllText(Path.Combine(_folder, $"{pid}.json"), new JsonObject
            {
                ["pid"] = pid, ["sessionId"] = sessionId, ["cwd"] = cwd, ["name"] = name, ["nameSource"] = "user", ["status"] = "idle",
            }.ToJsonString());
    }
}
