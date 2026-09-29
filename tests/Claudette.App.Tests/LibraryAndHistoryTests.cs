using System.Globalization;
using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Library;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>The session library, leases and History, wired into tabs (DESIGN.md §9).</summary>
public class LibraryAndHistoryTests
{
    [Fact]
    public async Task A_finished_turn_is_copied_to_the_library()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.WriteTranscript("s1", UserLine("s1", "Fix the login bug", h.WorkFolder));
        tab.State.UserName = "Login fix";

        h.Transport.EmitTurn();

        // The lease is taken last, after the transcript and the record are written: wait for the whole save.
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return h.Services.Library.CheckLease("s1") is LeaseStatus.Mine;
        }, "the library copy");
        Assert.NotNull(h.Services.Library.Library.GetTranscriptPath("s1"));
        var entry = Assert.Single(h.Services.Library.Library.List());
        Assert.Equal("Login fix", entry.Record.Name);
        Assert.Equal(h.Services.Library.MachineName, entry.Record.Machine);
        Assert.Equal(120, entry.Record.Tokens.Total);
        Assert.IsType<LeaseStatus.Mine>(h.Services.Library.CheckLease("s1"));
    }

    [Fact]
    public async Task History_lists_local_sessions_and_resumes_one()
    {
        await using var h = new TabTestHarness();
        h.WriteTranscript("old-1", UserLine("old-1", "Refactor the parser", h.WorkFolder), """{"type":"ai-title","aiTitle":"Parser refactor","sessionId":"old-1"}""");
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");

        var entry = Assert.Single(Assert.Single(history.Groups).Entries);
        Assert.Equal("Parser refactor", entry.Title);
        Assert.Equal("This machine", entry.Machine);

        await history.OpenCommand.ExecuteAsync(entry);

        Assert.False(h.Shell.IsHistoryOpen);
        var tab = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 1, "the resume");
        Assert.Equal("old-1", h.Factory.Launches[0].Resume);
        Assert.Contains(tab.Items, i => i is Conversation.UserMessageItem { Text: "Refactor the parser" });
        // When the session began, not when this tab started its process.
        var started = DateTimeOffset.Parse("2026-09-28T10:00:00Z", CultureInfo.InvariantCulture);
        Assert.Equal(started, tab.State.SessionStartedAt);
        Assert.Contains(tab.InfoRows, r => r.Label == "Started" && r.Value == started.ToLocalTime().ToString("g"));
    }

    [Fact]
    public async Task A_library_session_from_another_machine_resumes_from_a_local_copy()
    {
        await using var h = new TabTestHarness();
        await SaveToLibraryAsync(h, "lib-1", "DESKTOP-01");
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");
        var entry = history.Groups.SelectMany(g => g.Entries).Single();
        Assert.Equal("DESKTOP-01", entry.Machine);
        Assert.False(entry.IsLocal);

        await history.OpenCommand.ExecuteAsync(entry);

        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 1, "the resume");
        var resume = h.Factory.Launches[0].Resume!;
        Assert.StartsWith(h.Services.Paths.LocalSessionsDirectory, resume, StringComparison.Ordinal);
        Assert.EndsWith("lib-1.jsonl", resume, StringComparison.Ordinal);
        Assert.False(h.Factory.Launches[0].ForkSession);
    }

    [Fact]
    public async Task A_session_open_on_another_machine_can_be_opened_as_a_copy()
    {
        await using var h = new TabTestHarness();
        await SaveToLibraryAsync(h, "lib-2", "DESKTOP-01");
        var lease = Path.Combine(h.Services.Library.Library.GetSessionFolder("lib-2"), LeaseManager.FileName);
        await File.WriteAllTextAsync(lease, new JsonObject { ["machine"] = "DESKTOP-01", ["owner"] = "other", ["updatedAt"] = h.Time.GetUtcNow().ToString("O") }.ToJsonString(), TestContext.Current.CancellationToken);
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");

        await history.OpenCommand.ExecuteAsync(history.Groups.SelectMany(g => g.Entries).Single());

        var confirmation = Assert.IsType<ConfirmationViewModel>(h.Shell.Confirmation);
        Assert.Contains("DESKTOP-01", confirmation.Title, StringComparison.Ordinal);
        Assert.Equal("Take over", confirmation.ConfirmText);
        Assert.Equal("Open a copy", confirmation.SecondaryText);

        await confirmation.SecondaryCommand.ExecuteAsync(null);

        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 1, "the resume");
        Assert.True(h.Factory.Launches[0].ForkSession);
    }

    [Fact]
    public async Task A_tab_taken_over_elsewhere_becomes_read_only()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder));
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return h.Services.Library.Leases.HeldSessions.Contains("s1");
        }, "the lease");

        var lease = Path.Combine(h.Services.Library.Library.GetSessionFolder("s1"), LeaseManager.FileName);
        await File.WriteAllTextAsync(lease, new JsonObject { ["machine"] = "LAPTOP-02", ["owner"] = "other", ["updatedAt"] = h.Time.GetUtcNow().ToString("O") }.ToJsonString(), TestContext.Current.CancellationToken);
        h.Services.Library.Leases.RefreshAll();

        await TabTestHarness.Eventually(() => tab.IsReadOnly, "read-only");
        Assert.Equal("LAPTOP-02", tab.TakenOverBy);
        tab.ComposerText = "more";
        Assert.False(tab.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Opening_a_library_session_takes_its_lease_as_it_starts()
    {
        await using var h = new TabTestHarness();
        await SaveToLibraryAsync(h, "lib-3", "DESKTOP-01");
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");

        await history.OpenCommand.ExecuteAsync(history.Groups.SelectMany(g => g.Entries).Single());

        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 1, "the resume");
        Assert.IsType<LeaseStatus.Mine>(h.Services.Library.CheckLease("lib-3"));
    }

    [Fact]
    public async Task A_session_on_this_machine_that_another_machine_has_open_asks_first()
    {
        await using var h = new TabTestHarness();
        h.WriteTranscript("both-1", UserLine("both-1", "Add the export button", h.WorkFolder));
        await SaveToLibraryAsync(h, "both-1", "DESKTOP-01", lastUsed: DateTimeOffset.Parse("2026-09-28T09:00:00Z", CultureInfo.InvariantCulture));
        var lease = Path.Combine(h.Services.Library.Library.GetSessionFolder("both-1"), LeaseManager.FileName);
        await File.WriteAllTextAsync(lease, new JsonObject { ["machine"] = "DESKTOP-01", ["owner"] = "other", ["updatedAt"] = h.Time.GetUtcNow().ToString("O") }.ToJsonString(), TestContext.Current.CancellationToken);
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");
        var entry = history.Groups.SelectMany(g => g.Entries).Single();
        Assert.True(entry.IsLocal);
        Assert.False(entry.ContinuedElsewhere);

        await history.OpenCommand.ExecuteAsync(entry);

        var confirmation = Assert.IsType<ConfirmationViewModel>(h.Shell.Confirmation);
        Assert.Empty(h.Factory.Launches);
        await confirmation.ConfirmCommand.ExecuteAsync(null);

        // Taken over: resumed from this machine's own transcript, with the lease now held here.
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 1, "the resume");
        Assert.Equal("both-1", h.Factory.Launches[0].Resume);
        Assert.IsType<LeaseStatus.Mine>(h.Services.Library.CheckLease("both-1"));
    }

    [Fact]
    public async Task A_session_continued_on_another_machine_opens_from_the_library()
    {
        await using var h = new TabTestHarness();
        h.WriteTranscript("both-2", UserLine("both-2", "Add the export button", h.WorkFolder));
        await SaveToLibraryAsync(h, "both-2", "DESKTOP-01", lastUsed: DateTimeOffset.Parse("2026-09-28T12:00:00Z", CultureInfo.InvariantCulture));
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");
        var entry = history.Groups.SelectMany(g => g.Entries).Single();
        Assert.True(entry.ContinuedElsewhere);

        await history.OpenCommand.ExecuteAsync(entry);

        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 1, "the resume");
        Assert.StartsWith(h.Services.Paths.LocalSessionsDirectory, h.Factory.Launches[0].Resume!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restored_tab_whose_session_carried_on_elsewhere_becomes_read_only_instead_of_starting()
    {
        await using var h = new TabTestHarness();
        h.WriteTranscript("pinned-1", UserLine("pinned-1", "Add the export button", h.WorkFolder));
        await SaveToLibraryAsync(h, "pinned-1", "LAPTOP-02");
        var lease = Path.Combine(h.Services.Library.Library.GetSessionFolder("pinned-1"), LeaseManager.FileName);
        await File.WriteAllTextAsync(lease, new JsonObject { ["machine"] = "LAPTOP-02", ["owner"] = "other", ["updatedAt"] = h.Time.GetUtcNow().ToString("O") }.ToJsonString(), TestContext.Current.CancellationToken);
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "pinned-1" }];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        await tab.EnsureStartedAsync();

        Assert.Equal("LAPTOP-02", tab.TakenOverBy);
        Assert.Empty(h.Factory.Launches);
    }

    [Fact]
    public async Task A_library_session_opens_with_its_tab_overrides()
    {
        await using var h = new TabTestHarness();
        await SaveToLibraryAsync(h, "lib-4", "DESKTOP-01", overrides: new TabOverrides { Model = "claude-sonnet-5-5", PermissionMode = "plan", ShowProcessMonitor = true });
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");

        await history.OpenCommand.ExecuteAsync(history.Groups.SelectMany(g => g.Entries).Single());

        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 1, "the resume");
        Assert.Equal("claude-sonnet-5-5", h.Factory.Launches[0].Model);
        Assert.Equal("plan", h.Factory.Launches[0].PermissionMode);
        Assert.True(h.Shell.SelectedTab!.State.Overrides.ShowProcessMonitor);
    }

    [Fact]
    public async Task A_turn_saves_the_tab_overrides_to_the_library()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.State.Overrides.Effort = "high";
        h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder));
        h.Transport.EmitTurn();

        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return h.Services.Library.Library.List().Any(e => e.Record.SessionId == "s1");
        }, "the library copy");
        var record = h.Services.Library.Library.List().Single(e => e.Record.SessionId == "s1").Record;
        Assert.Equal("high", record.Overrides?.Effort);
    }

    private static async Task SaveToLibraryAsync(TabTestHarness h, string sessionId, string machine, DateTimeOffset? lastUsed = null, TabOverrides? overrides = null)
    {
        var source = Path.Combine(h.Root, $"{sessionId}.jsonl");
        await File.WriteAllLinesAsync(source, [UserLine(sessionId, "Add the export button", h.WorkFolder)]);
        var record = new SessionRecord
        {
            SessionId = sessionId,
            Name = "Export button",
            Machine = machine,
            LastUsed = lastUsed ?? h.Time.GetUtcNow().AddHours(-1),
            Folder = h.WorkFolder,
            Tokens = new TokenTotals(),
            Overrides = overrides,
        };
        await h.Services.Library.Library.SaveAsync(record, source, subagentsDirectory: null);
    }

    private static string UserLine(string sessionId, string text, string cwd) => new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        ["cwd"] = cwd,
        ["sessionId"] = sessionId,
        ["timestamp"] = "2026-09-28T10:00:00Z",
    }.ToJsonString();
}
