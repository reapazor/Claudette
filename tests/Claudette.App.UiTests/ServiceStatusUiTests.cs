using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core.Auth;
using Claudette.Core.Settings;
using Claudette.Core.Status;
using Claudette.Core.Tests.Support;

namespace Claudette.App.UiTests;

/// <summary>
/// Claude's service status rendered (DESIGN.md §18, "Service status"): the dot before the account name with its
/// tooltip, and the banner across the top, in the Standard and Claude styles, light and dark.
/// </summary>
public class ServiceStatusUiTests
{
    private static readonly string Incident = File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "status", "summary-incident.json"));

    private const string Maintenance = """
        {
          "status": { "indicator": "maintenance" },
          "components": [
            { "id": "code", "name": "Claude Code", "status": "operational" },
            { "id": "api", "name": "Claude API (api.anthropic.com)", "status": "under_maintenance" },
            { "id": "web", "name": "claude.ai", "status": "operational" }
          ],
          "incidents": [],
          "scheduled_maintenances": [ { "id": "m1", "name": "Scheduled database maintenance", "status": "in_progress", "components": [ { "id": "api", "name": "Claude API (api.anthropic.com)" } ] } ]
        }
        """;

    /// <summary>The main window, signed in, once the status page has answered with <paramref name="summary"/>.</summary>
    private static async Task<(TabTestHarness H, MainWindow Window)> ShowAsync(string summary)
    {
        var page = new FakeHttpHandler().OnJson(StatusFeed.SummaryUrl, summary);
        var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher(), http: page);
        var main = new MainWindowViewModel(h.Services) { CurrentPage = h.Shell };
        main.Account.Status = new AuthStatus(true, "claude.ai", null, "me@example.com", null, "max", null, null);
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 800 };
        window.Show();
        h.Services.ServiceStatus.Start();
        await UiText.SettleUntilAsync(window, () => h.Services.ServiceStatus.Report is not null, "the status");
        return (h, window);
    }

    private static Button Dot(Window window) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ServiceStatusDot");

    private static Border Banner(Window window) => window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ServiceStatusBanner");

    private static Button BannerButton(Window window, string label) =>
        Banner(window).GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == label);

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static Color Token(Window window, string key) => ColorOf(window.FindResource(window.ActualThemeVariant, key) as IBrush);

    [AvaloniaFact]
    public async Task The_dot_sits_before_the_account_with_each_service_in_its_tooltip_and_the_banner_names_the_incident()
    {
        var (h, window) = await ShowAsync(Incident);
        await using var _ = h;
        var dot = Dot(window);
        var banner = Banner(window);

        Assert.True(dot.IsEffectivelyVisible);
        Assert.Equal("Claude's service status: Claude has an outage", AutomationProperties.GetName(dot));
        Assert.Equal("""
            Claude has an outage
            Claude Code: Partial outage
            Claude API: Degraded performance
            claude.ai: Partial outage
            Latest incident: Elevated errors on claude.ai, Claude Code, Claude Cowork and the Claude API (investigating)
            as of 12:00
            Click to open status.claude.com
            """.ReplaceLineEndings("\n"), ToolTip.GetTip(dot));
        Assert.Contains("outage", dot.GetVisualDescendants().OfType<Ellipse>().Single().Classes);
        // Just before the account name, at the header's right.
        var account = window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Account");
        var row = Assert.IsType<StackPanel>(dot.Parent);
        Assert.Equal(row.Children.IndexOf(account) - 1, row.Children.IndexOf(dot));
        Assert.True(banner.IsEffectivelyVisible);
        Assert.DoesNotContain("info", banner.Classes);

        BannerButton(window, "Status page").Command!.Execute(null);
        UiText.Settle(window);
        Assert.Equal(["https://stspg.io/br61xzj05pp5"], h.Platform.OpenedUrls);

        await Verify(UiText.Describe(window, (h.Root, "{root}")));
    }

    [AvaloniaFact]
    public async Task Dismiss_hides_the_banner_and_the_dot_opens_the_status_page()
    {
        var (h, window) = await ShowAsync(Incident);
        await using var _ = h;

        Click(window, BannerButton(window, "Dismiss"));
        Assert.False(Banner(window).IsEffectivelyVisible);
        Assert.True(Dot(window).IsEffectivelyVisible);
        Click(window, Dot(window));

        Assert.Equal(["https://status.claude.com"], h.Platform.OpenedUrls);
        Assert.Equal(["4xvtc2gnq73l"], h.Services.State.DismissedServiceStatus?.IncidentIds);
    }

    [AvaloniaFact]
    public async Task The_dot_and_banners_use_each_styles_colors_in_light_and_dark()
    {
        var app = (App)Application.Current!;
        var before = app.RequestedThemeVariant;
        var (h, window) = await ShowAsync(Incident);
        await using var _ = h;
        try
        {
            foreach (var style in new[] { AppStyle.Standard, AppStyle.Claude })
            {
                foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    app.Colors.Apply(style);
                    app.RequestedThemeVariant = variant;
                    UiText.Settle(window);
                    var what = $"{style} {variant}";
                    Assert.True(Token(window, "MeterCriticalBrush") == ColorOf(Dot(window).GetVisualDescendants().OfType<Ellipse>().Single().Fill), what);
                    Assert.True(Token(window, "CautionBackgroundBrush") == ColorOf(Banner(window).Background), what);
                    Assert.True(Token(window, "CautionBorderBrush") == ColorOf(Banner(window).BorderBrush), what);
                }
            }
            // The Claude style's caution is its own warm tint, not Standard's yellow.
            Assert.Equal(Color.Parse("#33271F"), ColorOf(Banner(window).Background));
        }
        finally
        {
            app.Colors.Apply(AppStyle.Standard);
            app.RequestedThemeVariant = before;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Maintenance_is_an_amber_dot_and_a_quieter_banner()
    {
        var (h, window) = await ShowAsync(Maintenance);
        await using var _ = h;
        var banner = Banner(window);

        Assert.True(banner.IsEffectivelyVisible);
        Assert.Contains("info", banner.Classes);
        Assert.Equal(Token(window, "SubtleBrush"), ColorOf(banner.Background));
        Assert.Equal(Token(window, "MeterWarningBrush"), ColorOf(Dot(window).GetVisualDescendants().OfType<Ellipse>().Single().Fill));
        Assert.Contains("Claude maintenance in progress: Scheduled database maintenance", UiText.Describe(banner));
    }

    private static void Click(Window window, Control target)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        UiText.Settle(window);
    }
}
