using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Claudette.App.Themes;
using Claudette.Core.Settings;

namespace Claudette.App.UiTests;

/// <summary>Settings → Appearance → Colors, applied to a running app (DESIGN.md §3, "Visual style").</summary>
public class ColorsTests
{
    [AvaloniaFact]
    public void Claude_colors_repaint_the_window_the_tokens_and_the_accent_and_System_accent_puts_them_back()
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

            app.Colors.Apply(ColorPalette.Claude);
            UiText.Settle(window);

            // Fluent's window background, Claudette's own tokens and the accent all change, while the app runs.
            Assert.Equal(Color.Parse("#FAF9F5"), BackgroundOf(window));
            Assert.Equal(Color.Parse("#F0EEE6"), Token(window, "SidebarBrush"));
            Assert.Equal(AppColors.ClaudeAccent, (Color)window.FindResource("SystemAccentColor")!);

            app.RequestedThemeVariant = ThemeVariant.Dark;
            UiText.Settle(window);

            Assert.Equal(Color.Parse("#262624"), BackgroundOf(window));
            Assert.Equal(Color.Parse("#1F1E1D"), Token(window, "SidebarBrush"));

            app.Colors.Apply(ColorPalette.System);
            app.RequestedThemeVariant = ThemeVariant.Light;
            UiText.Settle(window);

            Assert.Equal(systemBackground, BackgroundOf(window));
            Assert.Equal(Color.Parse("#F0F0F2"), Token(window, "SidebarBrush"));
            Assert.Equal(systemAccent, (Color)window.FindResource("SystemAccentColor")!);
        }
        finally
        {
            app.Colors.Apply(ColorPalette.System);
            app.RequestedThemeVariant = before;
            window.Close();
        }
    }

    private static Color BackgroundOf(Window window) => Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;

    private static Color Token(Window window, string key) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource(window.ActualThemeVariant, key)).Color;
}
