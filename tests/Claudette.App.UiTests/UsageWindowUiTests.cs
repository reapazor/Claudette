using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Usage;

namespace Claudette.App.UiTests;

/// <summary>The Usage panel's Projects list (DESIGN.md §6, "Usage history").</summary>
public class UsageWindowUiTests
{
    [AvaloniaFact]
    public async Task The_projects_list_shows_each_folder_and_follows_the_period()
    {
        await using var h = new TabTestHarness();
        await using var tracker = new UsageTracker(h.Services, new UsageStore(Path.Combine(h.Root, "usage.db"), h.Time));
        using var header = new UsageViewModel(h.Services, tracker);
        var now = h.Time.GetUtcNow();
        tracker.Store.AddTurns(
        [
            new TurnRecord(now.AddDays(-20), "tab-1", null, "fable", 5_000, 0, 0, 0, 2, "/work/old"),
            new TurnRecord(now.AddHours(-1), "tab-2", null, "fable", 3_000, 0, 0, 0, 1, "/work/api"),
        ]);
        var panel = new UsagePanelViewModel(h.Services, tracker, header, _ => null);
        await panel.RefreshAsync();
        var window = new Views.UsageWindow { DataContext = panel, Width = 900, Height = 900 };
        window.Show();
        UiText.Settle(window);

        Assert.Contains("api", Texts(window));
        Assert.DoesNotContain("old", Texts(window));
        var period = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.ItemsSource == panel.ProjectPeriods);
        Assert.Equal("This week", period.SelectedItem?.ToString());

        period.SelectedItem = panel.ProjectPeriods.Single(p => p.Period == ProjectPeriod.Month);
        await panel.RefreshAsync();
        UiText.Settle(window);

        Assert.Contains("old", Texts(window));
        window.Close();
    }

    private static List<string?> Texts(Window window) =>
        [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];
}
