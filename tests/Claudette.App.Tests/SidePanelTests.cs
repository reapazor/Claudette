using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Diffs;
using Claudette.Core.Processes;
using Claudette.Core.Settings;
using Claudette.Platform.Processes;

namespace Claudette.App.Tests;

/// <summary>The side panel: changed files (DESIGN.md §8) and the process monitor (DESIGN.md §4).</summary>
public class SidePanelTests
{
    [Fact]
    public async Task Resizing_the_side_panel_resizes_every_tabs_down_to_its_least_and_is_saved()
    {
        await using var h = new TabTestHarness();
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true }, new TabState { Folder = h.WorkFolder, IsPinned = true }];
        h.Shell.Restore(null);
        var (first, second) = (h.Shell.AllTabs.First(), h.Shell.AllTabs.Last());
        // The selected tab starts meanwhile, changing properties on another thread while the test reads them.
        await TabTestHarness.Eventually(() => first.Status == TabStatus.Idle && first.IsSettled, "the first tab to start");
        Assert.Equal(ShellLayout.DefaultSidePanelWidth, second.SidePanelWidth);
        var changed = new List<string?>();
        second.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        first.ResizeSidePanel(40);
        Assert.Equal(ShellLayout.MinSidePanelWidth, second.SidePanelWidth);
        // No most of its own: each tab's view shows it as far as leaves the conversation room.
        first.ResizeSidePanel(5000);
        Assert.Equal(5000, second.SidePanelWidth);
        Assert.Contains(nameof(TabViewModel.SidePanelWidth), changed);

        // Kept when the drag ends, for every tab and the next launch.
        first.ResizeSidePanel(480);
        Assert.Null(h.Services.State.SidePanelWidth);
        first.SaveSidePanelWidth();
        Assert.Equal(480, h.Services.State.SidePanelWidth);
        Assert.Equal(480, new ShellViewModel(h.Services, () => { }).Layout.SidePanelWidth);

        // Double-clicking the edge.
        second.ResetSidePanelWidth();
        Assert.Equal(ShellLayout.DefaultSidePanelWidth, first.SidePanelWidth);
        Assert.Equal(ShellLayout.DefaultSidePanelWidth, h.Services.State.SidePanelWidth);
    }

    [Fact]
    public async Task Moving_a_page_moves_it_in_every_tabs_side_panel_and_is_saved()
    {
        await using var h = new TabTestHarness();
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true }, new TabState { Folder = h.WorkFolder, IsPinned = true }];
        h.Shell.Restore(null);
        var (first, second) = (h.Shell.AllTabs.First(), h.Shell.AllTabs.Last());
        await TabTestHarness.Eventually(() => first.Status == TabStatus.Idle && first.IsSettled, "the first tab to start");
        Assert.Equal(Enum.GetValues<SidePanelPage>(), second.SidePanelPages);
        var changed = new List<string?>();
        second.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Dragged onto Agents' place, the scratch pad takes it, and Agents moves over.
        first.MoveSidePanelPage(SidePanelPage.ScratchPad, SidePanelPage.Agents);
        SidePanelPage[] moved = [SidePanelPage.Files, SidePanelPage.ScratchPad, SidePanelPage.Agents, SidePanelPage.Project, SidePanelPage.Processes, SidePanelPage.Tasks, SidePanelPage.Mcp];
        Assert.Equal(moved, second.SidePanelPages);
        Assert.Contains(nameof(TabViewModel.SidePanelPages), changed);
        Assert.Equal(moved.Select(p => p.ToString()), h.Services.State.SidePanelPages);
        Assert.Equal(moved, new ShellViewModel(h.Services, () => { }).Layout.SidePanelPages);

        // Reset order.
        second.ResetSidePanelPages();
        Assert.Equal(Enum.GetValues<SidePanelPage>(), first.SidePanelPages);
        Assert.Null(h.Services.State.SidePanelPages);
    }

    [Fact]
    public async Task Move_left_and_right_pass_the_next_page_the_tab_has()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        // No project tools, monitor or MCP servers, and no tasks: Changed Files, Agents and Scratch Pad.
        Assert.Equal([SidePanelPage.Files, SidePanelPage.Agents, SidePanelPage.ScratchPad], tab.SidePanelPages.Where(tab.HasSidePanelPage));

        tab.MoveSidePanelPage(SidePanelPage.ScratchPad, -1);
        Assert.Equal([SidePanelPage.Files, SidePanelPage.ScratchPad, SidePanelPage.Agents], tab.SidePanelPages.Where(tab.HasSidePanelPage));

        // Already at either end, it stays.
        tab.MoveSidePanelPage(SidePanelPage.Files, -1);
        tab.MoveSidePanelPage(SidePanelPage.Agents, 1);
        Assert.Equal([SidePanelPage.Files, SidePanelPage.ScratchPad, SidePanelPage.Agents], tab.SidePanelPages.Where(tab.HasSidePanelPage));

        tab.MoveSidePanelPage(SidePanelPage.Files, 1);
        Assert.Equal([SidePanelPage.ScratchPad, SidePanelPage.Files, SidePanelPage.Agents], tab.SidePanelPages.Where(tab.HasSidePanelPage));
    }

    [Fact]
    public async Task A_saved_order_leaves_out_names_it_doesnt_know_and_puts_a_page_it_doesnt_name_after_the_one_it_follows()
    {
        await using var h = new TabTestHarness();
        // Saved before MCP was a page, and with one since taken out.
        h.Services.State.SidePanelPages = ["ScratchPad", "Tasks", "Gone", "Files", "Agents", "Project", "Processes", "Files"];

        var layout = new ShellViewModel(h.Services, () => { }).Layout;

        Assert.Equal([SidePanelPage.ScratchPad, SidePanelPage.Tasks, SidePanelPage.Mcp, SidePanelPage.Files, SidePanelPage.Agents, SidePanelPage.Project, SidePanelPage.Processes],
            layout.SidePanelPages);
    }

    [Fact]
    public async Task A_new_file_from_Write_shows_as_added()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "notes.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\n", TestContext.Current.CancellationToken);

        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "w1", ["name"] = "Write", ["input"] = new JsonObject { ["file_path"] = path, ["content"] = "one\ntwo\n" } }) },
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "w1", ["content"] = "File created" }) },
            ["tool_use_result"] = new JsonObject { ["type"] = "create", ["filePath"] = path, ["content"] = "one\ntwo\n", ["originalFile"] = null },
        });

        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 1, "the changed file");
        var row = tab.ChangedFiles.Files[0];
        Assert.Equal("A", row.Status);
        Assert.Equal("notes.txt", row.DisplayPath);
        Assert.Equal("+2 −0", row.Stats);
        Assert.Equal("1 file changed", tab.ChangedFiles.Summary);
    }

    [Fact]
    public async Task An_edit_keeps_the_rows_of_the_other_files_as_they_were()
    {
        // Each edit used to clear the list and inspect every file again; now only what changed is redone.
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var (first, second) = (Path.Combine(h.WorkFolder, "a.cs"), Path.Combine(h.WorkFolder, "b.cs"));
        await File.WriteAllTextAsync(first, "b\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "b\n", TestContext.Current.CancellationToken);
        EmitEdit(h, "e1", first);
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 1, "the first file");
        var kept = tab.ChangedFiles.Files[0];

        await File.WriteAllTextAsync(second, "b\nc\n", TestContext.Current.CancellationToken);
        EmitEdit(h, "e2", second);
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 2, "the second file");

        Assert.Same(kept, tab.ChangedFiles.Files[0]);
        Assert.Equal("+2 −1", tab.ChangedFiles.Files[1].Stats);
        Assert.Equal(2, tab.ChangedFiles.Count);
    }

    [Fact]
    public async Task A_tab_in_the_background_counts_its_changed_files_and_lists_them_once_shown()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "a.cs");
        await File.WriteAllTextAsync(path, "b\n", TestContext.Current.CancellationToken);
        tab.IsSelected = false;

        EmitEdit(h, "e1", path);
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Count == 1, "the count");
        Assert.Empty(tab.ChangedFiles.Files);

        tab.IsSelected = true;
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 1, "the rows");
        Assert.Equal("+1 −1", tab.ChangedFiles.Files[0].Stats);
    }

    [Fact]
    public async Task Opening_a_changed_file_asks_for_the_diff_view()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "a.cs");
        await File.WriteAllTextAsync(path, "new\n", TestContext.Current.CancellationToken);
        var row = new ChangedFileRow { Path = path, DisplayPath = "a.cs", Status = "M", StatusText = "Modified", Before = "old\n" };
        Diffs.DiffSource? requested = null;
        tab.ChangedFiles.DiffRequested += source => requested = source;

        await tab.ChangedFiles.OpenFileCommand.ExecuteAsync(row);

        Assert.NotNull(requested);
        Assert.Equal("old\n", requested.Before);
        Assert.Null(requested.OpenInDiffTool);
    }

    [Fact]
    public async Task A_large_file_changed_live_is_saved_for_when_the_tab_is_restored()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "big.cs");
        await File.WriteAllTextAsync(path, LargeFile.Replace("line 0007", "line seven", StringComparison.Ordinal), TestContext.Current.CancellationToken);

        // Live, Claude Code sends the whole file as it was, however large.
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "e1", ["name"] = "Edit", ["input"] = LargeEditInput(path) }) },
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "e1", ["content"] = "The file has been updated." }) },
            ["tool_use_result"] = LargeEditResult(path, LargeFile),
        });

        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 1, "the changed file");
        Assert.Equal(("M", "+1 −1"), (tab.ChangedFiles.Files[0].Status, tab.ChangedFiles.Files[0].Stats));
        Assert.True(File.Exists(Path.Combine(h.Services.Paths.BeforeContentDirectory, "e1.txt.gz")));
    }

    [Fact]
    public async Task A_restored_tab_finds_the_large_file_its_transcript_leaves_out()
    {
        await using var h = new TabTestHarness();
        var path = Path.Combine(h.WorkFolder, "big.cs");
        await File.WriteAllTextAsync(path, LargeFile.Replace("line 0007", "line seven", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        new BeforeContentStore(h.Services.Paths.BeforeContentDirectory, h.Time).Save("e1", LargeFile);
        // Claude Code writes originalFile to the transcript as null over 10,000 characters.
        h.WriteTranscript("s1",
            Wire.Entry("assistant", "2026-09-28T11:00:00Z", Wire.Message(new JsonObject { ["type"] = "tool_use", ["id"] = "e1", ["name"] = "Edit", ["input"] = LargeEditInput(path) })),
            Wire.Entry("user", "2026-09-28T11:00:01Z", Wire.ResultMessage("e1", "The file has been updated."), LargeEditResult(path, null)));
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "s1" }];

        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.ChangedFiles.Files.Count == 1, "the changed file");
        var row = tab.ChangedFiles.Files[0];
        Assert.Equal(("M", "+1 −1", LargeFile), (row.Status, row.Stats, row.Before));
    }

    /// <summary>A file of over 10,000 characters, as it was before Claude changed line 7.</summary>
    private static readonly string LargeFile = string.Concat(Enumerable.Range(1, 400).Select(i => $"A line of a large file, line {i:D4}\n"));

    private static JsonObject LargeEditInput(string path) =>
        new() { ["file_path"] = path, ["old_string"] = "line 0007", ["new_string"] = "line seven" };

    private static JsonObject LargeEditResult(string path, string? originalFile) => new()
    {
        ["filePath"] = path,
        ["oldString"] = "line 0007",
        ["newString"] = "line seven",
        ["originalFile"] = originalFile,
        ["structuredPatch"] = JsonNode.Parse("""[{"oldStart":7,"oldLines":1,"newStart":7,"newLines":1,"lines":["-A line of a large file, line 0007","+A line of a large file, line seven"]}]"""),
        ["userModified"] = false,
        ["replaceAll"] = false,
    };

    [Fact]
    public async Task A_file_whose_before_is_unknown_opens_in_the_built_in_view_even_with_a_diff_tool()
    {
        await using var h = new TabTestHarness(s =>
        {
            s.DiffTool.Kind = "custom";
            s.DiffTool.CustomCommand = "meld {left} {right}";
        });
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "big.cs");
        await File.WriteAllTextAsync(path, "one\n2\n", TestContext.Current.CancellationToken);
        var row = new ChangedFileRow { Path = path, DisplayPath = "big.cs", Status = "M", StatusText = "Modified", BeforeKnown = false };
        Diffs.DiffSource? requested = null;
        tab.ChangedFiles.DiffRequested += source => requested = source;

        await tab.ChangedFiles.OpenFileCommand.ExecuteAsync(row);

        Assert.NotNull(requested);
        Assert.False(requested.BeforeKnown);
        Assert.Null(requested.OpenInDiffTool);
        Assert.StartsWith("what it held before Claude's first change isn't known", requested.BeforeLabel, StringComparison.Ordinal);

        // The view shows the file as it is now, with nothing marked as changed.
        var view = new Diffs.DiffWindowViewModel(requested, dark: false);
        await TabTestHarness.Eventually(() => !view.IsLoading, "the file");
        Assert.True(view.ShowWholeFile);
        Assert.Equal("", view.Stats);
        Assert.Equal(["one", "2"], view.InlineRows.Select(r => r.Text));
        Assert.All(view.InlineRows, r => Assert.Equal(Core.Diffs.DiffOp.Context, r.Op));
    }

    // ---- Reviewed (DESIGN.md §8) ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_ticked_file_stays_reviewed_until_Claude_changes_it_again()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "auth.cs");
        await File.WriteAllTextAsync(path, "b\n", TestContext.Current.CancellationToken);
        EmitEdit(h, "e1", path);
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 1, "the changed file");

        tab.ChangedFiles.ToggleFileReviewedCommand.Execute(tab.ChangedFiles.Files[0]);

        Assert.True(tab.ChangedFiles.Files[0].IsReviewed);
        Assert.Equal("1 file changed · 1 reviewed", tab.ChangedFiles.Summary);
        Assert.Equal([(path, "e1")], tab.State.ReviewedFiles.Select(m => (m.Path, m.Change)));

        // Your own edits don't count.
        await File.WriteAllTextAsync(path, "b\nmine\n", TestContext.Current.CancellationToken);
        await tab.ChangedFiles.RefreshCommand.ExecuteAsync(null);
        Assert.True(tab.ChangedFiles.Files[0].IsReviewed);

        EmitEdit(h, "e2", path);

        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files is [{ LatestChange: "e2" }], "Claude's next change");
        Assert.False(tab.ChangedFiles.Files[0].IsReviewed);
        Assert.Equal("1 file changed", tab.ChangedFiles.Summary);
        Assert.Empty(tab.State.ReviewedFiles);
    }

    [Fact]
    public async Task Unticking_a_file_clears_its_mark()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "auth.cs");
        await File.WriteAllTextAsync(path, "b\n", TestContext.Current.CancellationToken);
        EmitEdit(h, "e1", path);
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 1, "the changed file");
        var row = tab.ChangedFiles.Files[0];

        tab.ChangedFiles.ToggleFileReviewedCommand.Execute(row);
        tab.ChangedFiles.ToggleFileReviewedCommand.Execute(row);

        Assert.False(row.IsReviewed);
        Assert.Equal("1 file changed", tab.ChangedFiles.Summary);
        Assert.Empty(tab.State.ReviewedFiles);
    }

    [Fact]
    public async Task Reviewed_in_the_diff_view_ticks_the_file_and_closes_the_view()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "auth.cs");
        await File.WriteAllTextAsync(path, "b\n", TestContext.Current.CancellationToken);
        EmitEdit(h, "e1", path);
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 1, "the changed file");
        var view = await OpenDiffAsync(tab, tab.ChangedFiles.Files[0]);
        Assert.True(view.CanMarkReviewed);
        Assert.False(view.IsReviewed);
        var closed = false;
        view.CloseRequested += () => closed = true;

        view.MarkReviewedCommand.Execute(null);

        Assert.True(closed);
        Assert.True(view.IsReviewed);
        Assert.True(tab.ChangedFiles.Files[0].IsReviewed);
        Assert.Equal("1 file changed · 1 reviewed", tab.ChangedFiles.Summary);
        // Opened again, it shows the file is reviewed.
        Assert.True((await OpenDiffAsync(tab, tab.ChangedFiles.Files[0])).IsReviewed);
    }

    [Fact]
    public async Task Reviewed_marks_what_the_diff_view_showed_so_a_change_since_leaves_the_file_unreviewed()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "auth.cs");
        await File.WriteAllTextAsync(path, "b\n", TestContext.Current.CancellationToken);
        EmitEdit(h, "e1", path);
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 1, "the changed file");
        var view = await OpenDiffAsync(tab, tab.ChangedFiles.Files[0]);
        var closed = false;
        view.CloseRequested += () => closed = true;

        EmitEdit(h, "e2", path);
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files is [{ LatestChange: "e2" }], "Claude's next change");
        view.MarkReviewedCommand.Execute(null);

        Assert.True(closed);
        Assert.False(view.IsReviewed);
        Assert.False(tab.ChangedFiles.Files[0].IsReviewed);

        // Opened again, the view shows Claude's latest change, and marks that.
        (await OpenDiffAsync(tab, tab.ChangedFiles.Files[0])).MarkReviewedCommand.Execute(null);
        Assert.True(tab.ChangedFiles.Files[0].IsReviewed);
    }

    [Fact]
    public async Task A_restored_tab_keeps_its_reviewed_files()
    {
        await using var h = new TabTestHarness();
        var (reviewed, changedSince) = (Path.Combine(h.WorkFolder, "a.cs"), Path.Combine(h.WorkFolder, "b.cs"));
        await File.WriteAllTextAsync(reviewed, "b\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(changedSince, "b\n", TestContext.Current.CancellationToken);
        h.WriteTranscript("s1", [.. TranscriptEdit("e1", reviewed), .. TranscriptEdit("e2", changedSince), .. TranscriptEdit("e3", changedSince)]);
        h.Services.State.Tabs =
        [
            new TabState
            {
                Folder = h.WorkFolder,
                IsPinned = true,
                SessionId = "s1",
                ReviewedFiles = [new ReviewedFile { Path = reviewed, Change = "e1" }, new ReviewedFile { Path = changedSince, Change = "e2" }],
            },
        ];

        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.ChangedFiles.Files is [_, { LatestChange: "e3" }], "the changed files");
        Assert.Equal([true, false], tab.ChangedFiles.Files.Select(r => r.IsReviewed));
        Assert.Equal("2 files changed · 1 reviewed", tab.ChangedFiles.Summary);
        Assert.False(tab.AllFilesReviewed);
    }

    [Fact]
    public async Task Once_every_file_Claude_changed_is_ticked_the_reviewed_icon_covers_the_mark_until_one_is_not()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.SetMark(TabMark.Star);
        var (a, b, c) = (Path.Combine(h.WorkFolder, "a.cs"), Path.Combine(h.WorkFolder, "b.cs"), Path.Combine(h.WorkFolder, "c.cs"));
        foreach (var path in new[] { a, b, c })
        {
            await File.WriteAllTextAsync(path, "b\n", TestContext.Current.CancellationToken);
        }
        Assert.False(tab.AllFilesReviewed);
        EmitEdit(h, "e1", a);
        EmitEdit(h, "e2", b);
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 2, "the changed files");
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Tick(a);
        Assert.False(tab.AllFilesReviewed);
        Tick(b);

        Assert.True(tab.AllFilesReviewed);
        Assert.True(tab.State.AllFilesReviewed);
        Assert.True(tab.ShowsMarkIcon);
        Assert.Equal("All changed files reviewed · Marked with a star", tab.MarkTip);
        Assert.Contains(nameof(TabViewModel.AllFilesReviewed), changed);
        // The mark is still the tab's, under the icon.
        Assert.Equal(TabMark.Star, tab.Mark);
        tab.SetMark(null);
        Assert.True(tab.ShowsMarkIcon);
        Assert.Equal("All changed files reviewed", tab.MarkTip);
        tab.SetMark(TabMark.Star);

        // Claude changes a reviewed file again.
        EmitEdit(h, "e3", b);
        await TabTestHarness.Eventually(() => !tab.AllFilesReviewed, "Claude's change");
        Assert.False(tab.State.AllFilesReviewed);
        Assert.Equal("Marked with a star", tab.MarkTip);
        Tick(b);
        Assert.True(tab.AllFilesReviewed);

        // A file Claude hadn't changed before.
        EmitEdit(h, "e4", c);
        await TabTestHarness.Eventually(() => !tab.AllFilesReviewed, "Claude's change to another file");
        Tick(c);
        Assert.True(tab.AllFilesReviewed);

        // Unticking one.
        Tick(a);
        Assert.False(tab.AllFilesReviewed);
        Assert.Equal(TabMark.Star, tab.Mark);

        void Tick(string path) =>
            tab.ChangedFiles.ToggleFileReviewedCommand.Execute(tab.ChangedFiles.Files.Single(r => r.Path == path));
    }

    [Fact]
    public async Task A_tab_in_the_background_loses_the_reviewed_icon_as_Claude_changes_a_file_without_waiting_to_be_shown()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "a.cs");
        await File.WriteAllTextAsync(path, "b\n", TestContext.Current.CancellationToken);
        EmitEdit(h, "e1", path);
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 1, "the changed file");
        tab.ChangedFiles.ToggleFileReviewedCommand.Execute(tab.ChangedFiles.Files[0]);
        Assert.True(tab.AllFilesReviewed);

        tab.IsSelected = false;
        EmitEdit(h, "e2", path);

        await TabTestHarness.Eventually(() => !tab.AllFilesReviewed, "Claude's change");
        // The rows still wait for the tab to be shown.
        Assert.Equal("e1", tab.ChangedFiles.Files[0].LatestChange);
    }

    [Fact]
    public async Task A_restored_tab_whose_files_are_all_reviewed_shows_the_reviewed_icon()
    {
        await using var h = new TabTestHarness();
        var (first, second) = (Path.Combine(h.WorkFolder, "a.cs"), Path.Combine(h.WorkFolder, "b.cs"));
        await File.WriteAllTextAsync(first, "b\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "b\n", TestContext.Current.CancellationToken);
        h.WriteTranscript("s1", [.. TranscriptEdit("e1", first), .. TranscriptEdit("e2", second), .. TranscriptEdit("e3", second)]);
        h.Services.State.Tabs =
        [
            new TabState
            {
                Folder = h.WorkFolder,
                IsPinned = true,
                SessionId = "s1",
                Mark = "flag",
                ReviewedFiles = [new ReviewedFile { Path = first, Change = "e1" }, new ReviewedFile { Path = second, Change = "e3" }],
            },
        ];

        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.AllFilesReviewed, "the reviewed icon");
        Assert.Equal(TabMark.Flag, tab.Mark);
        Assert.True(tab.State.AllFilesReviewed);
    }

    [Fact]
    public async Task A_tab_restored_in_the_background_shows_its_saved_reviewed_icon_until_it_reads_its_conversation()
    {
        await using var h = new TabTestHarness();
        var path = Path.Combine(h.WorkFolder, "a.cs");
        Directory.CreateDirectory(h.WorkFolder);
        await File.WriteAllTextAsync(path, "b\n", TestContext.Current.CancellationToken);
        // Claude changed the file again after it was ticked, in a turn this tab never saw finish.
        h.WriteTranscript("s2", [.. TranscriptEdit("e1", path), .. TranscriptEdit("e2", path)]);
        h.Services.State.Tabs =
        [
            new TabState { Folder = h.WorkFolder, IsPinned = true },
            new TabState
            {
                Folder = h.WorkFolder,
                IsPinned = true,
                SessionId = "s2",
                ReviewedFiles = [new ReviewedFile { Path = path, Change = "e1" }],
                AllFilesReviewed = true,
            },
        ];

        h.Shell.Restore(null);
        var (first, second) = (h.Shell.AllTabs.First(), h.Shell.AllTabs.Last());
        await TabTestHarness.Eventually(() => first.Status == TabStatus.Idle && first.IsSettled, "the first tab to start");

        Assert.True(second.AllFilesReviewed);
        Assert.True(second.ShowsMarkIcon);
        Assert.Empty(second.Items);

        h.Shell.SelectTab(second.Id);

        await TabTestHarness.Eventually(() => second.Items.Count > 0 && !second.AllFilesReviewed, "the conversation read back");
        Assert.False(second.State.AllFilesReviewed);
        Assert.False(second.ShowsMarkIcon);
    }

    [Fact]
    public async Task A_tab_whose_conversation_is_gone_loses_its_saved_reviewed_icon()
    {
        await using var h = new TabTestHarness();
        Directory.CreateDirectory(h.WorkFolder);
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "cleaned-up", AllFilesReviewed = true }];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        await tab.EnsureStartedAsync();

        Assert.True(tab.IsSessionMissing);
        Assert.False(tab.AllFilesReviewed);
        Assert.False(tab.State.AllFilesReviewed);
    }

    [Fact]
    public async Task The_working_tree_view_shares_the_marks()
    {
        Assert.SkipWhen(FileProbe.Instance.FindOnPath(OperatingSystem.IsWindows() ? "git.exe" : "git") is null, "git isn't on PATH.");
        await using var h = new TabTestHarness();
        var init = await ProcessRunner.RunAsync(new ProcessLauncher(), new ProcessStartSpec("git", ["init", "-q"]) { WorkingDirectory = h.WorkFolder },
            TimeSpan.FromSeconds(30), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.Equal(0, init.ExitCode);
        var tab = await h.OpenTabAsync();
        var (claudes, yours) = (Path.Combine(h.WorkFolder, "a.cs"), Path.Combine(h.WorkFolder, "notes.md"));
        await File.WriteAllTextAsync(claudes, "b\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(yours, "mine\n", TestContext.Current.CancellationToken);
        EmitEdit(h, "e1", claudes);
        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 1, "the changed file");
        tab.ChangedFiles.ToggleFileReviewedCommand.Execute(tab.ChangedFiles.Files[0]);

        tab.ChangedFiles.ShowGitChanges = true;

        await TabTestHarness.Eventually(() => tab.ChangedFiles.Files.Count == 2, "the working tree");
        Assert.Equal([("a.cs", true), ("notes.md", false)], tab.ChangedFiles.Files.Select(r => (r.FileName, r.IsReviewed)).OrderBy(r => r.FileName));

        // A file only you changed stays reviewed until Claude changes it.
        tab.ChangedFiles.ToggleFileReviewedCommand.Execute(tab.ChangedFiles.Files.Single(r => r.FileName == "notes.md"));
        Assert.Equal("2 files changed · 2 reviewed", tab.ChangedFiles.Summary);
        EmitEdit(h, "e2", yours);
        // Git is asked once the edits pause.
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(ChangedFilesViewModel.GitRefreshDelay);
            return tab.ChangedFiles.Files.SingleOrDefault(r => r.FileName == "notes.md") is { LatestChange: "e2", IsReviewed: false };
        }, "Claude's change");
        Assert.True(tab.ChangedFiles.Files.Single(r => r.FileName == "a.cs").IsReviewed);
    }

    [Fact]
    public async Task Git_is_asked_once_edits_pause_and_not_for_a_tab_in_the_background()
    {
        var statuses = 0;
        var git = new FakeLauncher
        {
            OnStart = (spec, process) =>
            {
                if (spec.Arguments.Contains("--show-toplevel"))
                {
                    process.WriteOutput(spec.WorkingDirectory!);
                    process.WriteOutput("");
                    process.Exit(0);
                    return;
                }
                if (spec.Arguments.Contains("status"))
                {
                    Interlocked.Increment(ref statuses);
                }
                process.Exit(0);
            },
        };
        await using var h = new TabTestHarness(launcher: git);
        var tab = await h.OpenTabAsync();
        tab.ChangedFiles.ShowGitChanges = true;
        await TabTestHarness.Eventually(() => Volatile.Read(ref statuses) == 1, "the first listing");

        for (var i = 0; i < 5; i++)
        {
            EmitEdit(h, $"e{i}", Path.Combine(h.WorkFolder, $"f{i}.cs"));
        }
        await TabTestHarness.Eventually(() => tab.Items.OfType<Conversation.ToolUseItem>().Count() == 5 && tab.IsSettled, "the edits");
        Assert.Equal(1, Volatile.Read(ref statuses));
        h.Time.Advance(ChangedFilesViewModel.GitRefreshDelay);
        await TabTestHarness.Eventually(() => Volatile.Read(ref statuses) == 2, "one listing for the five");

        // In the background (another tab selected), it waits to be shown.
        tab.IsSelected = false;
        EmitEdit(h, "e9", Path.Combine(h.WorkFolder, "f9.cs"));
        await TabTestHarness.Eventually(() => tab.Items.OfType<Conversation.ToolUseItem>().Count() == 6 && tab.IsSettled, "the edit");
        h.Time.Advance(ChangedFilesViewModel.GitRefreshDelay * 4);
        Assert.Equal(2, Volatile.Read(ref statuses));
        tab.IsSelected = true;
        await TabTestHarness.Eventually(() =>
        {
            h.Time.Advance(ChangedFilesViewModel.GitRefreshDelay);
            return Volatile.Read(ref statuses) == 3;
        }, "the listing once it's shown");
    }

    [Fact]
    public async Task A_refresh_that_ends_after_a_later_one_leaves_the_later_ones_rows()
    {
        // Git finds the repository when the test says, then lists what the working tree has at that point.
        var asked = new System.Collections.Concurrent.ConcurrentQueue<FakeProcess>();
        var untracked = "";
        var git = new FakeLauncher
        {
            OnStart = (spec, process) =>
            {
                if (spec.Arguments.Contains("--show-toplevel"))
                {
                    asked.Enqueue(process);
                    return;
                }
                if (spec.Arguments.Contains("status"))
                {
                    process.WriteOutput($"?? {untracked}\0");
                }
                process.Exit(spec.Arguments.Contains("status") ? 0 : 128);
            },
        };
        await using var h = new TabTestHarness(launcher: git);
        var tab = await h.OpenTabAsync();
        tab.ChangedFiles.ShowGitChanges = true;
        await TabTestHarness.Eventually(() => asked.Count == 1, "the first refresh");
        var older = tab.ChangedFiles.RefreshCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => asked.Count == 2, "the older refresh");
        var newer = tab.ChangedFiles.RefreshCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => asked.Count == 3, "the newer refresh");
        var processes = asked.ToArray();
        processes[0].Exit(128);

        untracked = "new.txt";
        Found(processes[2]);
        await newer;
        Assert.Equal(["new.txt"], InlineDispatcher.Read(() => tab.ChangedFiles.Files.Select(r => r.FileName).ToArray()));

        untracked = "old.txt";
        Found(processes[1]);
        await older;
        Assert.Equal(["new.txt"], InlineDispatcher.Read(() => tab.ChangedFiles.Files.Select(r => r.FileName).ToArray()));

        void Found(FakeProcess process)
        {
            process.WriteOutput(h.WorkFolder);
            process.WriteOutput("");
            process.Exit(0);
        }
    }

    /// <summary>A successful Edit of <paramref name="path"/>, live.</summary>
    private static void EmitEdit(TabTestHarness h, string id, string path)
    {
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(EditUse(id, path)) },
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = "The file has been updated." }) },
            ["tool_use_result"] = EditResult(path),
        });
    }

    /// <summary>A successful Edit of <paramref name="path"/>, as a transcript has it.</summary>
    private static string[] TranscriptEdit(string id, string path) =>
    [
        Wire.Entry("assistant", "2026-09-28T11:00:00Z", Wire.Message(EditUse(id, path))),
        Wire.Entry("user", "2026-09-28T11:00:01Z", Wire.ResultMessage(id, "The file has been updated."), EditResult(path)),
    ];

    private static JsonObject EditUse(string id, string path) => new()
    {
        ["type"] = "tool_use",
        ["id"] = id,
        ["name"] = "Edit",
        ["input"] = new JsonObject { ["file_path"] = path, ["old_string"] = "a", ["new_string"] = "b" },
    };

    private static JsonObject EditResult(string path) => new() { ["filePath"] = path, ["oldString"] = "a", ["newString"] = "b", ["originalFile"] = "a\n" };

    /// <summary>What selecting the row opens: the built-in diff view, loaded.</summary>
    private static async Task<Diffs.DiffWindowViewModel> OpenDiffAsync(TabViewModel tab, ChangedFileRow row)
    {
        Diffs.DiffSource? requested = null;
        void OnRequested(Diffs.DiffSource source) => requested = source;
        tab.ChangedFiles.DiffRequested += OnRequested;
        await tab.ChangedFiles.OpenFileDiffCommand.ExecuteAsync(row);
        tab.ChangedFiles.DiffRequested -= OnRequested;
        var view = new Diffs.DiffWindowViewModel(requested!, dark: false);
        await TabTestHarness.Eventually(() => !view.IsLoading, "the diff");
        return view;
    }

    [Fact]
    public async Task The_process_summary_counts_what_the_tab_started()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true);
        var tab = await h.OpenTabAsync();
        var tree = h.Trees.Trees[4242];
        tree.Children.Add((5001, "node"));

        h.Time.Advance(ProcessSampler.SummaryInterval);

        await TabTestHarness.Eventually(() => tab.ProcessMonitor.SummaryText is not null, "a sample");
        Assert.StartsWith("1 proc · ", tab.ProcessMonitor.SummaryText, StringComparison.Ordinal);
        Assert.True(tab.ProcessMonitor.HasBusyProcesses);
        Assert.Equal(["claude", "node"], tab.ProcessMonitor.Processes.Select(p => p.Name));
    }

    [Fact]
    public async Task A_process_row_shows_its_arguments_short_and_copies_its_PID()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true);
        var tab = await h.OpenTabAsync();
        h.Trees.Trees[4242].Children.Add((5001, "node"));
        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => tab.ProcessMonitor.Processes.Count == 2, "a sample");
        var node = tab.ProcessMonitor.Processes[1];

        Assert.Equal("--serve", node.ShortArguments);
        await tab.ProcessMonitor.CopyPidCommand.ExecuteAsync(node);
        Assert.Equal("5001", h.Platform.Clipboard);
    }

    [Fact]
    public async Task Each_process_keeps_its_row_from_sample_to_sample()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true);
        var tab = await h.OpenTabAsync();
        var tree = h.Trees.Trees[4242];
        tree.Children.Add((5001, "node"));
        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => tab.ProcessMonitor.Processes.Count == 2, "the first sample");
        var (claude, node) = (tab.ProcessMonitor.Processes[0], tab.ProcessMonitor.Processes[1]);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        tab.ProcessMonitor.Processes.CollectionChanged += (_, e) => changes.Add(e.Action);

        tree.Children.Add((5002, "dotnet"));
        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => tab.ProcessMonitor.Processes.Count == 3, "the second sample");

        // The rows that were there are the same rows; only the new one was added.
        Assert.Same(claude, tab.ProcessMonitor.Processes[0]);
        Assert.Same(node, tab.ProcessMonitor.Processes[1]);
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Add], changes);

        tree.Children.RemoveAll(c => c.Item1 == 5001);
        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => tab.ProcessMonitor.Processes.Count == 2, "the third sample");
        Assert.Equal(["claude", "dotnet"], tab.ProcessMonitor.Processes.Select(p => p.Name));
        Assert.Same(claude, tab.ProcessMonitor.Processes[0]);
    }

    [Fact]
    public async Task A_process_collapses_the_processes_under_it_and_counts_them_in_its_numbers()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true);
        var tab = await h.OpenTabAsync();
        var tree = h.Trees.Trees[4242];
        // claude → bash → node → esbuild → worker, each child at 20% and 50 MB.
        tree.Children.AddRange([(5001, "bash"), (5002, "node"), (5003, "esbuild"), (5004, "worker")]);
        tree.Parents[5002] = 5001;
        tree.Parents[5003] = 5002;
        tree.Parents[5004] = 5003;
        var monitor = tab.ProcessMonitor;
        IEnumerable<string> Shown() => monitor.Processes.Select(p => p.Name);
        ProcessRow Row(string name) => monitor.Processes.Single(p => p.Name == name);

        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => monitor.Processes.Count > 0, "a sample");

        // Nothing more than two levels under claude shows at first: node starts collapsed, with what's under it in its numbers.
        Assert.Equal(["claude", "bash", "node"], Shown());
        Assert.True(Row("bash").IsExpanded);
        var node = Row("node");
        Assert.True(node.HasChildren);
        Assert.False(node.IsExpanded);
        Assert.Equal("+2", node.CollapsedText);
        Assert.Equal($"{60:0.#}%", node.CpuText);
        Assert.Equal("150 MB", node.MemoryText);
        Assert.EndsWith("Its CPU and memory include the 2 processes collapsed under it.", node.Tooltip, StringComparison.Ordinal);
        Assert.Equal("", Row("bash").CollapsedText);
        Assert.Equal($"{20:0.#}%", Row("bash").CpuText);

        // Its arrow shows the next level, which starts collapsed too.
        monitor.ToggleProcessCommand.Execute(node);
        Assert.Equal(["claude", "bash", "node", "esbuild"], Shown());
        Assert.Same(node, Row("node"));
        Assert.Equal("", node.CollapsedText);
        Assert.Equal($"{20:0.#}%", node.CpuText);
        Assert.Equal("+1", Row("esbuild").CollapsedText);
        Assert.False(Row("esbuild").IsExpanded);

        // Collapsed stays collapsed from sample to sample.
        monitor.ToggleProcessCommand.Execute(Row("bash"));
        Assert.Equal(["claude", "bash"], Shown());
        Assert.Equal("+3", Row("bash").CollapsedText);
        Assert.Equal("200 MB", Row("bash").MemoryText);
        h.Time.Advance(ProcessSampler.SummaryInterval);
        var sampled = h.Time.GetUtcNow();
        await TabTestHarness.Eventually(() => monitor.Processes[0].Snapshot.FirstSeen == sampled, "the next sample");
        Assert.Equal(["claude", "bash"], Shown());

        // Expand all opens every level under the row; Collapse all closes them, so expanding it again shows one level.
        monitor.ExpandAllProcessesCommand.Execute(Row("bash"));
        Assert.Equal(["claude", "bash", "node", "esbuild", "worker"], Shown());
        Assert.False(Row("worker").HasChildren);
        monitor.CollapseAllProcessesCommand.Execute(Row("bash"));
        monitor.ToggleProcessCommand.Execute(Row("bash"));
        Assert.Equal(["claude", "bash", "node"], Shown());
        Assert.False(Row("node").IsExpanded);

        // The arrow's Alt+click: every level under it.
        monitor.ToggleProcessAllLevels(Row("node"));
        Assert.Equal(["claude", "bash", "node", "esbuild", "worker"], Shown());
    }

    [Fact]
    public async Task A_collapsed_process_is_forgotten_once_it_exits()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true);
        var tab = await h.OpenTabAsync();
        var tree = h.Trees.Trees[4242];
        tree.Children.AddRange([(5001, "bash"), (5002, "node")]);
        tree.Parents[5002] = 5001;
        var monitor = tab.ProcessMonitor;
        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => monitor.Processes.Count == 3, "a sample");
        monitor.ToggleProcessCommand.Execute(monitor.Processes[1]);
        Assert.Equal(["claude", "bash"], monitor.Processes.Select(p => p.Name));

        tree.Children.Clear();
        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => monitor.Processes.Count == 1, "the processes to exit");

        // Another program given its PID starts as any process at its depth does.
        tree.Children.AddRange([(5001, "make"), (5002, "cc")]);
        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => monitor.Processes.Count == 3, "the new processes");
        Assert.Equal(["claude", "make", "cc"], monitor.Processes.Select(p => p.Name));
    }

    [Fact]
    public async Task A_process_whose_parent_isnt_listed_sits_beside_claude()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true);
        var tab = await h.OpenTabAsync();
        var tree = h.Trees.Trees[4242];
        tree.Children.AddRange([(5001, "node"), (6001, "vite"), (6002, "esbuild")]);
        // vite's parent exited; esbuild runs under it.
        tree.Parents[6001] = 999;
        tree.Parents[6002] = 6001;
        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => tab.ProcessMonitor.Processes.Count == 4, "a sample");

        Assert.Equal(["claude", "node", "vite", "esbuild"], tab.ProcessMonitor.Processes.Select(p => p.Name));
        Assert.Equal([0.0, 14, 0, 14], tab.ProcessMonitor.Processes.Select(p => p.Indent));

        // Collapsing claude leaves it beside claude.
        tab.ProcessMonitor.ToggleProcessCommand.Execute(tab.ProcessMonitor.Processes[0]);
        Assert.Equal(["claude", "vite", "esbuild"], tab.ProcessMonitor.Processes.Select(p => p.Name));
        Assert.Equal("+1", tab.ProcessMonitor.Processes[0].CollapsedText);
    }

    [Fact]
    public async Task The_header_totals_the_tabs_processes_while_the_monitor_is_on()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true);
        var tab = await h.OpenTabAsync();
        h.Trees.Trees[4242].Children.Add((5001, "node"));

        h.Time.Advance(ProcessSampler.SummaryInterval);

        // claude's 1% and 100 MB with the child's 20% and 50 MB.
        await TabTestHarness.Eventually(() => h.Shell.ProcessTotalsText == "21% CPU · 150 MB", "the header's total");
        Assert.Equal($"Processes of every tab, Claude Code included\n{tab.DisplayName}: 1 proc · 21% CPU · 150 MB", h.Shell.ProcessTotalsTip);

        // Off in Tab settings: nothing to add up, so the header shows nothing.
        var previous = tab.State.Overrides;
        tab.State.Overrides = new TabOverrides { ShowProcessMonitor = false };
        await tab.ApplyOverridesAsync(previous);
        Assert.Null(h.Shell.ProcessTotalsText);
        Assert.Null(h.Shell.ProcessTotalsTip);

        // Back on, then the tab closes.
        previous = tab.State.Overrides;
        tab.State.Overrides = new TabOverrides();
        await tab.ApplyOverridesAsync(previous);
        await TabTestHarness.Eventually(() => h.Shell.ProcessTotalsText is not null, "a sample");
        await h.Shell.CloseTabCommand.ExecuteAsync(tab);
        await h.Shell.Confirmation!.ConfirmCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Shell.ProcessTotalsText is null, "the closed tab to leave the total");
    }

    [Fact]
    public async Task One_tab_can_turn_the_process_monitor_on_or_off_for_itself()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = false);
        var tab = await h.OpenTabAsync();
        h.Trees.Trees[4242].Children.Add((5001, "node"));
        Assert.False(tab.ProcessMonitor.IsOn);

        // On for this tab only, from Tab settings….
        var settings = new TabSettingsViewModel(h.Services, tab, () => { });
        Assert.Equal("Default (off)", settings.SelectedMonitor.Label);
        settings.SelectedMonitor = settings.MonitorChoices.Single(c => c.Label == "On");
        await settings.ApplyCommand.ExecuteAsync(null);

        Assert.True(tab.ProcessMonitor.IsOn);
        Assert.True(tab.State.Overrides.ShowProcessMonitor);
        Assert.True(tab.State.Overrides.HasAny);
        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => tab.ProcessMonitor.SummaryText is not null, "a sample");

        // Off for this tab while Settings has it on everywhere else.
        h.Services.Settings.Processes.ShowMonitor = true;
        settings = new TabSettingsViewModel(h.Services, tab, () => { });
        Assert.Equal("On", settings.SelectedMonitor.Label);
        settings.SelectedMonitor = settings.MonitorChoices.Single(c => c.Label == "Off");
        await settings.ApplyCommand.ExecuteAsync(null);

        Assert.False(tab.ProcessMonitor.IsOn);
        Assert.Null(tab.ProcessMonitor.SummaryText);
        Assert.Empty(tab.ProcessMonitor.Processes);

        // Use defaults follows Settings again.
        settings = new TabSettingsViewModel(h.Services, tab, () => { });
        settings.UseDefaultsCommand.Execute(null);
        await settings.ApplyCommand.ExecuteAsync(null);
        Assert.Null(tab.State.Overrides.ShowProcessMonitor);
        Assert.True(tab.ProcessMonitor.IsOn);
    }

    [Fact]
    public async Task A_background_task_is_stopped_through_Claude_Code()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true);
        var tab = await h.OpenTabAsync();
        h.Transport.Emit("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"b1","name":"Bash","input":{"command":"npm run dev","run_in_background":true}}]}}""");
        h.Transport.Emit("""{"type":"system","subtype":"task_started","task_id":"task-9","tool_use_id":"b1","description":"npm run dev","task_type":"local_bash"}""");
        await TabTestHarness.Eventually(() => tab.Items.OfType<Conversation.ToolUseItem>().Any(), "the Bash card");
        h.Trees.Trees[4242].Children.Add((6001, "node"));

        h.Time.Advance(ProcessSampler.SummaryInterval);
        await TabTestHarness.Eventually(() => tab.ProcessMonitor.Processes.Any(p => p.Name == "node"), "the process");
        var row = tab.ProcessMonitor.Processes.Single(p => p.Name == "node");
        Assert.Equal("Bash: npm run dev", row.ToolText);

        tab.ProcessMonitor.StopProcessCommand.Execute(row);
        await h.Shell.Confirmation!.ConfirmCommand.ExecuteAsync(null);

        await TabTestHarness.Eventually(() => h.Transport.SentControlSubtypes.Contains("stop_task"), "stop_task");
        Assert.Empty(h.Trees.Trees[4242].Stopped);
    }

    [Fact]
    public async Task Closing_a_tab_stops_its_processes_unless_asked_to_leave_them()
    {
        await using var h = new TabTestHarness();
        var first = await h.OpenTabAsync();
        var tree = h.Trees.Trees[4242];
        tree.Children.Add((5001, "vite"));

        await h.Shell.CloseTabCommand.ExecuteAsync(first);

        var confirmation = Assert.IsType<ConfirmationViewModel>(h.Shell.Confirmation);
        Assert.Contains("vite (5001)", confirmation.Message, StringComparison.Ordinal);
        Assert.Equal("Close, leave running", confirmation.SecondaryText);

        await confirmation.SecondaryCommand.ExecuteAsync(null);

        Assert.False(tree.Killed);
        Assert.Empty(h.Shell.AllTabs);
    }

    [Fact]
    public async Task Closing_a_tab_normally_stops_everything_it_started()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var tree = h.Trees.Trees[4242];
        tree.Children.Add((5001, "vite"));

        await h.Shell.CloseTabCommand.ExecuteAsync(tab);
        await h.Shell.Confirmation!.ConfirmCommand.ExecuteAsync(null);

        Assert.True(tree.Killed);
    }
}
