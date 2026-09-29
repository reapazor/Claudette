using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Views;
using Path = Avalonia.Controls.Shapes.Path;

namespace Claudette.App.UiTests;

/// <summary>
/// The composer bar's menus (DESIGN.md §5, "Model &amp; effort"; §7): picking an item closes the menu and does what it
/// says. Items are picked the way a click picks them, through the button's own click, not by running its command.
/// </summary>
public class ComposerMenuUiTests
{
    [AvaloniaFact]
    public async Task Picking_an_effort_level_changes_the_effort()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });

        var (flyout, item) = await OpenAndFindAsync(window, "Effort", b => b.Content as string == "high");
        Pick(item);
        await UiText.SettleUntilAsync(window, () => tab.Effort == "high", "the effort to change");

        Assert.False(flyout.IsOpen);
        Assert.Contains("apply_flag_settings", h.Transport.SentControlSubtypes);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Picking_a_model_asks_to_switch()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });

        var (flyout, item) = await OpenAndFindAsync(window, "Model", b => b.DataContext is Core.Sessions.ModelInfo { Value: "haiku" });
        Pick(item);
        UiText.Settle(window);

        Assert.False(flyout.IsOpen);
        Assert.Equal("haiku", tab.PendingModel?.Value);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Picking_a_permission_mode_switches_to_it()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });

        var (flyout, item) = await OpenAndFindAsync(window, "Permission mode for this session",
            b => b.DataContext is ViewModels.PermissionModeChoice { Label: "Plan" });
        Pick(item);
        await UiText.SettleUntilAsync(window, () => tab.PermissionMode == "plan", "the mode to change");

        Assert.False(flyout.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Picking_a_suffix_adds_its_chip_and_checks_it_and_picking_it_again_takes_it_off()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var suffix = tab.AvailableSuffixes[0];

        var (flyout, item) = await OpenAndFindAsync(window, tab.Tips.Suffixes, b => b.CommandParameter == suffix);
        Assert.False(Check(item).IsVisible);
        Pick(item);
        UiText.Settle(window);

        Assert.False(flyout.IsOpen);
        Assert.Contains(tab.Chips, c => c.Suffix == suffix);

        (flyout, item) = await OpenAndFindAsync(window, tab.Tips.Suffixes, b => b.CommandParameter == suffix);
        Assert.True(Check(item).IsVisible);
        var other = ((Control)flyout.Content!).GetVisualDescendants().OfType<Button>().Single(b => b.CommandParameter == tab.AvailableSuffixes[1]);
        Assert.False(Check(other).IsVisible);
        Pick(item);
        UiText.Settle(window);

        Assert.False(flyout.IsOpen);
        Assert.Empty(tab.Chips);
        window.Close();

        static Path Check(Button row) => row.GetVisualDescendants().OfType<Path>().Single();
    }

    /// <summary>Opens the composer bar's menu with the given tooltip and finds one of its items.</summary>
    private static async Task<(Flyout Flyout, Button Item)> OpenAndFindAsync(Window window, string tip, Func<Button, bool> match)
    {
        var opener = window.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) as string == tip && b.Flyout is not null);
        var flyout = Assert.IsType<Flyout>(opener.Flyout);
        flyout.ShowAt(opener);
        var content = Assert.IsAssignableFrom<Control>(flyout.Content);
        Button? item = null;
        await UiText.SettleUntilAsync(window, () => (item = content.GetVisualDescendants().OfType<Button>().FirstOrDefault(match)) is not null, $"the {tip} menu");
        return (flyout, item!);
    }

    /// <summary>Enter on a button runs its click: the Click handlers, then its command, as a mouse click does.</summary>
    private static void Pick(Button item) =>
        item.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = item });
}
