using Avalonia;
using Claudette.App.Services;
using Claudette.Core.Development;

namespace Claudette.App.Tests;

/// <summary>The main window's remembered position (DESIGN.md §14, "What stays on each machine").</summary>
public class WindowPlacementTests
{
    private static readonly PixelRect[] TwoScreens = [new(0, 0, 1920, 1040), new(1920, 0, 2560, 1400)];

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(2500, 300, true)]
    [InlineData(4420, 300, true)]
    [InlineData(4460, 300, false)]
    // A monitor that was unplugged since: the OS places the window instead.
    [InlineData(5000, 300, false)]
    [InlineData(100, -200, false)]
    [InlineData(-110, 100, false)]
    public void A_saved_position_is_used_only_while_its_title_bar_is_on_a_screen(int x, int y, bool used)
    {
        Assert.Equal(used, WindowPlacementTracker.IsOnScreen(new WindowPlacement(x, y, 1200, 800, false), TwoScreens));
    }
}
