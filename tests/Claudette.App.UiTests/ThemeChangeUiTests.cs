using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Themes;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core.Auth;
using Claudette.Core.Settings;
using Claudette.Usage;
using LiveMarkdown.Avalonia;

namespace Claudette.App.UiTests;

/// <summary>
/// Changing the theme or the style repaints everything at once, as a restart would (DESIGN.md §3, "Visual style";
/// GitHub issue #14).
/// </summary>
public class ThemeChangeUiTests
{
    /// <summary>
    /// Code blocks already shown keep their code and take the new theme's syntax colors. LiveMarkdown on its own left a
    /// one-line block without colors, and with nothing for Copy, until Claudette restarted.
    /// </summary>
    [AvaloniaFact]
    public async Task Code_blocks_keep_their_code_and_take_the_new_themes_colors()
    {
        string[] codes = ["var x = 1;", "/* one\n   two */\nvar y = 2;"];
        var app = Application.Current!;
        var theme = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = ThemeVariant.Light;
            await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
            await h.OpenTabAsync();
            h.Transport.EmitTurn(string.Concat(codes.Select(code => $"```csharp\n{code}\n```\n\n")));
            var window = UiText.Show(new ShellView { DataContext = h.Shell });
            await UiText.SettleUntilAsync(window, () => Shown(window), "the code blocks");
            var light = Blocks(window).Select(Colors).ToList();

            app.RequestedThemeVariant = ThemeVariant.Dark;
            UiText.Settle(window);

            var blocks = Blocks(window);
            Assert.Equal(codes, blocks.Select(b => b.Code?.ReplaceLineEndings("\n")));
            Assert.All(blocks.Zip(light), pair => Assert.NotEqual(pair.Second, Colors(pair.First)));
            // As blocks shown in the dark theme from the start color them.
            var opened = UiText.Show(new ShellView { DataContext = h.Shell });
            await UiText.SettleUntilAsync(opened, () => Shown(opened), "the code blocks");
            Assert.Equal(Blocks(opened).Select(Colors), blocks.Select(Colors));
        }
        finally
        {
            app.RequestedThemeVariant = theme;
        }

        static bool Shown(Window window) => Blocks(window) is { Count: 2 } blocks && blocks.All(b => b.CodeTextBlock is not null);
    }

    /// <summary>
    /// Every color in the main window after a change of theme or style is the one a window opened afterwards has: the
    /// usage header and its charts, the sidebar, the conversation and the composer.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("light", "dark")]
    [InlineData("dark", "light")]
    [InlineData("standard", "claude")]
    [InlineData("claude", "standard")]
    public async Task The_main_window_repaints_as_a_restart_would(string from, string to)
    {
        var app = (App)Application.Current!;
        var theme = app.RequestedThemeVariant;
        try
        {
            app.Colors.Apply(AppStyle.Standard);
            app.RequestedThemeVariant = ThemeVariant.Light;
            Apply(app, from);
            await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
            await h.OpenTabAsync();
            h.Transport.EmitTurn("Found **two** problems in `Parser.cs`.\n\n```csharp\nvar x = 1;\n```\n\n> A quote");
            // The detailed header, with its charts.
            h.Services.State.DetailedUsageHeader = true;
            await using var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));
            using var usage = new UsageViewModel(h.Services, tracker);
            tracker.OnRateLimitEvent(RateLimitEvent(0.62, h.Time.GetUtcNow().AddHours(2), 0.38));
            await TabTestHarness.Eventually(() => usage.HasData && h.Shell.SelectedTab!.Status != TabStatus.Working, "the meters and the reply");
            var main = new MainWindowViewModel(h.Services) { CurrentPage = h.Shell, Usage = usage };
            main.Account.Status = new AuthStatus(true, "claude.ai", null, "me@example.com", null, "max", null, null);
            var window = await ShowAsync(main);

            Apply(app, to);
            UiText.Settle(window);

            var opened = await ShowAsync(main);
            Assert.Equal(Brushes(opened), Brushes(window));
        }
        finally
        {
            app.Colors.Apply(AppStyle.Standard);
            app.RequestedThemeVariant = theme;
        }
    }

    private static void Apply(App app, string change)
    {
        switch (change)
        {
            case "light":
                app.RequestedThemeVariant = ThemeVariant.Light;
                break;
            case "dark":
                app.RequestedThemeVariant = ThemeVariant.Dark;
                break;
            case "standard":
                app.Colors.Apply(AppStyle.Standard);
                break;
            case "claude":
                app.Colors.Apply(AppStyle.Claude);
                break;
        }
    }

    private static async Task<Window> ShowAsync(MainWindowViewModel main)
    {
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 800 };
        window.Show();
        await UiText.SettleUntilAsync(window, () => Blocks(window) is [{ CodeTextBlock: not null }] && window.GetVisualDescendants().OfType<Control>().Any(c => c is { Name: "SessionChart", IsEffectivelyVisible: true }),
            "the reply and the charts");
        return window;
    }

    private static List<CodeBlock> Blocks(Visual root) => [.. root.GetVisualDescendants().OfType<CodeBlock>()];

    /// <summary>Each piece of the code's text with its color, line by line.</summary>
    private static List<string> Colors(CodeBlock block) => [.. Runs(block.Inlines).Select(r => $"{r.Text}:{(r.Foreground as ISolidColorBrush)?.Color}")];

    private static IEnumerable<Run> Runs(InlineCollection inlines) =>
        inlines.SelectMany(inline => inline switch
        {
            Run run => [run],
            Span span => Runs(span.Inlines),
            _ => [],
        });

    /// <summary>
    /// Every brush on every visible element, by where the element is in the window. Not the window's transparency
    /// fallback: Avalonia sets its brush only when the window's transparency changes, and it only shows through a window
    /// that asked for transparency and didn't get it, which MainWindow gives an opaque background then.
    /// </summary>
    private static Dictionary<string, string> Brushes(Window window)
    {
        var result = new Dictionary<string, string>();
        foreach (var visual in window.GetVisualDescendants().Where(v => v.IsEffectivelyVisible && (v as StyledElement)?.Name != "PART_TransparencyFallback"))
        {
            var path = string.Join("/", visual.GetVisualAncestors().Reverse().Append(visual).Skip(1)
                .Select(v => $"{v.GetType().Name}#{(v as StyledElement)?.Name}[{v.GetVisualParent()?.GetVisualChildren().TakeWhile(c => c != v).Count()}]"));
            foreach (var property in AvaloniaPropertyRegistry.Instance.GetRegistered(visual).Where(p => p.PropertyType == typeof(IBrush)))
            {
                result[$"{path}.{property.Name}"] = visual.GetValue(property) switch
                {
                    null => "none",
                    ISolidColorBrush solid => $"{solid.Color} {solid.Opacity}",
                    var brush => brush.GetType().Name,
                };
            }
        }
        return result;
    }

    private static Core.Protocol.RateLimitEventMessage RateLimitEvent(double session, DateTimeOffset resetsAt, double weekly)
    {
        var info = new JsonObject
        {
            ["status"] = "allowed",
            ["rateLimitType"] = "five_hour",
            ["unifiedWindows"] = new JsonObject
            {
                ["five_hour"] = new JsonObject { ["utilization"] = session, ["resetsAt"] = resetsAt.ToUnixTimeSeconds() },
                ["seven_day"] = new JsonObject { ["utilization"] = weekly, ["resetsAt"] = resetsAt.AddDays(3).ToUnixTimeSeconds() },
            },
        };
        return new Core.Protocol.RateLimitEventMessage(info, new JsonObject { ["type"] = "rate_limit_event", ["rate_limit_info"] = info.DeepClone() });
    }
}
