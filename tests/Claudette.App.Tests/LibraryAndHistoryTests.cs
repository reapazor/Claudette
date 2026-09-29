using System.Globalization;
using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Development;
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
        tab.SetSyncToLibrary(true);
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
        // It only ever lived on this machine, so it stays here.
        Assert.False(tab.SyncToLibrary);
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
        // Someone synced it, so it keeps syncing here.
        Assert.True(h.Shell.SelectedTab!.SyncToLibrary);
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
        // A copy of a library session syncs too, under its own id once it has one.
        Assert.True(h.Shell.SelectedTab!.SyncToLibrary);
    }

    [Fact]
    public async Task A_tab_taken_over_elsewhere_becomes_read_only()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.SetSyncToLibrary(true);
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
        Assert.True(h.Shell.SelectedTab!.SyncToLibrary);
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
        Assert.True(h.Shell.SelectedTab!.SyncToLibrary);
    }

    [Fact]
    public async Task A_restored_tab_whose_session_carried_on_elsewhere_becomes_read_only_instead_of_starting()
    {
        await using var h = new TabTestHarness();
        h.WriteTranscript("pinned-1", UserLine("pinned-1", "Add the export button", h.WorkFolder));
        await SaveToLibraryAsync(h, "pinned-1", "LAPTOP-02");
        var lease = Path.Combine(h.Services.Library.Library.GetSessionFolder("pinned-1"), LeaseManager.FileName);
        await File.WriteAllTextAsync(lease, new JsonObject { ["machine"] = "LAPTOP-02", ["owner"] = "other", ["updatedAt"] = h.Time.GetUtcNow().ToString("O") }.ToJsonString(), TestContext.Current.CancellationToken);
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "pinned-1", SyncToLibrary = true }];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        await tab.EnsureStartedAsync();

        Assert.Equal("LAPTOP-02", tab.TakenOverBy);
        Assert.Empty(h.Factory.Launches);
    }

    [Fact]
    public async Task A_restored_tab_that_does_not_sync_ignores_leases()
    {
        await using var h = new TabTestHarness();
        h.WriteTranscript("pinned-2", UserLine("pinned-2", "Add the export button", h.WorkFolder));
        await SaveToLibraryAsync(h, "pinned-2", "LAPTOP-02");
        await HoldLeaseAsync(h, "pinned-2", "LAPTOP-02");
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "pinned-2" }];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        await tab.EnsureStartedAsync();

        // It carries on with this machine's own transcript, and leaves the other machine's lease alone.
        Assert.Null(tab.TakenOverBy);
        Assert.Equal("pinned-2", Assert.Single(h.Factory.Launches).Resume);
        Assert.IsType<LeaseStatus.HeldByOther>(h.Services.Library.CheckLease("pinned-2"));
        Assert.Empty(h.Services.Library.Leases.HeldSessions);
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
        tab.SetSyncToLibrary(true);
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

    // ---- Syncing is per tab, and opt-in (DESIGN.md §9, "Session library") -------------------------------------------

    [Fact]
    public async Task A_new_tab_writes_nothing_to_the_library_and_takes_no_lease()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        Assert.False(tab.SyncToLibrary);
        h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder));

        h.Transport.EmitTurn();

        await TabTestHarness.Eventually(() => tab.State.Tokens.Total == 120, "the turn to finish");
        await SettleAsync(h);
        Assert.Empty(h.Services.Library.Library.List());
        Assert.Null(h.Services.Library.Library.GetTranscriptPath("s1"));
        Assert.IsType<LeaseStatus.Free>(h.Services.Library.CheckLease("s1"));
        Assert.Empty(h.Services.Library.Leases.HeldSessions);
    }

    [Fact]
    public async Task Turning_sync_on_copies_the_session_straight_away_and_takes_its_lease()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder));
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.State.Tokens.Total == 120, "the turn to finish");

        tab.ToggleSyncToLibraryCommand.Execute(null);

        Assert.True(tab.SyncToLibrary);
        Assert.True(Assert.Single(h.Services.State.Tabs).SyncToLibrary);
        // No turn needed: the copy follows as soon as the transcript has settled.
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return h.Services.Library.CheckLease("s1") is LeaseStatus.Mine;
        }, "the library copy");
        Assert.NotNull(h.Services.Library.Library.GetTranscriptPath("s1"));
        Assert.Equal(120, Assert.Single(h.Services.Library.Library.List()).Record.Tokens.Total);
    }

    [Fact]
    public async Task Turning_sync_on_during_a_turn_copies_the_session_when_the_turn_ends()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.WriteTranscript("s1", UserLine("s1", "long job", h.WorkFolder));
        tab.ComposerText = "long job";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5"}""");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "working");

        tab.SetSyncToLibrary(true);

        // Never while Claude Code is writing the transcript.
        await SettleAsync(h);
        Assert.Null(h.Services.Library.Library.GetTranscriptPath("s1"));
        Assert.Empty(h.Services.Library.Leases.HeldSessions);

        h.Transport.EmitTurn();

        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return h.Services.Library.CheckLease("s1") is LeaseStatus.Mine;
        }, "the library copy");
        Assert.NotNull(h.Services.Library.Library.GetTranscriptPath("s1"));
    }

    [Fact]
    public async Task Turning_sync_off_releases_the_lease_and_leaves_the_copy()
    {
        await using var h = new TabTestHarness(s => s.Sessions.SyncNewTabs = true);
        var tab = await h.OpenTabAsync();
        h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder));
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return h.Services.Library.CheckLease("s1") is LeaseStatus.Mine;
        }, "the library copy");

        tab.ToggleSyncToLibraryCommand.Execute(null);

        Assert.False(tab.SyncToLibrary);
        Assert.IsType<LeaseStatus.Free>(h.Services.Library.CheckLease("s1"));
        Assert.Empty(h.Services.Library.Leases.HeldSessions);
        // Later turns stay on this machine; other machines still see the copy as it was.
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.State.Tokens.Total == 240, "the second turn");
        await SettleAsync(h);
        Assert.NotNull(h.Services.Library.Library.GetTranscriptPath("s1"));
        Assert.Equal(120, Assert.Single(h.Services.Library.Library.List()).Record.Tokens.Total);
        Assert.IsType<LeaseStatus.Free>(h.Services.Library.CheckLease("s1"));
    }

    [Fact]
    public async Task New_tabs_sync_when_Settings_says_so()
    {
        await using var h = new TabTestHarness(s => s.Sessions.SyncNewTabs = true);

        var tab = await h.OpenTabAsync();

        Assert.True(tab.SyncToLibrary);
        h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder));
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return h.Services.Library.CheckLease("s1") is LeaseStatus.Mine;
        }, "the library copy");
    }

    [Fact]
    public async Task A_session_on_this_machine_with_a_library_copy_opens_syncing()
    {
        await using var h = new TabTestHarness();
        h.WriteTranscript("both-3", UserLine("both-3", "Add the export button", h.WorkFolder));
        await SaveToLibraryAsync(h, "both-3", h.Services.Library.MachineName, lastUsed: DateTimeOffset.Parse("2026-09-28T09:00:00Z", CultureInfo.InvariantCulture));
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");
        var entry = history.Groups.SelectMany(g => g.Entries).Single();
        Assert.True(entry.IsLocal);
        Assert.False(entry.ContinuedElsewhere);

        await history.OpenCommand.ExecuteAsync(entry);

        // Resumed from this machine's own transcript, and syncing, so it takes the lease as it starts.
        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 1, "the resume");
        Assert.Equal("both-3", h.Factory.Launches[0].Resume);
        Assert.True(h.Shell.SelectedTab!.SyncToLibrary);
        Assert.IsType<LeaseStatus.Mine>(h.Services.Library.CheckLease("both-3"));
    }

    [Fact]
    public async Task A_tab_cannot_start_syncing_a_session_another_machine_has_open()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder));
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.State.Tokens.Total == 120, "the turn to finish");
        await SaveToLibraryAsync(h, "s1", "LAPTOP-02");
        await HoldLeaseAsync(h, "s1", "LAPTOP-02");

        tab.ToggleSyncToLibraryCommand.Execute(null);

        Assert.False(tab.SyncToLibrary);
        Assert.Contains(InlineDispatcher.Read(() => tab.Items.OfType<Conversation.NoteItem>().ToArray()), n => n.Text.Contains("LAPTOP-02", StringComparison.Ordinal));
        await SettleAsync(h);
        Assert.Equal("LAPTOP-02", Assert.Single(h.Services.Library.Library.List()).Record.Machine);
        Assert.IsType<LeaseStatus.HeldByOther>(h.Services.Library.CheckLease("s1"));
    }

    [Fact]
    public async Task Tab_settings_turn_sync_on_and_Use_defaults_leaves_it()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var settings = new TabSettingsViewModel(h.Services, tab, () => { });
        Assert.False(settings.SyncToLibrary);

        settings.SyncToLibrary = true;
        settings.UseDefaultsCommand.Execute(null);
        await settings.ApplyCommand.ExecuteAsync(null);

        // It's the tab's own state, not an override.
        Assert.True(tab.SyncToLibrary);
        Assert.False(tab.HasOverrides);
        Assert.True(new TabSettingsViewModel(h.Services, tab, () => { }).SyncToLibrary);
    }

    [Fact]
    public async Task The_sync_switch_survives_saving_restoring_and_a_restart_into_a_new_build()
    {
        var saved = new List<TabState>();
        RestartSnapshot? snapshot;
        await using (var h = new TabTestHarness())
        {
            h.Services.Settings.Sessions.RestoreUnpinnedTabs = true;
            h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, UserName = "synced" }, new TabState { Folder = h.WorkFolder, UserName = "local" }];
            h.Shell.Restore(null);
            h.Shell.AllTabs.First().SetSyncToLibrary(true);
            await h.Services.FlushAsync();
            saved.AddRange(new JsonFileStore<AppState>(h.Services.Paths.StateFile).Load().Tabs);

            var captured = h.Shell.CaptureForRestart();
            captured.Nonce = "n";
            captured.CreatedAt = h.Time.GetUtcNow();
            captured.Save(h.Services.Paths.RestartFile);
            snapshot = RestartSnapshot.Load(h.Services.Paths.RestartFile, "n", h.Time.GetUtcNow());
        }
        Assert.Equal([true, false], saved.Select(t => t.SyncToLibrary));
        Assert.NotNull(snapshot);
        Assert.Equal([true, false], snapshot.Tabs.Select(t => t.SyncToLibrary));

        await using var launched = new TabTestHarness(s => s.Sessions.RestoreUnpinnedTabs = true);
        launched.Services.State.Tabs = saved;
        launched.Shell.Restore(null);
        Assert.Equal([true, false], launched.Shell.AllTabs.Select(t => t.SyncToLibrary));

        await using var restarted = new TabTestHarness();
        restarted.Shell.Restore(null, snapshot);
        Assert.Equal([true, false], restarted.Shell.AllTabs.Select(t => t.SyncToLibrary));
    }

    /// <summary>Gives a copy that would follow a turn every chance to: past the settle delay, with time for background work.</summary>
    private static async Task SettleAsync(TabTestHarness h)
    {
        for (var i = 0; i < 5; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Another machine has the session open, with a fresh lease.</summary>
    private static Task HoldLeaseAsync(TabTestHarness h, string sessionId, string machine) =>
        File.WriteAllTextAsync(
            Path.Combine(h.Services.Library.Library.GetSessionFolder(sessionId), LeaseManager.FileName),
            new JsonObject { ["machine"] = machine, ["owner"] = "other", ["updatedAt"] = h.Time.GetUtcNow().ToString("O") }.ToJsonString(),
            TestContext.Current.CancellationToken);

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
