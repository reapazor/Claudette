using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;
using Claudette.Platform.Shell;

namespace Claudette.App.Tests;

/// <summary>Recent folders for the jump list and Open Recent, and launch arguments (DESIGN.md §4, "Other ways in").</summary>
public class PlatformChromeTests
{
    [Fact]
    public async Task Recent_folders_reach_the_jump_list_when_they_change()
    {
        await using var h = new TabTestHarness();
        var jumpList = new FakeJumpList();
        var main = new MainWindowViewModel(h.Services);
        using var chrome = new PlatformChrome(null, null, main, h.Services, jumpList);
        Assert.Single(jumpList.Updates);
        Assert.Empty(chrome.Folders);

        await h.OpenTabAsync();

        Assert.Equal(2, jumpList.Updates.Count);
        var folder = Assert.Single(chrome.Folders);
        Assert.Equal(FolderHistory.Normalize(h.WorkFolder), folder.Path);
        Assert.Equal("work", folder.Label);

        // Saving state for other reasons doesn't rebuild the list.
        h.Services.SaveState();
        Assert.Equal(2, jumpList.Updates.Count);
    }

    [Theory]
    [InlineData(new[] { "--folder", "/work/api" }, "/work/api")]
    [InlineData(new[] { "--other", "--folder", "relative" }, "relative")]
    [InlineData(new[] { "--folder" }, null)]
    [InlineData(new string[0], null)]
    public void Reads_the_folder_argument(string[] args, string? expected)
    {
        Assert.Equal(expected is null ? null : Path.GetFullPath(expected), LaunchArguments.Folder(args));
    }

    [Fact]
    public void A_relative_folder_is_made_absolute_where_the_launch_started_before_it_is_handed_on()
    {
        var started = Path.Combine(Path.GetTempPath(), "launched-here");

        var args = LaunchArguments.WithFullPaths(["--folder", "api", "--source-build", "../bin", "--other", "x"], started);

        Assert.Equal(["--folder", Path.Combine(started, "api"), "--source-build", Path.GetFullPath(Path.Combine(started, "..", "bin")), "--other", "x"], args);
        // The running Claudette that gets them reads the same folder, wherever it runs from.
        Assert.Equal(Path.Combine(started, "api"), LaunchArguments.Folder(args));
    }

    private sealed class FakeJumpList : IJumpList
    {
        public List<IReadOnlyList<RecentFolderEntry>> Updates { get; } = [];

        public void Update(IReadOnlyList<RecentFolderEntry> folders) => Updates.Add(folders);
    }
}
