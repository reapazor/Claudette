using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Views;
using Claudette.Core.Settings;

namespace Claudette.App.UiTests;

/// <summary>
/// A tab's mark rendered (DESIGN.md §4, "Marks"): under the status icon on the tab's row, in the rail's square, in the
/// tab's menu and in History.
/// </summary>
public class TabMarkUiTests
{
    [AvaloniaFact]
    public async Task The_mark_sits_under_the_status_icon_level_with_the_second_line_without_making_the_row_taller()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabrow"));
        var mark = row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RowMark");
        var height = row.Bounds.Height;
        Assert.False(mark.IsEffectivelyVisible);

        tab.SetMark(TabMark.Question);
        UiText.Settle(window);

        Assert.True(mark.IsEffectivelyVisible);
        Assert.Equal("Marked with a question mark", ToolTip.GetTip(mark));
        Assert.Equal("Marked with a question mark", AutomationProperties.GetName(mark));
        var icon = mark.GetVisualDescendants().OfType<MarkIcon>().Single();
        Assert.NotNull(icon.Data);
        Assert.Equal(Brush(window, "WarningTextBrush"), (icon.Stroke as ISolidColorBrush)?.Color);
        var status = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("status"));
        var detail = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == tab.RowDetail);
        Assert.True(InRow(mark, row).Top >= InRow(status, row).Bottom - 0.5, "The mark isn't under the status icon.");
        Assert.True(InRow(mark, row).Top < InRow(detail, row).Bottom && InRow(detail, row).Top < InRow(mark, row).Bottom, "The mark isn't level with the second line.");
        Assert.True(Math.Abs(InRow(mark, row).Center.X - InRow(status, row).Center.X) < 0.5, "The mark isn't under the status icon.");
        Assert.Equal(height, row.Bounds.Height, precision: 3);

        // Each mark has its own icon; the star is filled.
        tab.SetMark(TabMark.Star);
        UiText.Settle(window);
        Assert.Equal(Brush(window, "MarkStarBrush"), (icon.Fill as ISolidColorBrush)?.Color);

        // The rail shows it in the square's corner, across from the status icon.
        h.Shell.Layout.IsSidebarCollapsed = true;
        UiText.Settle(window);
        var railMark = window.GetVisualDescendants().OfType<MarkIcon>().Single(m => m.Name == "RailMark");
        Assert.True(railMark.IsEffectivelyVisible);
        Assert.Equal("Marked with a star", AutomationProperties.GetName(railMark));
        var square = railMark.FindAncestorOfType<Button>()!;
        Assert.True(InRow(railMark, square).Left < square.Bounds.Width / 2 && InRow(railMark, square).Top > square.Bounds.Height / 2, "The rail's mark isn't in the bottom-left corner.");

        tab.SetMark(null);
        UiText.Settle(window);
        Assert.False(railMark.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_reviewed_icon_covers_the_mark_while_every_changed_file_is_reviewed()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        tab.SetMark(TabMark.Star);
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabrow"));
        var mark = row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RowMark");
        var icon = mark.GetVisualDescendants().OfType<MarkIcon>().Single();
        var path = Path.Combine(h.WorkFolder, "a.cs");
        await File.WriteAllTextAsync(path, "b\n", TestContext.Current.CancellationToken);
        EmitEdit(h, "e1", path);
        await UiText.SettleUntilAsync(window, () => tab.ChangedFiles.Files.Count == 1, "the changed file");
        var star = icon.Data;

        tab.ChangedFiles.ToggleFileReviewedCommand.Execute(tab.ChangedFiles.Files[0]);
        UiText.Settle(window);

        Assert.True(mark.IsEffectivelyVisible);
        Assert.Equal("All changed files reviewed · Marked with a star", ToolTip.GetTip(mark));
        Assert.Contains("reviewed", icon.Classes);
        Assert.DoesNotContain("star", icon.Classes);
        Assert.NotNull(icon.Data);
        Assert.NotSame(star, icon.Data);
        Assert.Equal(Brush(window, "OkTextBrush"), (icon.Fill as ISolidColorBrush)?.Color);

        // Over no mark too, and in the rail.
        tab.SetMark(null);
        UiText.Settle(window);
        Assert.True(mark.IsEffectivelyVisible);
        h.Shell.Layout.IsSidebarCollapsed = true;
        UiText.Settle(window);
        var railMark = window.GetVisualDescendants().OfType<MarkIcon>().Single(m => m.Name == "RailMark");
        Assert.True(railMark.IsEffectivelyVisible);
        Assert.Contains("reviewed", railMark.Classes);
        Assert.Equal("All changed files reviewed", AutomationProperties.GetName(railMark));

        // Claude changes the file again: the tab's own mark is back.
        tab.SetMark(TabMark.Star);
        EmitEdit(h, "e2", path);
        await UiText.SettleUntilAsync(window, () => !railMark.Classes.Contains("reviewed"), "Claude's change");
        Assert.Contains("star", railMark.Classes);
        Assert.Equal("Marked with a star", AutomationProperties.GetName(railMark));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_tab_menu_lists_the_marks_with_the_tabs_own_ticked_and_Clear_mark_while_it_has_one()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabrow"));
        row.ContextMenu!.Open(row);
        UiText.Settle(window);
        var menu = row.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Mark");
        var clear = row.ContextMenu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Clear mark");
        Assert.False(clear.IsVisible);
        menu.Open();
        UiText.Settle(window);

        var items = Items(menu);
        Assert.Equal(["Check", "Cross", "Question mark", "Star", "Flag", "Pause"], items.Select(Name));
        Assert.All(items, i => Assert.False(i.IsChecked));
        Assert.All(items, i => Assert.True(i.IsEffectivelyEnabled));
        // Each item shows its mark's icon beside its name.
        Assert.Equal(TabMarks.All, items.Select(i => i.GetVisualDescendants().OfType<MarkIcon>().Single().Mark!.Value));
        Assert.All(items, i => Assert.NotNull(i.GetVisualDescendants().OfType<MarkIcon>().Single().Data));

        Click(window, items[2]);

        Assert.Equal(TabMark.Question, tab.Mark);
        Assert.True(clear.IsVisible);
        menu.Open();
        UiText.Settle(window);
        Assert.Equal([false, false, true, false, false, false], Items(menu).Select(i => i.IsChecked));

        // The ticked one takes it off.
        Click(window, Items(menu)[2]);
        Assert.Null(tab.Mark);
        Assert.False(clear.IsVisible);

        Click(window, Items(menu)[0]);
        Assert.Equal(TabMark.Check, tab.Mark);
        clear.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        UiText.Settle(window);
        Assert.Null(tab.Mark);
        row.ContextMenu.Close();
        window.Close();

        static string? Name(MenuItem item) => item.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t));
    }

    [AvaloniaFact]
    public async Task History_shows_a_sessions_mark_before_its_title()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        foreach (var id in new[] { "s1", "s2" })
        {
            h.WriteTranscript(id, new JsonObject
            {
                ["type"] = "user",
                ["sessionId"] = id,
                ["cwd"] = h.WorkFolder,
                ["timestamp"] = "2026-09-01T00:00:00Z",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = $"Prompt for {id}" },
            }.ToJsonString());
        }
        h.Services.State.SessionMarks["s1"] = new SessionMark { Mark = "flag", At = h.Time.GetUtcNow() };
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await UiText.SettleUntilAsync(window, () => !history.IsLoading, "History to load");
        UiText.Settle(window);

        var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "HistoryRows");
        var (marked, markedLine) = Title(list, "Prompt for s1");
        var (_, unmarkedLine) = Title(list, "Prompt for s2");
        var icon = markedLine.Children.OfType<Border>().Single().GetVisualDescendants().OfType<MarkIcon>().Single();
        Assert.True(icon.IsEffectivelyVisible);
        Assert.Equal(TabMark.Flag, icon.Mark);
        Assert.Equal("Marked with a flag", AutomationProperties.GetName(icon.FindAncestorOfType<Border>()!));
        Assert.True(InRow(icon, markedLine).Right <= InRow(marked, markedLine).Left, "The mark isn't before the title.");
        Assert.False(unmarkedLine.Children.OfType<Border>().Single().IsVisible);
        window.Close();

        static (TextBlock Title, DockPanel Line) Title(ItemsControl list, string text)
        {
            var title = list.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == text && t.Parent is DockPanel);
            return (title, (DockPanel)title.Parent!);
        }
    }

    /// <summary>A successful Edit of <paramref name="path"/>, live.</summary>
    private static void EmitEdit(TabTestHarness h, string id, string path)
    {
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = id,
                    ["name"] = "Edit",
                    ["input"] = new JsonObject { ["file_path"] = path, ["old_string"] = "a", ["new_string"] = "b" },
                }),
            },
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = "The file has been updated." }) },
            ["tool_use_result"] = new JsonObject { ["filePath"] = path, ["oldString"] = "a", ["newString"] = "b", ["originalFile"] = "a\n" },
        });
    }

    private static MenuItem[] Items(MenuItem menu) => menu.GetRealizedContainers().OfType<MenuItem>().ToArray();

    private static void Click(Window window, MenuItem item)
    {
        item.SetCurrentValue(MenuItem.IsCheckedProperty, !item.IsChecked);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        UiText.Settle(window);
    }

    private static Color? Brush(Window window, string key) =>
        window.TryFindResource(key, window.ActualThemeVariant, out var brush) ? (brush as ISolidColorBrush)?.Color : null;

    private static Rect InRow(Visual control, Visual row) => new(control.TranslatePoint(default, row)!.Value, control.Bounds.Size);
}
