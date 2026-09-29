using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Claudette.App.Controls;

namespace Claudette.App.UiTests;

/// <summary>
/// The composer's control bar (DESIGN.md §5): the chips at the start and the counts and Send at the end, on one line
/// when they fit, or the end's group on a second line, still at the end.
/// </summary>
public class ControlBarPanelTests
{
    private static (ControlBarPanel Bar, Border Chips, Border Send) Show(double width)
    {
        var chips = new Border { Width = 300, Height = 24 };
        var send = new Border { Width = 200, Height = 30 };
        var bar = new ControlBarPanel { RowSpacing = 4, Children = { chips, send } };
        UiText.Show(new Border { Width = width, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Child = bar });
        return (bar, chips, send);
    }

    [AvaloniaFact]
    public void With_room_both_groups_share_a_line_at_either_end()
    {
        var (bar, chips, send) = Show(600);

        Assert.False(bar.IsWrapped);
        Assert.Equal(30, bar.Bounds.Height);
        // The shorter group is centered on the line, as in a grid row.
        Assert.Equal(new Rect(0, 3, 300, 24), chips.Bounds);
        Assert.Equal(new Rect(400, 0, 200, 30), send.Bounds);
    }

    [AvaloniaFact]
    public void Without_room_the_end_group_moves_under_the_chips_still_at_the_end()
    {
        var (bar, chips, send) = Show(450);

        Assert.True(bar.IsWrapped);
        Assert.Equal(24 + 4 + 30, bar.Bounds.Height);
        Assert.Equal(new Rect(0, 0, 300, 24), chips.Bounds);
        Assert.Equal(new Rect(250, 28, 200, 30), send.Bounds);
    }

    [AvaloniaFact]
    public void Exactly_enough_room_is_still_one_line()
    {
        var (bar, _, send) = Show(500);

        Assert.False(bar.IsWrapped);
        Assert.Equal(new Rect(300, 0, 200, 30), send.Bounds);
    }
}
