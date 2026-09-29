using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>
/// The bar over the composer while a tab waits for a usage limit to reset, and the tab's row (DESIGN.md §6,
/// "Continuing after a limit resets").
/// </summary>
public class AutoContinueUiTests
{
    [AvaloniaFact]
    public async Task The_bar_says_when_the_task_continues_and_offers_to_leave_it_stopped()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });

        tab.ComposerText = "Refactor the parser";
        await tab.SendCommand.ExecuteAsync(null);
        HitLimit(h, h.Time.GetUtcNow() + TimeSpan.FromHours(2));
        await UiText.SettleUntilAsync(window, () => tab.HasLimitWait, "the wait");

        var bar = Bar(window, tab);
        Assert.True(bar.IsEffectivelyVisible);
        var waiting = UiText.Describe(bar);
        // The row says so in place of the model.
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Usage limit · continues at 14:00" && t.IsEffectivelyVisible);

        var dontContinue = bar.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Don't continue" && b.IsEffectivelyVisible);
        dontContinue.Command!.Execute(null);
        await UiText.SettleUntilAsync(window, () => tab.CanContinueAfterLimit, "the wait to stop");
        UiText.Settle(window);
        var stopped = UiText.Describe(bar);
        var close = bar.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Close");
        Assert.True(close.IsEffectivelyVisible);

        close.Command!.Execute(null);
        await UiText.SettleUntilAsync(window, () => !tab.HasLimitWait, "the bar to close");
        Assert.False(bar.IsEffectivelyVisible);

        await Verify($"Continuing:\n{waiting}\n\nAfter Don't continue:\n{stopped}");
    }

    /// <summary>The bar's border: the one around the wait's text.</summary>
    private static Border Bar(Window window, TabViewModel tab) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == tab.LimitWaitText)
            .GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("codeblock"));

    /// <summary>The turn runs into the session limit, as Claude Code reports it.</summary>
    private static void HitLimit(TabTestHarness h, DateTimeOffset reset)
    {
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default"}""");
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "rate_limit_event",
            ["rate_limit_info"] = new JsonObject { ["status"] = "rejected", ["resetsAt"] = reset.ToUnixTimeSeconds(), ["rateLimitType"] = "five_hour" },
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "result", ["subtype"] = "success", ["is_error"] = true, ["result"] = "You've hit your session limit · resets 2pm",
            ["api_error_status"] = 429, ["terminal_reason"] = "api_error", ["session_id"] = "s1",
        });
    }
}
