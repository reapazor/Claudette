using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Claudette.App.Controls;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>
/// What fills a tab's context window, in the flyout of its context ring and of the composer's indicator (DESIGN.md §6,
/// "Per-tab context").
/// </summary>
public class ContextBreakdownUiTests
{
    private static JsonObject Usage() => JsonNode.Parse("""
        {
          "categories": [
            { "name": "System prompt", "tokens": 3100, "kind": "used" },
            { "name": "System tools", "tokens": 14200, "kind": "used" },
            { "name": "Memory files", "tokens": 2400, "kind": "used" },
            { "name": "Messages", "tokens": 102600, "kind": "used" },
            { "name": "Free space", "tokens": 44700, "kind": "free" },
            { "name": "Autocompact buffer", "tokens": 33000, "kind": "buffer" }
          ],
          "totalTokens": 122300, "maxTokens": 200000, "percentage": 61,
          "memoryFiles": [ { "path": "/work/nexus/CLAUDE.md", "type": "Project", "tokens": 2400 } ],
          "messageBreakdown": { "toolCallTokens": 8000, "toolResultTokens": 90000, "userMessageTokens": 4600,
                                "toolCallsByType": [ { "name": "Read", "callTokens": 8000, "resultTokens": 90000 } ] },
          "isAutoCompactEnabled": true, "autoCompactThreshold": 167000
        }
        """)!.AsObject();

    [AvaloniaFact]
    public async Task The_ring_on_a_tabs_row_shows_what_fills_its_context_without_selecting_it_as_the_composer_does()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        h.Transport.Answers["get_context_usage"] = _ => Usage();
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        await UiText.SettleUntilAsync(window, () => tab.Context.Breakdown is not null, "the breakdown");

        // A click on the ring opens the breakdown. The row doesn't see the click, so another tab would stay selected.
        var ring = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ContextRing");
        var row = ring.GetVisualAncestors().OfType<Button>().First(b => b.Classes.Contains("tabrow"));
        // The ring's own click bubbles up through the row; the row selects its tab only when it's clicked itself.
        var rowClicks = 0;
        row.Click += (_, e) => rowClicks += e.Source == row ? 1 : 0;
        Assert.Equal("Context 61%", AutomationProperties.GetName(ring));
        Click(window, ring);
        var flyout = Assert.IsType<Flyout>(ring.Flyout);
        await UiText.SettleUntilAsync(window, () => flyout.IsOpen, "the ring's flyout");
        Assert.Equal(0, rowClicks);
        var view = Assert.IsType<ContextBreakdownView>(flyout.Content);
        Assert.Same(tab, view.DataContext);

        // The bar has a part for each row, and the Messages section opens to what's in them.
        var bar = view.GetVisualDescendants().OfType<ContextBar>().Single(b => b.Name == "Bar");
        Assert.Equal(6, bar.Segments!.Count);
        Assert.True(bar.Bounds.Width > 300, $"The bar is {bar.Bounds.Width} px wide.");
        Assert.StartsWith("Memory files: 2,400 tokens, 1%; System prompt", AutomationProperties.GetName(bar), StringComparison.Ordinal);
        var messages = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Messages");
        messages.Command!.Execute(null);
        UiText.Settle(window);
        var shown = UiText.Describe(view);
        flyout.Hide();
        UiText.Settle(window);

        // The composer's indicator opens the same breakdown.
        var indicator = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ContextButton");
        var composerFlyout = Assert.IsType<Flyout>(indicator.Flyout);
        composerFlyout.ShowAt(indicator);
        await UiText.SettleUntilAsync(window, () => composerFlyout.IsOpen, "the composer's flyout");
        var composerView = Assert.IsType<ContextBreakdownView>(composerFlyout.Content);
        Assert.Same(tab, composerView.DataContext);
        Assert.Equal(shown, UiText.Describe(composerView));

        // Compact now runs /compact and closes the flyout.
        var compact = composerView.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Compact now");
        compact.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        compact.Command!.Execute(null);
        await UiText.SettleUntilAsync(window, () => !composerFlyout.IsOpen, "the flyout to close");
        await UiText.SettleUntilAsync(window, () => h.Transport.SentUserTexts.Contains("/compact"), "/compact");
        window.Close();

        await Verify(shown);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Each_part_has_a_color_in_the_theme(string variant)
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        try
        {
            app.RequestedThemeVariant = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
            var bar = new ContextBar();
            var window = UiText.Show(new Border { Child = bar }, 400, 40);
            string[] keys = [.. ContextBreakdown.Parts.Select(p => p.Brush), ContextBreakdown.OtherBrush, ContextBreakdown.FreeBrush,
                ContextBreakdown.BufferBrush, ContextBreakdown.EstimateBrush];

            var brushes = keys.Select(key => bar.TryFindResource(key, bar.ActualThemeVariant, out var brush) ? brush as IBrush : null).ToList();

            Assert.All(keys.Zip(brushes), pair => Assert.True(pair.Second is not null, $"No {pair.First} in {variant}."));
            // The parts' colors are all different.
            var colors = brushes.Take(ContextBreakdown.Parts.Count + 1).Cast<ISolidColorBrush>().Select(b => b.Color).ToList();
            Assert.Equal(colors.Count, colors.Distinct().Count());
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = before;
        }
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
