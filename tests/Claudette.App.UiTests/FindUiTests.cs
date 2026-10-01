using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>Find in the conversation (DESIGN.md §5, "Find").</summary>
public class FindUiTests
{
    [AvaloniaFact]
    public async Task The_find_shortcut_opens_the_bar_and_Enter_and_Esc_work_in_it()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);
        foreach (var text in new[] { "Fix the parser", "Now the lexer", "And the parser tests" })
        {
            tab.ComposerText = text;
            await tab.SendCommand.ExecuteAsync(null);
            h.Transport.EmitTurn("done");
            await UiText.SettleUntilAsync(window, () => tab.Status == ViewModels.TabStatus.Idle && tab.IsSettled, "the turn");
        }
        var primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        window.KeyPressQwerty(PhysicalKey.F, primary);
        window.KeyReleaseQwerty(PhysicalKey.F, primary);
        UiText.Settle(window);
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "FindBox");
        Assert.True(tab.Find.IsOpen);
        Assert.True(box.IsEffectivelyVisible);
        Assert.True(box.IsFocused);

        window.KeyTextInput("parser");
        UiText.Settle(window);
        Assert.Equal("2 of 2", tab.Find.CountText);
        var newest = tab.Items.OfType<UserMessageItem>().Last();
        Assert.Same(newest, tab.Find.Current);
        // The match the bar shows is marked.
        Assert.Equal([newest], Marked(window));

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiText.Settle(window);
        Assert.Equal("1 of 2", tab.Find.CountText);
        Assert.Equal([tab.Items.OfType<UserMessageItem>().First()], Marked(window));

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        UiText.Settle(window);
        Assert.False(tab.Find.IsOpen);
        Assert.Empty(Marked(window));
        Assert.True(window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "Composer").IsFocused);
    }

    private static List<object?> Marked(Window window) =>
        [.. window.GetVisualDescendants().OfType<ContentPresenter>().Where(p => p.Classes.Contains("findcurrent")).Select(p => p.Content)];
}
