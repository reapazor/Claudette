using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;

namespace Claudette.App.UiTests;

/// <summary>The bar over the composer while a check-in counts down (DESIGN.md §5, "Check-ins on long turns").</summary>
public class CheckInUiTests
{
    [AvaloniaFact]
    public async Task The_bar_counts_down_to_the_check_in_and_offers_to_send_it_now_or_not_at_all()
    {
        await using var h = new TabTestHarness(s => s.CheckIns = new CheckInSettings { RunTimeMinutes = 0, QuietTimeMinutes = 5, Message = "Status?" },
            dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.ComposerText = "Refactor the parser";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default"}""");
        await UiText.SettleUntilAsync(window, () => tab.Status == TabStatus.Working, "the turn");

        for (var i = 0; i < 30; i++)
        {
            h.Time.Advance(CheckInMonitor.TickInterval);
        }
        await UiText.SettleUntilAsync(window, () => tab.HasCheckInCountdown, "the countdown");
        UiText.Settle(window);

        var bar = Bar(window, tab);
        Assert.True(bar.IsEffectivelyVisible);
        var counting = UiText.Describe(bar);
        var skip = bar.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Don't check in");

        skip.Command!.Execute(null);
        await UiText.SettleUntilAsync(window, () => !tab.HasCheckInCountdown, "the bar to close");

        Assert.False(bar.IsEffectivelyVisible);
        Assert.DoesNotContain("Status?", h.Transport.SentUserTexts);
        await Verify(counting);
    }

    /// <summary>The bar's border: the one around the countdown's text.</summary>
    private static Border Bar(Window window, TabViewModel tab) =>
        window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == tab.CheckInCountdownText)
            .GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("codeblock"));
}
