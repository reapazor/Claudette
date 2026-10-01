using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>The command palette and prompt recall, by keyboard (DESIGN.md §4, §5).</summary>
public class CommandPaletteUiTests
{
    private static readonly RawInputModifiers Primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        UiText.Settle(window);
    }

    [AvaloniaFact]
    public async Task The_palette_opens_from_its_shortcut_filters_as_you_type_and_Enter_runs_the_choice()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);

        Press(window, PhysicalKey.P, Primary | RawInputModifiers.Shift);
        Assert.True(h.Shell.IsPaletteOpen);
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "QueryBox");
        Assert.True(box.IsFocused);

        window.KeyTextInput("find in");
        UiText.Settle(window);
        Assert.Equal("Find in the conversation", h.Shell.Palette!.Selected!.Label);
        Press(window, PhysicalKey.Enter);

        Assert.False(h.Shell.IsPaletteOpen);
        Assert.True(tab.Find.IsOpen);

        Press(window, PhysicalKey.P, Primary | RawInputModifiers.Shift);
        Press(window, PhysicalKey.Escape);
        Assert.False(h.Shell.IsPaletteOpen);
    }

    [AvaloniaFact]
    public async Task Up_in_an_empty_composer_brings_back_the_last_prompt()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);
        tab.ComposerText = "run the tests";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.EmitTurn();
        await UiText.SettleUntilAsync(window, () => tab.Status == ViewModels.TabStatus.Idle && tab.IsSettled, "the turn");
        var composer = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "Composer");
        composer.Focus();
        UiText.Settle(window);

        Press(window, PhysicalKey.ArrowUp);
        Assert.Equal("run the tests", composer.Text);
        Assert.Equal(composer.Text!.Length, composer.CaretIndex);

        Press(window, PhysicalKey.ArrowDown);
        Assert.Equal("", composer.Text ?? "");
    }
}
