using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Themes;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core.Settings;

namespace Claudette.App.UiTests;

/// <summary>Settings → Appearance → Style, applied to a running app (DESIGN.md §3, "Visual style").</summary>
public class ClaudeStyleTests
{
    [AvaloniaFact]
    public void The_Claude_style_repaints_the_window_the_tokens_and_the_accent_and_Standard_puts_them_back()
    {
        var app = (App)Application.Current!;
        var before = app.RequestedThemeVariant;
        var window = new Window { Width = 200, Height = 100 };
        window.Show();
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            UiText.Settle(window);
            var systemBackground = BackgroundOf(window);
            var systemAccent = (Color)window.FindResource("SystemAccentColor")!;

            app.Colors.Apply(AppStyle.Claude);
            UiText.Settle(window);

            // Fluent's window background, Claudette's own tokens and the accent all change, while the app runs.
            Assert.Equal(Color.Parse("#FAF9F5"), BackgroundOf(window));
            Assert.Equal(Color.Parse("#F5F4ED"), Token(window, "SidebarBrush"));
            Assert.Equal(AppColors.ClaudeAccent, (Color)window.FindResource("SystemAccentColor")!);

            app.RequestedThemeVariant = ThemeVariant.Dark;
            UiText.Settle(window);

            Assert.Equal(Color.Parse("#262624"), BackgroundOf(window));
            Assert.Equal(Color.Parse("#1F1E1D"), Token(window, "SidebarBrush"));

            app.Colors.Apply(AppStyle.Standard);
            app.RequestedThemeVariant = ThemeVariant.Light;
            UiText.Settle(window);

            Assert.Equal(systemBackground, BackgroundOf(window));
            Assert.Equal(Color.Parse("#F0F0F2"), Token(window, "SidebarBrush"));
            Assert.Equal(systemAccent, (Color)window.FindResource("SystemAccentColor")!);
        }
        finally
        {
            app.Colors.Apply(AppStyle.Standard);
            app.RequestedThemeVariant = before;
            window.Close();
        }
    }

    private static Color BackgroundOf(Window window) => Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;

    private static Color Token(Window window, string key) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource(window.ActualThemeVariant, key)).Color;

    [AvaloniaFact]
    public async Task The_Claude_style_puts_your_messages_in_bubbles_on_the_right_and_makes_Send_a_round_arrow()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "Why does the parser drop the last line?";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.EmitTurn("It stops at the last newline.");
        await TabTestHarness.Eventually(() => !tab.IsWorking, "the reply");
        var window = new MainWindow { DataContext = new MainWindowViewModel(h.Services) { CurrentPage = h.Shell }, Width = 1000, Height = 700 };
        window.Show();
        await UiText.SettleUntilAsync(window, () => Bubble(window) is not null, "the message");

        // Standard: the VS Code extension's bordered box, full width, and a Send label.
        Assert.Equal(HorizontalAlignment.Stretch, Bubble(window)!.HorizontalAlignment);
        Assert.Equal(new CornerRadius(8), Bubble(window)!.CornerRadius);
        Assert.True(SendPart<TextBlock>(window).IsEffectivelyVisible);
        Assert.False(SendPart<Path>(window).IsEffectivelyVisible);

        h.Services.Settings.Appearance.Style = AppStyle.Claude;
        h.Services.SaveSettings();
        UiText.Settle(window);

        Assert.Equal(HorizontalAlignment.Right, Bubble(window)!.HorizontalAlignment);
        Assert.Equal(new CornerRadius(18), Bubble(window)!.CornerRadius);
        Assert.Equal(new Thickness(0), Bubble(window)!.BorderThickness);
        Assert.False(SendPart<TextBlock>(window).IsEffectivelyVisible);
        Assert.True(SendPart<Path>(window).IsEffectivelyVisible);
        var send = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("send"));
        Assert.Equal(32, send.Bounds.Width);
        Assert.Equal(32, send.Bounds.Height);
        Assert.Equal("Send", Avalonia.Automation.AutomationProperties.GetName(send));
        AssertDrawnAboutTheMiddle(send);

        // Stop too, while a turn runs.
        tab.ComposerText = "And the first line?";
        await tab.SendCommand.ExecuteAsync(null);
        await UiText.SettleUntilAsync(window, () => RoundButton(window, "stop") is { IsEffectivelyVisible: true }, "Stop");
        AssertDrawnAboutTheMiddle(RoundButton(window, "stop")!);
        h.Transport.EmitTurn("It keeps it.");
        await TabTestHarness.Eventually(() => !tab.IsWorking, "the reply");
        window.Close();
    }

    private static Button? RoundButton(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains(name));

    // The icon fills the button and is drawn about its middle: one centered by layout is rounded to a whole pixel, which
    // at 150% puts a 10 or 14 wide icon half a pixel off.
    private static void AssertDrawnAboutTheMiddle(Button button)
    {
        var icon = button.GetVisualDescendants().OfType<Path>().Single(p => p.IsEffectivelyVisible);
        Assert.Equal(new Point(0, 0), icon.TranslatePoint(new Point(0, 0), button));
        Assert.Equal(button.Bounds.Size, icon.Bounds.Size);
        Assert.Equal(new Point(16, 16), icon.Data!.Bounds.Center);
    }

    private static Border? Bubble(Window window) =>
        window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("usermessage"));

    private static T SendPart<T>(Window window) where T : Control =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("send")).GetVisualDescendants().OfType<T>().Single();
}
