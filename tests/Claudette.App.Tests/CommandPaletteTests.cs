using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>The command palette (DESIGN.md §4, "Command palette").</summary>
public class CommandPaletteTests
{
    private readonly List<string> _ran = [];
    private int _closed;

    private CommandPaletteViewModel Palette() => new(
    [
        new("New tab", "Command", () => Run("new"), "Ctrl+T"),
        new("Close tab", "Command", () => Run("close"), "Ctrl+W"),
        new("History", "Command", () => Run("history"), "Ctrl+Shift+H"),
        new("api", "Tab", () => Run("tab api"), Detail: "/work/api"),
        new("Settings: Appearance", "Settings", () => Run("appearance")),
    ], () => _closed++);

    private Task Run(string what)
    {
        _ran.Add(what);
        return Task.CompletedTask;
    }

    [Fact]
    public void Everything_shows_before_anything_is_typed_and_the_first_is_chosen()
    {
        var palette = Palette();

        Assert.Equal(5, palette.Results.Count);
        Assert.Equal("New tab", palette.Selected?.Label);
    }

    [Fact]
    public void Typing_keeps_labels_with_its_letters_in_order_best_first()
    {
        var palette = Palette();

        palette.Query = "tab";
        Assert.Equal(["New tab", "Close tab", "api"], palette.Results.Select(r => r.Label));

        palette.Query = "ct";
        Assert.Equal("Close tab", palette.Results[0].Label);

        palette.Query = "zzz";
        Assert.False(palette.HasResults);
        Assert.Null(palette.Selected);
    }

    [Fact]
    public async Task Up_down_and_enter_run_the_chosen_entry_after_closing()
    {
        var palette = Palette();
        palette.Query = "tab";

        palette.MoveDownCommand.Execute(null);
        Assert.Equal("Close tab", palette.Selected?.Label);
        palette.MoveUpCommand.Execute(null);
        palette.MoveUpCommand.Execute(null);
        // Up from the first wraps to the last.
        Assert.Equal("api", palette.Selected?.Label);
        await palette.RunCommand.ExecuteAsync(null);

        Assert.Equal(1, _closed);
        Assert.Equal(["tab api"], _ran);
    }

    [Fact]
    public void A_match_at_the_start_of_a_word_beats_one_inside_it()
    {
        Assert.True(CommandPaletteViewModel.Score("Close tab", "t") > CommandPaletteViewModel.Score("Settings", "t"));
        Assert.Null(CommandPaletteViewModel.Score("History", "hz"));
    }
}
