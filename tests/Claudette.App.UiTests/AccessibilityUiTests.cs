using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
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
}
