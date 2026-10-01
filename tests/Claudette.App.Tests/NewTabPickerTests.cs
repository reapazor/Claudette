using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>The new-tab picker (DESIGN.md §4, "Opening a tab"): favorites, recent folders, search and the number keys.</summary>
public class NewTabPickerTests
{
    private static string Folder(TabTestHarness h, string name) => Directory.CreateDirectory(Path.Combine(h.Root, name)).FullName;

    private static NewTabPickerViewModel Open(TabTestHarness h)
    {
        h.Shell.NewTabCommand.Execute(null);
        return Assert.IsType<NewTabPickerViewModel>(h.Shell.Picker);
    }

    [Fact]
    public async Task Favorites_come_first_then_recent_folders_newest_first_numbered_for_the_keys()
    {
        await using var h = new TabTestHarness();
        var (api, web, docs) = (Folder(h, "api"), Folder(h, "web"), Folder(h, "docs"));
        var now = h.Time.GetUtcNow();
        FolderHistory.Touch(h.Services.State, api, now - TimeSpan.FromDays(3), 10);
        FolderHistory.Touch(h.Services.State, web, now - TimeSpan.FromHours(2), 10);
        FolderHistory.Touch(h.Services.State, docs, now - TimeSpan.FromMinutes(5), 10);
        FolderHistory.SetFavorite(h.Services.State, api, true);

        var picker = Open(h);

        Assert.Equal(["api"], picker.Favorites.Select(f => f.Name));
        Assert.Equal(["docs", "web"], picker.Recents.Select(f => f.Name));
        Assert.Equal(["1", "2", "3"], picker.All.Select(f => f.NumberText));
        Assert.Equal("5 min ago", picker.Recents[0].Details);
        Assert.Equal("2 h ago", picker.Recents[1].Details);
        Assert.Equal("3 days ago", picker.Favorites[0].Details);
        Assert.Same(picker.All[0], picker.Selected);
    }

    [Fact]
    public async Task Search_narrows_both_lists_by_path()
    {
        await using var h = new TabTestHarness();
        var (api, web) = (Folder(h, "api"), Folder(h, "web"));
        FolderHistory.Touch(h.Services.State, api, h.Time.GetUtcNow(), 10);
        FolderHistory.Touch(h.Services.State, web, h.Time.GetUtcNow(), 10);
        FolderHistory.SetFavorite(h.Services.State, web, true);
        var picker = Open(h);

        picker.Search = "API";

        Assert.False(picker.HasFavorites);
        Assert.Equal(["api"], picker.Recents.Select(f => f.Name));
        picker.Search = "nothing like it";
        Assert.True(picker.IsEmpty);
        Assert.Null(picker.Selected);
    }

    [Fact]
    public async Task A_number_opens_that_folder_and_closes_the_picker()
    {
        await using var h = new TabTestHarness();
        var (api, web) = (Folder(h, "api"), Folder(h, "web"));
        FolderHistory.Touch(h.Services.State, api, h.Time.GetUtcNow() - TimeSpan.FromHours(1), 10);
        FolderHistory.Touch(h.Services.State, web, h.Time.GetUtcNow(), 10);
        var picker = Open(h);

        await picker.OpenNumberAsync(2);

        Assert.Null(h.Shell.Picker);
        Assert.Equal(api, h.Shell.SelectedTab?.Folder);
        // Out of range does nothing.
        await Open(h).OpenNumberAsync(9);
        Assert.Single(h.Shell.AllTabs);
    }

    [Fact]
    public async Task A_folder_that_is_gone_is_marked_and_does_not_open()
    {
        await using var h = new TabTestHarness();
        var gone = Path.Combine(h.Root, "gone");
        FolderHistory.Touch(h.Services.State, gone, h.Time.GetUtcNow(), 10);
        var picker = Open(h);
        var entry = Assert.Single(picker.Recents);

        Assert.False(entry.Exists);
        Assert.StartsWith("not found", entry.Details, StringComparison.Ordinal);
        Assert.Null(picker.Selected);
        await picker.OpenCommand.ExecuteAsync(entry);
        Assert.Empty(h.Shell.AllTabs);
        Assert.NotNull(h.Shell.Picker);
    }

    [Fact]
    public async Task Favorites_and_recents_can_be_changed_from_the_picker()
    {
        await using var h = new TabTestHarness();
        var api = Folder(h, "api");
        FolderHistory.Touch(h.Services.State, api, h.Time.GetUtcNow(), 10);
        var picker = Open(h);

        picker.ToggleFavoriteCommand.Execute(picker.Recents.Single());
        Assert.Equal("Remove from favorites", Assert.Single(picker.Favorites).FavoriteMenuText);
        Assert.Empty(picker.Recents);
        Assert.True(FolderHistory.IsFavorite(h.Services.State, api));

        picker.ToggleFavoriteCommand.Execute(picker.Favorites.Single());
        picker.RemoveRecentCommand.Execute(picker.Recents.Single());
        Assert.True(picker.IsEmpty);
        Assert.Empty(h.Services.State.RecentFolders);
    }

    [Fact]
    public async Task Each_folder_says_how_many_tabs_are_open_in_it_worktree_tabs_included()
    {
        await using var h = new TabTestHarness();
        await h.OpenTabAsync();
        h.Shell.SelectedTab!.State.WorktreeOf = h.WorkFolder;
        h.Shell.SelectedTab.State.Folder = Path.Combine(h.WorkFolder, ".claude", "worktrees", "brisk-otter");

        var entry = Assert.Single(Open(h).Recents);

        Assert.Equal(1, entry.OpenTabs);
        Assert.EndsWith("1 open", entry.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Browse_opens_the_folder_picked()
    {
        await using var h = new TabTestHarness();
        var picker = Open(h);
        h.Platform.FolderToPick = h.WorkFolder;

        await picker.BrowseCommand.ExecuteAsync(null);

        Assert.Null(h.Shell.Picker);
        Assert.Equal(h.WorkFolder, h.Shell.SelectedTab?.Folder);
    }
}
