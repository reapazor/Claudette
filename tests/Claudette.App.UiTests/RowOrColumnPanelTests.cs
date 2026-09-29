using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Claudette.App.Controls;

namespace Claudette.App.UiTests;

/// <summary>
/// The header's weekly meters (DESIGN.md §6): side by side when they fit on one line, or else stacked one above the
/// other, at the start.
/// </summary>
public class RowOrColumnPanelTests
{
    private static (RowOrColumnPanel Panel, Border First, Border Second) Show(double width) => Show(width, out _);

    private static (RowOrColumnPanel Panel, Border First, Border Second) Show(double width, out Window window)
    {
        var first = new Border { Width = 120, Height = 16 };
        var second = new Border { Width = 100, Height = 20 };
        var panel = new RowOrColumnPanel { Spacing = 14, RowSpacing = 2, Children = { first, second } };
        window = UiText.Show(new Border { Width = width, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = panel });
        return (panel, first, second);
    }

    [AvaloniaFact]
    public void With_room_they_sit_side_by_side()
    {
        var (panel, first, second) = Show(300);

        Assert.False(panel.IsStacked);
        Assert.Equal(20, panel.Bounds.Height);
        // The shorter one is centered on the line, as in a grid row.
        Assert.Equal(new Rect(0, 2, 120, 16), first.Bounds);
        Assert.Equal(new Rect(134, 0, 100, 20), second.Bounds);
    }

    [AvaloniaFact]
    public void Without_room_they_stack_at_the_start()
    {
        var (panel, first, second) = Show(200);

        Assert.True(panel.IsStacked);
        Assert.Equal(16 + 2 + 20, panel.Bounds.Height);
        Assert.Equal(new Rect(0, 0, 120, 16), first.Bounds);
        Assert.Equal(new Rect(0, 18, 100, 20), second.Bounds);
    }

    [AvaloniaFact]
    public void Exactly_enough_room_is_still_one_line()
    {
        var (panel, _, second) = Show(234);

        Assert.False(panel.IsStacked);
        Assert.Equal(new Rect(134, 0, 100, 20), second.Bounds);
    }

    [AvaloniaFact]
    public void A_hidden_child_takes_no_room()
    {
        var (panel, first, second) = Show(200, out var window);

        second.IsVisible = false;
        UiText.Settle(window);

        Assert.False(panel.IsStacked);
        Assert.Equal(new Rect(0, 0, 120, 16), first.Bounds);
    }
}
