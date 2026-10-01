using Avalonia.Media;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>
/// The colors of tab groups (DESIGN.md §4, "Grouped by folder"): the color a new group takes, and the one remembered for
/// a folder. Folders are only paths here: nothing is read or written on disk.
/// </summary>
public class GroupColorsTests
{
    private static readonly IReadOnlyList<Color> Palette = TabGroupViewModel.Palette.Select(p => p.Color).ToArray();

    private static readonly string Api = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "work", "api"));

    private static readonly string Web = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "work", "web"));

    [Fact]
    public void A_new_group_takes_the_first_group_color_no_open_group_has()
    {
        Assert.Equal(Palette[0], GroupColors.Next([]));
        Assert.Equal(Palette[1], GroupColors.Next([Palette[0]]));
        // A closed group's color is taken again first.
        Assert.Equal(Palette[1], GroupColors.Next([Palette[0], Palette[2]]));
        // A color of the user's own leaves the group colors free.
        Assert.Equal(Palette[0], GroupColors.Next([Color.Parse("#123456")]));
    }

    [Fact]
    public void With_every_group_color_open_the_next_comes_in_turn()
    {
        var open = Palette.ToList();
        Assert.Equal(Palette[0], GroupColors.Next(open));

        open.Add(Palette[0]);
        Assert.Equal(Palette[1], GroupColors.Next(open));
    }

    [Fact]
    public void A_folders_color_is_found_however_its_path_was_written()
    {
        var state = new AppState();
        state.FolderColors[Api + Path.DirectorySeparatorChar] = "#12ab34";

        Assert.Equal(Color.Parse("#12AB34"), GroupColors.Saved(state, Api));
        Assert.Null(GroupColors.Saved(state, Web));
    }

    [Fact]
    public void An_older_save_kept_a_group_colors_index_which_wraps_around()
    {
        var state = new AppState();
        state.GroupColors[Api] = 5;
        state.GroupColors[Web] = Palette.Count + 2;
        var negative = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "work", "tools"));
        state.GroupColors[negative] = -1;

        Assert.Equal(Palette[5], GroupColors.Saved(state, Api));
        Assert.Equal(Palette[2], GroupColors.Saved(state, Web));
        Assert.Equal(Palette[^1], GroupColors.Saved(state, negative));
    }

    [Fact]
    public void A_saved_color_that_isnt_one_falls_back_to_an_older_index_or_none()
    {
        var state = new AppState();
        state.FolderColors[Api] = "red";
        state.FolderColors[Web] = "#12AB3";
        state.GroupColors[Api] = 3;

        Assert.Equal(Palette[3], GroupColors.Saved(state, Api));
        Assert.Null(GroupColors.Saved(state, Web));
    }

    [Fact]
    public void Remembering_a_color_replaces_what_was_saved_for_the_folder_under_any_spelling()
    {
        var state = new AppState();
        state.FolderColors[Api + Path.DirectorySeparatorChar] = "#12AB34";
        state.GroupColors[Api] = 2;
        state.FolderColors[Web] = "#ABCDEF";

        GroupColors.Remember(state, Api, Color.Parse("#00FF00"));

        Assert.Equal([(Api, "#00FF00"), (Web, "#ABCDEF")], state.FolderColors.Select(p => (p.Key, p.Value)).Order());
        Assert.Empty(state.GroupColors);
    }

    [Fact]
    public void A_new_group_gets_its_folders_saved_color_else_the_next_and_keeps_it()
    {
        var state = new AppState();
        state.GroupColors[Api] = 5;

        // The saved color, even when an open group has it.
        Assert.Equal(Palette[5], GroupColors.ForNewGroup(state, Api, [Palette[5]]));
        Assert.Equal("#25B8B8", state.FolderColors[Api]);
        Assert.Empty(state.GroupColors);

        Assert.Equal(Palette[1], GroupColors.ForNewGroup(state, Web, [Palette[0]]));
        Assert.Equal(TabGroupViewModel.ToHex(Palette[1]), state.FolderColors[Web]);
    }
}
