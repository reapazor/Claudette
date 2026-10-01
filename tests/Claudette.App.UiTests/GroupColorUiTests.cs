using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>A folder group's color picker, from Change color… in the group's menu (DESIGN.md §4, "Grouped by folder").</summary>
public class GroupColorUiTests
{
    [AvaloniaFact]
    public async Task Change_color_opens_a_picker_beside_the_group_that_recolors_it_as_you_pick()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var group = h.Shell.Groups.Single();
        var label = GroupLabel(window);

        var flyout = OpenPicker(window, label);
        var picker = Assert.IsType<GroupColorPicker>(flyout.Content);
        await UiText.SettleUntilAsync(window, () => picker.IsEffectivelyVisible, "the picker");
        var shown = UiText.Describe(picker);

        // The group colors, named, with the group's own ringed; then the spectrum and hue slider, on the same color.
        var swatches = picker.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("swatch")).ToList();
        Assert.Equal(TabGroupViewModel.Palette.Select(p => p.Name), swatches.Select(AutomationProperties.GetName));
        Assert.Equal([true, false, false, false, false, false, false, false], swatches.Select(s => s.Classes.Contains("selected")));
        var spectrum = picker.GetVisualDescendants().OfType<ColorSpectrum>().Single();
        var hue = picker.GetVisualDescendants().OfType<ColorSlider>().Single();
        Assert.Equal(group.Color, spectrum.Color);
        Assert.Equal(ColorComponent.Component1, hue.ColorComponent);
        Assert.Equal("Saturation and brightness", AutomationProperties.GetName(spectrum));
        Assert.Equal("Hue", AutomationProperties.GetName(hue));

        // A hex code recolors the group's dot and line as it's typed, and moves the spectrum.
        var hex = picker.GetVisualDescendants().OfType<TextBox>().Single();
        hex.Text = "#12AB34";
        UiText.Settle(window);
        Assert.Equal(Color.Parse("#12AB34"), group.Color);
        Assert.Equal(Color.Parse("#12AB34"), spectrum.Color);
        Assert.Equal(Color.Parse("#12AB34"), ((ISolidColorBrush)label.GetVisualDescendants().OfType<Ellipse>().Single().Fill!).Color);
        Assert.DoesNotContain(swatches, s => s.Classes.Contains("selected"));

        // A swatch applies and closes the picker.
        swatches[2].Command!.Execute(swatches[2].CommandParameter);
        UiText.Settle(window);
        Assert.False(flyout.IsOpen);
        Assert.Equal(TabGroupViewModel.Palette[2].Color, group.Color);

        // The rail's group menu opens it too, beside the rail's group.
        h.Shell.Layout.ToggleSidebarCommand.Execute(null);
        UiText.Settle(window);
        var rail = GroupLabel(window);
        Assert.NotSame(label, rail);
        var railFlyout = OpenPicker(window, rail);
        await UiText.SettleUntilAsync(window, () => railFlyout.IsOpen, "the rail's picker");
        railFlyout.Hide();

        await Verify(shown);
    }

    private static Button GroupLabel(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("grouplabel") && b.IsEffectivelyVisible);

    private static Flyout OpenPicker(Window window, Button label)
    {
        label.ContextMenu!.Open(label);
        UiText.Settle(window);
        var item = label.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Change color…");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        label.ContextMenu.Close();
        UiText.Settle(window);
        return Assert.IsType<Flyout>(FlyoutBase.GetAttachedFlyout(label));
    }
}
