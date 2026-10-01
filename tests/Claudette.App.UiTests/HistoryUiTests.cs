using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>History's list (DESIGN.md §9, "History").</summary>
public class HistoryUiTests
{
    [AvaloniaFact]
    public async Task A_long_history_builds_only_the_rows_on_screen()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        for (var i = 0; i < 300; i++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"s{i:000}");
            h.WriteTranscript(id, new JsonObject
            {
                ["type"] = "user",
                ["sessionId"] = id,
                ["cwd"] = h.WorkFolder,
                ["timestamp"] = DateTimeOffset.Parse("2026-09-01T00:00:00Z", CultureInfo.InvariantCulture).AddMinutes(i).ToString("O"),
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = $"Prompt number {i}" },
            }.ToJsonString());
        }
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await UiText.SettleUntilAsync(window, () => !history.IsLoading, "History to load");
        UiText.Settle(window);

        var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "HistoryRows");
        Assert.Equal(301, history.Rows.Count);
        var built = list.GetRealizedContainers().Count();
        Assert.InRange(built, 2, 60);
        var titles = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        // Newest first, under the folder's heading.
        Assert.Contains("work", titles);
        Assert.Contains("Prompt number 299", titles);
    }
}
