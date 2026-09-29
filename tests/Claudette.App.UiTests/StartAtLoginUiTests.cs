using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Claudette.App.Services;
using Claudette.App.Views;
using Claudette.Core.Development;

namespace Claudette.App.UiTests;

/// <summary>The main window opening minimized when Claudette starts at login (DESIGN.md §9, "Starting at login").</summary>
public class StartAtLoginUiTests
{
    [AvaloniaFact]
    public void A_window_started_minimized_comes_back_maximized_if_it_was()
    {
        var window = new MainWindow();
        var placement = new WindowPlacementTracker(window);
        placement.Apply(new WindowPlacement(100, 80, 1200, 800, IsMaximized: true));

        placement.StartMinimized();
        window.Show();

        Assert.Equal(WindowState.Minimized, window.WindowState);
        Assert.False(window.ShowActivated);
        // Closed before it's restored, it still comes back maximized next time.
        Assert.True(placement.Current().IsMaximized);

        window.WindowState = WindowState.Normal;

        Assert.Equal(WindowState.Maximized, window.WindowState);
        window.Close();
    }

    [AvaloniaFact]
    public void A_window_that_wasnt_maximized_comes_back_at_its_size()
    {
        var window = new MainWindow();
        var placement = new WindowPlacementTracker(window);
        placement.Apply(new WindowPlacement(100, 80, 1200, 800, IsMaximized: false));

        placement.StartMinimized();
        window.Show();
        window.WindowState = WindowState.Normal;

        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.False(placement.Current().IsMaximized);
        window.Close();
    }
}
