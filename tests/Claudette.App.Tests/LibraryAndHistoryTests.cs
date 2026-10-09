using System.Globalization;
using System.Text.Json.Nodes;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Development;
using Claudette.Core.Diffs;
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
    public async Task Searching_Claudes_replies_adds_sessions_only_a_reply_matches()
    {
        await using var h = new TabTestHarness();
        h.WriteTranscript("s1", UserLine("s1", "Why does the build fail?", h.WorkFolder), ReplyLine("s1", "Set VULKAN_SDK before you build."));
        h.WriteTranscript("s2", UserLine("s2", "Add Vulkan support", h.WorkFolder), ReplyLine("s2", "Done."));
        h.WriteTranscript("s3", UserLine("s3", "Rename the parser", h.WorkFolder), ReplyLine("s3", "Renamed."));
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");

        await SearchAsync(h, history, "vulkan");
        Assert.Equal(["s2"], history.Groups.SelectMany(g => g.Entries).Select(e => e.SessionId));
        // The view's one list: the folder's heading, then its sessions.
        Assert.Equal(["work", "s2"], history.Rows.Select(r => r is HistoryHeading heading ? heading.Label : ((HistoryEntry)r).SessionId));

        await history.SearchRepliesCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => history.Groups.SelectMany(g => g.Entries).Count() == 2, "the reply's session");

        var fromReply = history.Groups.SelectMany(g => g.Entries).Single(e => e.SessionId == "s1");
        Assert.Equal("Set VULKAN_SDK before you build.", fromReply.MatchedReply);
        Assert.Null(history.Groups.SelectMany(g => g.Entries).Single(e => e.SessionId == "s2").MatchedReply);
        Assert.Equal("Found 1 session more in Claude's replies.", history.ReplySearchText);

        // A new search starts again from the prompts.
        history.Search = "vulkan rename";
        Assert.Null(history.ReplySearchText);
        Assert.Null(fromReply.MatchedReply);
        await SearchAsync(h, history, "vulkan rename");
        Assert.Empty(history.Groups);
        Assert.True(history.IsEmpty);
    }

    [Fact]
    public async Task History_shows_sessions_as_it_reads_them_the_first_time_it_opens()
    {
        var dispatcher = new HistoryHoldingDispatcher();
        await using var h = new TabTestHarness(dispatcher: dispatcher);
        try
        {
            WriteSession(h, "old", "2026-09-28T08:00:00Z");
            WriteSession(h, "newest", "2026-09-28T10:00:00Z");
            WriteSession(h, "middle", "2026-09-28T09:00:00Z");
            h.Shell.OpenHistoryCommand.Execute(null);
            var history = h.Shell.History!;

            // The most recently written is read first, and shows while the rest are still to read.
            await TabTestHarness.Eventually(() => history.Rows.Count > 0, "the first session");
            Assert.Equal(["work", "newest"], InlineDispatcher.Read(() => RowNames(history)));
            Assert.True(history.IsLoading);
            Assert.False(history.IsFirstLoad);

            dispatcher.Release();
            await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");
            Assert.Equal(["work", "newest", "middle", "old"], RowNames(history));
        }
        finally
        {
            dispatcher.Release();
        }
    }

    [Fact]
    public async Task A_refresh_replaces_History_once_the_new_list_is_whole()
    {
        var dispatcher = new HistoryHoldingDispatcher();
        dispatcher.Release();
        await using var h = new TabTestHarness(dispatcher: dispatcher);
        WriteSession(h, "s1", "2026-09-28T08:00:00Z");
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");
        var shown = dispatcher.HistoryPosts;
        Assert.True(shown > 0);

        WriteSession(h, "s2", "2026-09-28T09:00:00Z");
        await history.RefreshCommand.ExecuteAsync(null);

        // Nothing shown part way: the list stayed as it was until the new one replaced it.
        Assert.Equal(shown, dispatcher.HistoryPosts);
        Assert.Equal(["work", "s2", "s1"], RowNames(history));
    }

    /// <summary>A session written when it was last active, as Claude Code writes them.</summary>
    private static void WriteSession(TabTestHarness h, string sessionId, string time)
    {
        var path = h.WriteTranscript(sessionId, UserLine(sessionId, $"Prompt for {sessionId}", h.WorkFolder, time));
        File.SetLastWriteTimeUtc(path, DateTimeOffset.Parse(time, CultureInfo.InvariantCulture).UtcDateTime);
    }

    /// <summary>History's list as the view shows it: each folder's heading, then its sessions.</summary>
    private static string[] RowNames(HistoryViewModel history) =>
        [.. history.Rows.Select(r => r is HistoryHeading heading ? heading.Label : ((HistoryEntry)r).SessionId)];

    /// <summary>
    /// Runs work at once, as <see cref="InlineDispatcher"/> does, and counts the shows History posts as it reads
    /// sessions. Until <see cref="Release"/>, each holds the scan that posted it, so a test sees History part read.
    /// </summary>
    private sealed class HistoryHoldingDispatcher : IUiDispatcher
    {
        private readonly InlineDispatcher _inner = new();
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _historyPosts;

        public int HistoryPosts => Volatile.Read(ref _historyPosts);

        public void Post(Action action)
        {
            _inner.Post(action);
            // Its lambdas' closures are nested in the view model.
            if (action.Method.DeclaringType is { } type && (type == typeof(HistoryViewModel) || type.DeclaringType == typeof(HistoryViewModel)))
            {
                Interlocked.Increment(ref _historyPosts);
                _released.Task.Wait();
            }
        }

        public void Release() => _released.TrySetResult();
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
    public async Task A_turn_that_ends_after_a_take_over_doesnt_copy_over_it()
    {
        // Leases are refreshed once a minute; the copy after a turn must not wait for that, or it would overwrite what
        // the other machine wrote and take the session back.
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
        var library = h.Services.Library.Library.GetTranscriptPath("s1")!;
        var copied = await File.ReadAllTextAsync(library, TestContext.Current.CancellationToken);
        var lease = Path.Combine(h.Services.Library.Library.GetSessionFolder("s1"), LeaseManager.FileName);
        var theirs = new JsonObject { ["machine"] = "LAPTOP-02", ["owner"] = "other", ["updatedAt"] = h.Time.GetUtcNow().ToString("O") }.ToJsonString();
        await File.WriteAllTextAsync(lease, theirs, TestContext.Current.CancellationToken);

        h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder), UserLine("s1", "written after the take-over", h.WorkFolder));
        h.Transport.EmitTurn();

        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return tab.IsReadOnly;
        }, "read-only");
        Assert.Equal("LAPTOP-02", tab.TakenOverBy);
        Assert.Equal(copied, await File.ReadAllTextAsync(library, TestContext.Current.CancellationToken));
        Assert.Equal(theirs, await File.ReadAllTextAsync(lease, TestContext.Current.CancellationToken));
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

    [Fact]
    public async Task A_library_session_opens_with_its_reviewed_files_in_this_machines_folder()
    {
        await using var h = new TabTestHarness();
        await SaveToLibraryAsync(h, "lib-5", "DESKTOP-01", reviewedFiles: [new ReviewedFile { Path = "src/auth.cs", Change = "e1" }]);
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");

        await history.OpenCommand.ExecuteAsync(history.Groups.SelectMany(g => g.Entries).Single());

        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 1, "the resume");
        Assert.Equal([(Path.Combine(h.WorkFolder, "src", "auth.cs"), "e1")], h.Shell.SelectedTab!.State.ReviewedFiles.Select(m => (m.Path, m.Change)));
    }

    [Fact]
    public async Task Ticking_a_file_in_a_tab_that_syncs_updates_its_record_without_waiting_for_a_turn()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder));
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.State.Tokens.Total == 120, "the turn to finish");
        tab.SetSyncToLibrary(true);
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return h.Services.Library.CheckLease("s1") is LeaseStatus.Mine;
        }, "the first copy");
        Assert.Empty(Record().ReviewedFiles ?? []);
        var path = Path.Combine(h.WorkFolder, "src", "auth.cs");

        tab.ChangedFiles.ToggleFileReviewedCommand.Execute(new ChangedFileRow { Path = path, DisplayPath = "src/auth.cs", Status = "M", StatusText = "Modified", FromGit = true });

        // Relative to the folder, which has another path on another machine.
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return Record().ReviewedFiles is [{ Path: "src/auth.cs", Change: null }];
        }, "the record");

        SessionRecord Record() => h.Services.Library.Library.List().Single(e => e.Record.SessionId == "s1").Record;
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
        // Released in the background, off the UI thread.
        await TabTestHarness.Eventually(() => h.Services.Library.CheckLease("s1") is LeaseStatus.Free, "the release");
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

    // ---- Sync now (DESIGN.md §9, "Which tabs sync") -----------------------------------------------------------------

    [Fact]
    public async Task Sync_now_copies_the_session_straight_away_every_file_again_and_says_so()
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
        // Renamed since the turn, and the library's transcript changed without its size or time changing, so an
        // ordinary copy would skip it.
        tab.State.UserName = "Login fix";
        var copy = h.Services.Library.Library.GetTranscriptPath("s1")!;
        var source = await File.ReadAllTextAsync(copy, TestContext.Current.CancellationToken);
        var copyTime = File.GetLastWriteTimeUtc(copy);
        await File.WriteAllTextAsync(copy, new string('x', source.Length), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(copy, copyTime);
        Assert.True(tab.SyncNowCommand.CanExecute(null));

        await SyncNowAsync(h, tab);

        Assert.Equal(source, await File.ReadAllTextAsync(copy, TestContext.Current.CancellationToken));
        Assert.Equal("Login fix", Assert.Single(h.Services.Library.Library.List()).Record.Name);
        Assert.IsType<LeaseStatus.Mine>(h.Services.Library.CheckLease("s1"));
        Assert.Contains(Notes(tab), n => n is { Text: "Copied this session to the session library.", Kind: Conversation.NoteKind.Info });
    }

    [Fact]
    public async Task Sync_now_is_only_there_for_a_tab_that_syncs_with_a_session_Claude_is_not_writing()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        Assert.False(tab.SyncNowCommand.CanExecute(null));

        tab.SetSyncToLibrary(true);

        // Nothing to copy until the first message.
        Assert.False(tab.SyncNowCommand.CanExecute(null));
        Assert.Contains("nothing to copy", tab.SyncNowTip, StringComparison.Ordinal);

        h.WriteTranscript("s1", UserLine("s1", "long job", h.WorkFolder));
        tab.ComposerText = "long job";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5"}""");
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Working, "working");

        // Never while Claude Code is writing the transcript: the turn's end copies it anyway.
        Assert.False(tab.SyncNowCommand.CanExecute(null));
        Assert.Contains("when the turn ends", tab.SyncNowTip, StringComparison.Ordinal);

        h.Transport.EmitTurn();

        await TabTestHarness.Eventually(() => tab.SyncNowCommand.CanExecute(null), "Sync now after the turn");
        Assert.Contains("without waiting", tab.SyncNowTip, StringComparison.Ordinal);

        await tab.OnTakenOverAsync("LAPTOP-02");

        Assert.False(tab.SyncNowCommand.CanExecute(null));
        Assert.Contains("LAPTOP-02", tab.SyncNowTip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_now_writes_nothing_to_a_session_another_machine_took_over_and_the_tab_becomes_read_only()
    {
        await using var h = new TabTestHarness(s => s.Sessions.SyncNewTabs = true);
        var tab = await h.OpenTabAsync();
        var transcript = h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder));
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return h.Services.Library.CheckLease("s1") is LeaseStatus.Mine;
        }, "the library copy");
        // Taken over since the last lease refresh, which hasn't noticed yet.
        await HoldLeaseAsync(h, "s1", "LAPTOP-02");
        await File.AppendAllTextAsync(transcript, UserLine("s1", "written here", h.WorkFolder) + "\n", TestContext.Current.CancellationToken);

        await SyncNowAsync(h, tab);

        Assert.Equal("LAPTOP-02", tab.TakenOverBy);
        await SettleAsync(h);
        Assert.DoesNotContain("written here", await File.ReadAllTextAsync(h.Services.Library.Library.GetTranscriptPath("s1")!, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.IsType<LeaseStatus.HeldByOther>(h.Services.Library.CheckLease("s1"));
        Assert.DoesNotContain(Notes(tab), n => n.Text.StartsWith("Copied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sync_now_says_why_when_the_library_folder_is_not_available()
    {
        await using var h = new TabTestHarness(s => s.Sessions.SyncNewTabs = true);
        var tab = await h.OpenTabAsync();
        h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder));
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.State.Tokens.Total == 120, "the turn to finish");
        await SettleAsync(h);
        // A file where the library's folder should be, as when a sync client's drive isn't mounted.
        var blocked = Path.Combine(h.Root, "not-a-folder");
        await File.WriteAllTextAsync(blocked, "", TestContext.Current.CancellationToken);
        h.Services.Settings.Sessions.LibraryFolder = blocked;
        h.Services.Library.OnSettingsChanged();

        await SyncNowAsync(h, tab);

        var note = Assert.Single(Notes(tab), n => n.Text.StartsWith("Couldn't copy this session to the session library: ", StringComparison.Ordinal));
        Assert.Equal(Conversation.NoteKind.Warning, note.Kind);
        Assert.True(tab.SyncToLibrary);
    }

    [Fact]
    public async Task A_copy_after_a_turn_that_fails_says_so_once_and_again_when_it_works()
    {
        await using var h = new TabTestHarness(s => s.Sessions.SyncNewTabs = true);
        var tab = await h.OpenTabAsync();
        h.WriteTranscript("s1", UserLine("s1", "hello", h.WorkFolder));
        var library = h.Services.Settings.Sessions.LibraryFolder;
        var blocked = Path.Combine(h.Root, "not-a-folder");
        await File.WriteAllTextAsync(blocked, "", TestContext.Current.CancellationToken);
        h.Services.Settings.Sessions.LibraryFolder = blocked;
        h.Services.Library.OnSettingsChanged();
        static bool Failed(Conversation.NoteItem n) => n.Text.StartsWith("Couldn't copy this session to the session library: ", StringComparison.Ordinal);

        for (var turn = 1; turn <= 2; turn++)
        {
            h.Transport.EmitTurn();
            await TabTestHarness.Eventually(() => tab.State.Tokens.Total == 120 * turn && tab.IsSettled, "the turn to finish");
            await CopyAfterTurnAsync(h, tab);
        }
        var note = Assert.Single(Notes(tab), Failed);
        Assert.Equal(Conversation.NoteKind.Warning, note.Kind);
        Assert.EndsWith("It's tried again after the next turn, or use Sync now.", note.Text, StringComparison.Ordinal);

        h.Services.Settings.Sessions.LibraryFolder = library;
        h.Services.Library.OnSettingsChanged();
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.State.Tokens.Total == 360 && tab.IsSettled, "the turn to finish");
        await CopyAfterTurnAsync(h, tab);
        Assert.Equal("Copied this session to the session library again.", Notes(tab)[^1].Text);
    }

    /// <summary>Types a search into History and waits for typing's pause and the filtering after it.</summary>
    private static async Task SearchAsync(TabTestHarness h, HistoryViewModel history, string text)
    {
        history.Search = text;
        Assert.True(history.IsFilterPending);
        h.Time.Advance(HistoryViewModel.SearchDelay);
        await TabTestHarness.Eventually(() => !history.IsFilterPending, "the search");
    }

    /// <summary>Waits out the copy that follows a turn, past its settle delay.</summary>
    private static async Task CopyAfterTurnAsync(TabTestHarness h, TabViewModel tab)
    {
        var copy = tab.LibraryCopy;
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return copy.IsCompleted;
        }, "the copy after the turn");
        await copy;
    }

    [Fact]
    public async Task Sync_now_says_so_when_this_machine_no_longer_has_the_transcript()
    {
        await using var h = new TabTestHarness(s => s.Sessions.SyncNewTabs = true);
        var tab = await h.OpenTabAsync();
        // The turn's own copy finds no transcript and gives up quietly.
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.State.Tokens.Total == 120, "the turn to finish");
        await SettleAsync(h);

        await SyncNowAsync(h, tab);

        var note = Assert.Single(Notes(tab), n => n.Text.StartsWith("Couldn't copy", StringComparison.Ordinal));
        Assert.Equal("Couldn't copy this session to the session library: its transcript isn't on this machine.", note.Text);
        Assert.Equal(Conversation.NoteKind.Warning, note.Kind);
        Assert.Empty(h.Services.Library.Library.List());
    }

    /// <summary>Runs <b>Sync now</b> to the end, moving the clock past the settle delay it waits.</summary>
    private static async Task SyncNowAsync(TabTestHarness h, TabViewModel tab)
    {
        var sync = tab.SyncNowCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return sync.IsCompleted;
        }, "Sync now");
        await sync;
    }

    private static Conversation.NoteItem[] Notes(TabViewModel tab) => InlineDispatcher.Read(() => tab.Items.OfType<Conversation.NoteItem>().ToArray());

    /// <summary>
    /// Lets a copy that would follow a turn run its course: past the settle delay, until the library has no copy under
    /// way and every tab has heard how its last one went.
    /// </summary>
    private static Task SettleAsync(TabTestHarness h)
    {
        h.Time.Advance(Claudette.App.Services.LibraryService.SettleDelay);
        return Waiting.UntilAsync(
            () => h.Services.Library.IsIdle && InlineDispatcher.Read(() => h.Shell.AllTabs.All(t => t.LibraryCopy.IsCompleted)),
            "the library copies to finish");
    }

    /// <summary>Another machine has the session open, with a fresh lease.</summary>
    private static Task HoldLeaseAsync(TabTestHarness h, string sessionId, string machine) =>
        File.WriteAllTextAsync(
            Path.Combine(h.Services.Library.Library.GetSessionFolder(sessionId), LeaseManager.FileName),
            new JsonObject { ["machine"] = machine, ["owner"] = "other", ["updatedAt"] = h.Time.GetUtcNow().ToString("O") }.ToJsonString(),
            TestContext.Current.CancellationToken);

    private static async Task SaveToLibraryAsync(TabTestHarness h, string sessionId, string machine, DateTimeOffset? lastUsed = null, TabOverrides? overrides = null,
        List<ReviewedFile>? reviewedFiles = null)
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
            ReviewedFiles = reviewedFiles,
        };
        await h.Services.Library.Library.SaveAsync(record, source, subagentsDirectory: null);
    }

    private static string ReplyLine(string sessionId, string text) => new JsonObject
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) },
        ["sessionId"] = sessionId,
        ["timestamp"] = "2026-09-28T10:01:00Z",
    }.ToJsonString();

    private static string UserLine(string sessionId, string text, string cwd, string time = "2026-09-28T10:00:00Z") => new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        ["cwd"] = cwd,
        ["sessionId"] = sessionId,
        ["timestamp"] = time,
    }.ToJsonString();
}
