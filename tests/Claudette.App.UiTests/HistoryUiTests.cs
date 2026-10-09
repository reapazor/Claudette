using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    [AvaloniaFact]
    public async Task Refreshing_or_searching_replies_leaves_the_list_where_it_is()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        h.WriteTranscript("s1", new JsonObject
        {
            ["type"] = "user",
            ["sessionId"] = "s1",
            ["cwd"] = h.WorkFolder,
            ["timestamp"] = "2026-09-01T00:00:00Z",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = "Fix the build" },
        }.ToJsonString());
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await UiText.SettleUntilAsync(window, () => !history.IsLoading, "History to load");
        UiText.Settle(window);
        var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "HistoryRows");
        double Top() => list.TranslatePoint(default, window)!.Value.Y;
        var top = Top();

        var loading = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Reading your sessions…");
        var refresh = history.RefreshCommand.ExecuteAsync(null);
        Assert.True(history.IsLoading);
        // The list stays; nothing appears above it to push it down.
        Assert.False(loading.IsVisible);
        UiText.Settle(window);
        Assert.Equal(top, Top());
        await refresh;

        history.Search = "nothing-matches-this";
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Search Claude's replies too");
        var search = history.SearchRepliesCommand.ExecuteAsync(null);
        Assert.True(history.IsSearchingReplies);
        Assert.True(button.IsVisible);
        UiText.Settle(window);
        Assert.Equal(top, Top());
        await search;
    }

    [AvaloniaFact]
    public async Task A_click_outside_closes_it_without_opening_anything()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        h.WriteTranscript("s1", new JsonObject
        {
            ["type"] = "user",
            ["sessionId"] = "s1",
            ["cwd"] = h.WorkFolder,
            ["timestamp"] = "2026-09-01T00:00:00Z",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = "Fix the build" },
        }.ToJsonString());
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);
        var tabs = h.Shell.AllTabs.Count();
        h.Shell.OpenHistoryCommand.Execute(null);
        await UiText.SettleUntilAsync(window, () => !h.Shell.History!.IsLoading, "History to load");
        UiText.Settle(window);

        // Inside it, even off its controls, it stays.
        var view = window.GetVisualDescendants().OfType<HistoryView>().Single();
        var title = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "History");
        Click(window, title.TranslatePoint(new Point(title.Bounds.Width + 40, title.Bounds.Height / 2), window)!.Value);
        Assert.True(h.Shell.IsHistoryOpen);

        // History is 760 wide, centred in the 1100 window: the right edge is outside it.
        Click(window, new Point(1080, 400));
        Assert.False(h.Shell.IsHistoryOpen);
        Assert.Equal(tabs, h.Shell.AllTabs.Count());
    }

    private static void Click(Window window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        UiText.Settle(window);
    }
}
