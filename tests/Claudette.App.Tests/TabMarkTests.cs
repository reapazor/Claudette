using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Library;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>A tab's mark: an icon of the user's own, kept with the tab and its session (DESIGN.md §4, "Marks").</summary>
public class TabMarkTests
{
    [Fact]
    public async Task A_mark_from_the_menu_is_put_on_the_tab_and_picking_it_again_takes_it_off()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        Assert.Null(tab.Mark);
        Assert.False(tab.HasMark);
        Assert.False(tab.ClearMarkCommand.CanExecute(null));
        Assert.Equal(TabMarks.All, tab.MarkMenu.Select(i => i.Mark));
        Assert.All(tab.MarkMenu, i => Assert.False(i.IsOn));

        Pick(tab, TabMark.Question);

        Assert.Equal(TabMark.Question, tab.Mark);
        Assert.True(tab.HasMark);
        Assert.Equal("Marked with a question mark", tab.MarkTip);
        Assert.Equal([TabMark.Question], tab.MarkMenu.Where(i => i.IsOn).Select(i => i.Mark));
        Assert.Equal("Question mark", tab.MarkMenu.Single(i => i.IsOn).Name);
        Assert.True(tab.ClearMarkCommand.CanExecute(null));

        // Another mark replaces it; the tab's own mark again takes it off.
        Pick(tab, TabMark.Star);
        Assert.Equal(TabMark.Star, tab.Mark);
        Pick(tab, TabMark.Star);
        Assert.Null(tab.Mark);
        Assert.Null(tab.MarkTip);

        Pick(tab, TabMark.Flag);
        tab.ClearMarkCommand.Execute(null);
        Assert.Null(tab.Mark);
        Assert.False(tab.ClearMarkCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_mark_is_saved_with_the_tab_and_comes_back_when_it_is_restored()
    {
        await using (var h = new TabTestHarness())
        {
            var tab = await h.OpenTabAsync();
            Pick(tab, TabMark.Cross);
            await h.Services.FlushAsync();

            Assert.Equal("cross", Assert.Single(new JsonFileStore<AppState>(h.Services.Paths.StateFile).Load().Tabs).Mark);
        }

        await using var restored = new TabTestHarness(s => s.Sessions.RestoreUnpinnedTabs = true);
        restored.Services.State.Tabs = [new TabState { Folder = restored.WorkFolder, Mark = "cross" }, new TabState { Folder = restored.WorkFolder, Mark = "heart" }];
        restored.Shell.Restore(null);

        // A mark a later Claudette added shows as none, but stays as it was written.
        Assert.Equal([TabMark.Cross, null], restored.Shell.AllTabs.Select(t => t.Mark));
        Assert.Equal("heart", restored.Shell.AllTabs.Last().State.Mark);
    }

    [Fact]
    public async Task The_sessions_mark_is_kept_on_this_machine_once_the_session_has_an_id_and_a_clear_is_kept_too()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var marks = h.Services.State.SessionMarks;

        // Before the first message the tab has no session to keep it for.
        Pick(tab, TabMark.Check);
        Assert.Empty(marks);

        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => marks.ContainsKey("s1"), "the session's mark");
        Assert.Equal("check", marks["s1"].Mark);
        Assert.Equal(h.Time.GetUtcNow(), marks["s1"].At);

        h.Time.Advance(TimeSpan.FromMinutes(1));
        tab.ClearMarkCommand.Execute(null);

        // Cleared rather than gone, so an older library record's mark doesn't come back.
        Assert.Null(marks["s1"].Mark);
        Assert.Equal(h.Time.GetUtcNow(), marks["s1"].At);
    }

    [Fact]
    public async Task A_copy_of_a_tab_starts_without_its_mark_and_marking_the_copy_leaves_the_original_session_alone()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.EmitTurn();
        await TabTestHarness.Eventually(() => tab.State.SessionId == "s1", "the session");
        Pick(tab, TabMark.Check);

        await h.Shell.DuplicateTabCommand.ExecuteAsync(tab);

        var copy = h.Shell.AllTabs.Single(t => t != tab);
        Assert.Null(copy.Mark);
        // Until its first turn the copy has the original's id, but it isn't the original's session.
        Pick(copy, TabMark.Cross);
        Assert.Equal("check", h.Services.State.SessionMarks["s1"].Mark);
        Assert.Equal(TabMark.Check, tab.Mark);
    }

    [Fact]
    public async Task History_shows_a_closed_sessions_mark_and_opening_it_brings_the_mark_back()
    {
        await using var h = new TabTestHarness();
        h.WriteTranscript("old-1", UserLine("old-1", "Refactor the parser", h.WorkFolder));
        h.Services.State.SessionMarks["old-1"] = new SessionMark { Mark = "pause", At = h.Time.GetUtcNow() };
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");

        var entry = history.Groups.SelectMany(g => g.Entries).Single();
        Assert.Equal(TabMark.Pause, entry.Mark);
        Assert.Equal("Marked with a pause sign", entry.MarkTip);

        await history.OpenCommand.ExecuteAsync(entry);

        Assert.Equal(TabMark.Pause, h.Shell.SelectedTab!.Mark);
    }

    [Fact]
    public async Task A_library_session_has_its_records_mark_unless_it_was_marked_here_since()
    {
        await using var h = new TabTestHarness();
        await SaveToLibraryAsync(h, "lib-1", "DESKTOP-01", mark: "flag", lastUsed: h.Time.GetUtcNow().AddHours(-1));
        // Marked here before the other machine wrote the record: the record has the latest.
        h.Services.State.SessionMarks["lib-1"] = new SessionMark { Mark = "cross", At = h.Time.GetUtcNow().AddHours(-2) };
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");
        Assert.Equal(TabMark.Flag, history.Groups.SelectMany(g => g.Entries).Single().Mark);

        // Marked here since, in a tab that no longer syncs, say: this machine's is the latest.
        h.Services.State.SessionMarks["lib-1"] = new SessionMark { Mark = "star", At = h.Time.GetUtcNow() };
        await history.RefreshCommand.ExecuteAsync(null);
        var entry = history.Groups.SelectMany(g => g.Entries).Single();
        Assert.Equal(TabMark.Star, entry.Mark);

        await history.OpenCommand.ExecuteAsync(entry);

        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 1, "the resume");
        Assert.Equal(TabMark.Star, h.Shell.SelectedTab!.Mark);
    }

    [Fact]
    public async Task A_copy_opened_from_History_starts_without_the_sessions_mark()
    {
        await using var h = new TabTestHarness();
        await SaveToLibraryAsync(h, "lib-2", "DESKTOP-01", mark: "flag");
        var lease = Path.Combine(h.Services.Library.Library.GetSessionFolder("lib-2"), LeaseManager.FileName);
        await File.WriteAllTextAsync(lease, new JsonObject { ["machine"] = "DESKTOP-01", ["owner"] = "other", ["updatedAt"] = h.Time.GetUtcNow().ToString("O") }.ToJsonString(), TestContext.Current.CancellationToken);
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");

        await history.OpenCommand.ExecuteAsync(history.Groups.SelectMany(g => g.Entries).Single());
        await Assert.IsType<ConfirmationViewModel>(h.Shell.Confirmation).SecondaryCommand.ExecuteAsync(null);

        await TabTestHarness.Eventually(() => h.Factory.Launches.Count == 1, "the resume");
        Assert.True(h.Factory.Launches[0].ForkSession);
        Assert.Null(h.Shell.SelectedTab!.Mark);
    }

    [Fact]
    public async Task Marking_a_tab_that_syncs_updates_its_record_without_waiting_for_a_turn()
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
        Assert.Null(Record().Mark);

        Pick(tab, TabMark.Star);

        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            return Record().Mark == "star";
        }, "the record");

        SessionRecord Record() => h.Services.Library.Library.List().Single(e => e.Record.SessionId == "s1").Record;
    }

    /// <summary>Picks a mark from the tab's menu, as clicking its item does.</summary>
    private static void Pick(TabViewModel tab, TabMark mark)
    {
        var item = tab.MarkMenu.Single(i => i.Mark == mark);
        item.Toggle.Execute(item.Mark);
    }

    private static async Task SaveToLibraryAsync(TabTestHarness h, string sessionId, string machine, string? mark, DateTimeOffset? lastUsed = null)
    {
        var source = Path.Combine(h.Root, $"{sessionId}.jsonl");
        await File.WriteAllLinesAsync(source, [UserLine(sessionId, "Add the export button", h.WorkFolder)]);
        var record = new SessionRecord
        {
            SessionId = sessionId,
            Name = "Export button",
            Mark = mark,
            Machine = machine,
            LastUsed = lastUsed ?? h.Time.GetUtcNow().AddHours(-1),
            Folder = h.WorkFolder,
            Tokens = new TokenTotals(),
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
