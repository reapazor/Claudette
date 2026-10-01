using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>Accessibility rendered (DESIGN.md §3, "Accessibility").</summary>
public class AccessibilityUiTests
{
    [AvaloniaFact]
    public async Task Announcements_go_to_a_polite_live_region_no_one_sees()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var announcer = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "Announcer");

        h.Shell.Announce("work: Claude finished.");
        UiText.Settle(window);

        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(announcer));
        Assert.Equal("work: Claude finished.", AutomationProperties.GetName(announcer));
        Assert.Equal(0, announcer.Opacity);
        Assert.True(announcer.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void The_busy_dots_pulse_unless_the_window_reduces_motion()
    {
        static bool Pulses(bool reduce)
        {
            var dot = new TextBlock { Text = "●", Classes = { "status", "busy" } };
            var window = UiText.Show(dot, 200, 100);
            window.Classes.Set("reducemotion", reduce);
            for (var i = 0; i < 3; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }
            var pulses = dot.IsAnimating(Visual.OpacityProperty);
            window.Close();
            return pulses;
        }

        Assert.True(Pulses(reduce: false));
        Assert.False(Pulses(reduce: true));
    }

    [AvaloniaFact]
    public async Task Zoom_scales_everything_in_the_main_window()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var main = new MainWindowViewModel(h.Services);
        main.UseShell(h.Shell);
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 800 };
        window.Show();
        UiText.Settle(window);
        var shell = window.GetVisualDescendants().OfType<ShellView>().Single();
        Assert.Equal(1200, shell.Bounds.Width, 1);

        window.UseZoom(150);
        UiText.Settle(window);

        // The content lays out in 800 wide and is drawn half as big again.
        Assert.Equal(800, shell.Bounds.Width, 1);
        Assert.Equal(1200, shell.TranslatePoint(new Point(shell.Bounds.Width, 0), window)!.Value.X, 1);
        window.Close();
    }
}
