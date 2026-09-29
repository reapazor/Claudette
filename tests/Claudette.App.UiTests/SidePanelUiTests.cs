using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>The side panel rendered (DESIGN.md §3): its pages as tabs, and dragging its edge to resize it.</summary>
public class SidePanelUiTests
{
    [AvaloniaFact]
    public async Task The_page_showing_has_the_accent_line_and_the_others_are_muted()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
        UiText.Settle(window);
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        var pages = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("pagetab") && b.IsEffectivelyVisible).ToList();
        var (files, agents) = (pages[0], pages[1]);

        Assert.Contains("selected", files.Classes);
        Assert.Equal(Accent(view), LineUnder(files));
        Assert.Equal(Colors.Transparent, LineUnder(agents));
        Assert.Contains("muted", Label(agents).Classes);

        tab.ShowAgentsPageCommand.Execute(null);
        UiText.Settle(window);

        Assert.Equal(Accent(view), LineUnder(agents));
        Assert.Equal(Colors.Transparent, LineUnder(files));
        Assert.Contains("muted", Label(files).Classes);
        Assert.DoesNotContain("muted", Label(agents).Classes);
    }

    [AvaloniaFact]
    public async Task Dragging_the_edge_resizes_the_panel_and_leaves_the_conversation_room()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
        UiText.Settle(window);
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        var panel = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SidePanel");
        var edge = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SidePanelEdge");
        Assert.Equal(ShellViewModel.DefaultSidePanelWidth, panel.Bounds.Width);

        // Left widens it; the width is kept when the drag ends.
        Drag(window, edge, -100);
        Assert.Equal(ShellViewModel.DefaultSidePanelWidth + 100, panel.Bounds.Width);
        Assert.Equal(ShellViewModel.DefaultSidePanelWidth + 100, h.Services.State.SidePanelWidth);

        // However far it's dragged, the conversation keeps 360.
        Drag(window, edge, -2000);
        Assert.Equal(view.Bounds.Width - 360, panel.Bounds.Width);
        Assert.Equal(view.Bounds.Width - 360, tab.SidePanelWidth);

        // Right narrows it, down to its least.
        Drag(window, edge, 2000);
        Assert.Equal(ShellViewModel.MinSidePanelWidth, panel.Bounds.Width);
    }

    private static void Drag(Window window, Control edge, double by)
    {
        var from = edge.TranslatePoint(new Point(edge.Bounds.Width / 2, edge.Bounds.Height / 2), window)!.Value;
        var to = from.WithX(from.X + by);
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        UiText.Settle(window);
    }

    private static Color Accent(Control view) =>
        view.TryFindResource("AccentStatusBrush", view.ActualThemeVariant, out var brush) && brush is ISolidColorBrush solid ? solid.Color : default;

    private static TextBlock Label(Button page) => page.GetVisualDescendants().OfType<TextBlock>().First();

    /// <summary>The line under a page's tab: its template's border.</summary>
    private static Color LineUnder(Button page) =>
        page.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter").BorderBrush is ISolidColorBrush solid
            ? solid.Color
            : Colors.Transparent;
}
