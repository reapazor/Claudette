using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>A restored tab whose folder no longer exists (DESIGN.md §9, "Missing folder").</summary>
public class MissingFolderTests
{
    private static string UserLine(string sessionId, string text, string cwd) => new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        ["cwd"] = cwd,
        ["sessionId"] = sessionId,
        ["timestamp"] = "2026-09-28T10:00:00Z",
    }.ToJsonString();

    /// <summary>A pinned tab in a folder that's gone, with its session's transcript where Claude Code keeps it.</summary>
    private static TabViewModel RestoreMissing(TabTestHarness h, out string gone)
    {
        gone = Path.Combine(h.Root, "deleted-clone", "api");
        h.WriteTranscript("s1", UserLine("s1", "Fix the login bug", gone));
        h.Services.State.Tabs = [new TabState { Folder = gone, IsPinned = true, SessionId = "s1", UserName = "login fix" }];
        h.Shell.Restore(null);
        return h.Shell.AllTabs.Single();
    }

    [Fact]
    public async Task A_tab_whose_folder_is_gone_says_so_as_soon_as_it_is_restored()
    {
        await using var h = new TabTestHarness();

        var tab = RestoreMissing(h, out var gone);

        Assert.True(tab.IsFolderMissing);
        Assert.Equal(TabStatus.Error, tab.Status);
        Assert.Contains(gone, tab.FolderMissingText);
        Assert.Equal("Unpin and close", tab.CloseMissingText);
        // It's the folder, not Claude Code, and Restart wouldn't help.
        Assert.Equal("Its folder no longer exists", tab.StatusTip);
        Assert.Equal("Its folder no longer exists", tab.RowDetail);
        Assert.False(tab.CanRestart);
        Assert.Empty(h.Factory.Launches);
    }

    [Fact]
    public async Task Choosing_the_folder_moves_the_tab_there_and_carries_on_its_session()
    {
        await using var h = new TabTestHarness();
        var tab = RestoreMissing(h, out _);
        var moved = Path.Combine(h.Root, "moved", "api");
        Directory.CreateDirectory(moved);
        h.Platform.FolderToPick = moved;

        await tab.ChooseFolderCommand.ExecuteAsync(null);

        Assert.False(tab.IsFolderMissing);
        Assert.Equal(moved, tab.Folder);
        var group = Assert.Single(h.Shell.Groups);
        Assert.Equal(moved, group.Folder);
        Assert.Same(tab, group.Tabs.Single());
        // Claude Code keeps sessions by folder, so it resumes from a working copy of the transcript.
        var workingCopy = Path.Combine(h.Services.Paths.LocalSessionsDirectory, "s1.jsonl");
        Assert.Equal(workingCopy, tab.State.TranscriptPath);
        Assert.True(File.Exists(workingCopy));
        var launch = Assert.Single(h.Factory.Launches);
        Assert.Equal(moved, launch.WorkingDirectory);
        Assert.Equal(workingCopy, launch.Resume);
        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle, "the tab to start");
        Assert.Equal(moved, h.Services.State.Tabs.Single().Folder);
        Assert.Contains(h.Services.State.RecentFolders, r => r.Path == moved);
    }

    [Fact]
    public async Task Cancelling_the_folder_picker_leaves_the_tab_as_it_was()
    {
        await using var h = new TabTestHarness();
        var tab = RestoreMissing(h, out var gone);

        await tab.ChooseFolderCommand.ExecuteAsync(null);

        Assert.True(tab.IsFolderMissing);
        Assert.Equal(gone, tab.Folder);
        Assert.Empty(h.Factory.Launches);
    }

    [Fact]
    public async Task Unpin_and_close_closes_it_without_asking()
    {
        await using var h = new TabTestHarness();
        var tab = RestoreMissing(h, out _);

        tab.UnpinAndCloseCommand.Execute(null);

        await TabTestHarness.Eventually(() => !h.Shell.AllTabs.Any(), "the tab to close");
        Assert.Null(h.Shell.Confirmation);
        Assert.Empty(h.Services.State.Tabs);
    }
}
