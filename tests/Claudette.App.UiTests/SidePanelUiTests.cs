using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Claudette.App.Diffs;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>
/// The side panel rendered (DESIGN.md §3): its pages as tabs, dragging its edge to resize it, and ticking changed files as
/// reviewed (DESIGN.md §8).
/// </summary>
public class SidePanelUiTests
{
    [AvaloniaFact]
    public async Task The_page_showing_has_the_accent_line_and_the_others_are_muted()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
        UiText.Settle(window);
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        var pages = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("pagetab") && b.IsEffectivelyVisible).ToList();
        var (files, agents) = (pages[0], pages[1]);

        Assert.Contains("selected", files.Classes);
        Assert.Equal(Accent(view), LineUnder(files));
        Assert.Equal(Colors.Transparent, LineUnder(agents));
        Assert.Contains("muted", Label(agents).Classes);

        tab.ShowAgentsPageCommand.Execute(null);
        UiText.Settle(window);

        Assert.Equal(Accent(view), LineUnder(agents));
        Assert.Equal(Colors.Transparent, LineUnder(files));
        Assert.Contains("muted", Label(files).Classes);
        Assert.DoesNotContain("muted", Label(agents).Classes);
    }

    [AvaloniaFact]
    public async Task Dragging_the_edge_resizes_the_panel_and_leaves_the_conversation_room()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
        UiText.Settle(window);
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        var panel = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SidePanel");
        var edge = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SidePanelEdge");
        Assert.Equal(ShellLayout.DefaultSidePanelWidth, panel.Bounds.Width);

        // Left widens it; the width is kept when the drag ends.
        Drag(window, edge, -100);
        Assert.Equal(ShellLayout.DefaultSidePanelWidth + 100, panel.Bounds.Width);
        Assert.Equal(ShellLayout.DefaultSidePanelWidth + 100, h.Services.State.SidePanelWidth);

        // However far it's dragged, the conversation keeps 360.
        Drag(window, edge, -2000);
        Assert.Equal(view.Bounds.Width - 360, panel.Bounds.Width);
        Assert.Equal(view.Bounds.Width - 360, tab.SidePanelWidth);

        // Right narrows it, down to its least.
        Drag(window, edge, 2000);
        Assert.Equal(ShellLayout.MinSidePanelWidth, panel.Bounds.Width);
    }

    [AvaloniaFact]
    public async Task A_changed_files_box_ticks_it_as_reviewed_without_opening_the_diff()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
        var (first, second) = (Path.Combine(h.WorkFolder, "auth.cs"), Path.Combine(h.WorkFolder, "login.cs"));
        await File.WriteAllTextAsync(first, "b\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "b\n", TestContext.Current.CancellationToken);
        h.Transport.Emit(Edit("e1", first));
        h.Transport.Emit(EditResult("e1", first));
        h.Transport.Emit(Edit("e2", second));
        h.Transport.Emit(EditResult("e2", second));
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ChangedFilesList");
        await UiText.SettleUntilAsync(window, () => Boxes(list).Count == 2, "the changed files");
        var opened = 0;
        tab.ChangedFiles.DiffRequested += _ => opened++;

        Click(window, Boxes(list)[0]);

        Assert.Equal(0, opened);
        Assert.Null(list.SelectedItem);
        Assert.True(tab.ChangedFiles.Files[0].IsReviewed);
        Assert.Equal([true, false], Boxes(list).Select(b => b.IsChecked == true));
        Assert.Equal("2 files changed · 1 reviewed", tab.ChangedFiles.Summary);
        // A reviewed file is drawn faintly; its box isn't.
        var rows = list.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("changedfile")).ToList();
        Assert.Equal([0.5, 1.0], rows.Select(r => r.Children.OfType<StackPanel>().Single().Opacity));
        Assert.Equal(1.0, Boxes(list)[0].Opacity);

        // Unticked by a click, then ticked from the view model, the box follows.
        Click(window, Boxes(list)[0]);
        Assert.False(tab.ChangedFiles.Files[0].IsReviewed);
        tab.ChangedFiles.ToggleFileReviewedCommand.Execute(tab.ChangedFiles.Files[0]);
        UiText.Settle(window);
        Assert.True(Boxes(list)[0].IsChecked);

        // Claude changing the file again unticks it.
        var panel = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SidePanel");
        var shown = UiText.Describe(panel);
        h.Transport.Emit(Edit("e3", first));
        h.Transport.Emit(EditResult("e3", first));
        await UiText.SettleUntilAsync(window, () => tab.ChangedFiles.Files is [{ LatestChange: "e3" }, _], "Claude's next change");
        Assert.Equal([false, false], Boxes(list).Select(b => b.IsChecked == true));

        await Verify(shown);
    }

    [AvaloniaFact]
    public async Task The_arrow_keys_move_through_changed_files_without_opening_them()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
        var (first, second) = (Path.Combine(h.WorkFolder, "auth.cs"), Path.Combine(h.WorkFolder, "login.cs"));
        await File.WriteAllTextAsync(first, "b\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(second, "b\n", TestContext.Current.CancellationToken);
        h.Transport.Emit(Edit("e1", first));
        h.Transport.Emit(EditResult("e1", first));
        h.Transport.Emit(Edit("e2", second));
        h.Transport.Emit(EditResult("e2", second));
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ChangedFilesList");
        await UiText.SettleUntilAsync(window, () => Boxes(list).Count == 2, "the changed files");
        var opened = new List<string>();
        tab.ChangedFiles.DiffRequested += source => opened.Add(Path.GetFileName(source.Path));

        list.SelectedIndex = 0;
        list.ContainerFromIndex(0)!.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        UiText.Settle(window);

        Assert.Equal(1, list.SelectedIndex);
        Assert.Empty(opened);

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        UiText.Settle(window);
        Assert.Equal(["login.cs"], opened);

        // A click opens a file, the same one again too.
        var name = list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "auth.cs");
        Click(window, name);
        Click(window, name);
        Assert.Equal(["login.cs", "auth.cs", "auth.cs"], opened);
    }

    [AvaloniaFact]
    public async Task Reviewed_in_the_diff_window_ticks_the_file_and_closes_the_window()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"claudette-diff-{Guid.NewGuid():N}");
        var file = Path.Combine(folder, "auth.cs");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(file, "one\ntwo\n", TestContext.Current.CancellationToken);
        try
        {
            string? marked = "none";
            var source = new DiffSource(file, "src/auth.cs", "one\n", "compared with before Claude's first change in this session",
                OpenInDiffTool: null, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
                Review: new DiffReview(() => "e1", () => false, change => marked = change));
            var window = new DiffWindow { DataContext = new DiffWindowViewModel(source, dark: false) };
            window.Show();
            var reviewed = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ReviewedButton");
            await UiText.SettleUntilAsync(window, () => ((DiffWindowViewModel)window.DataContext!).IsLoading == false, "the diff");
            var header = reviewed.FindAncestorOfType<Border>()!;
            var shown = UiText.Describe(header);
            var closed = false;
            window.Closed += (_, _) => closed = true;

            reviewed.Command!.Execute(null);
            UiText.Settle(window);

            Assert.Equal("e1", marked);
            Assert.True(closed);
            await Verify(shown);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static List<CheckBox> Boxes(ListBox list) =>
        list.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Classes.Contains("reviewed")).ToList();

    private static void Click(Window window, Control control)
    {
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        UiText.Settle(window);
    }

    private static JsonObject Edit(string id, string path) => new()
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "tool_use", ["id"] = id, ["name"] = "Edit", ["input"] = new JsonObject { ["file_path"] = path, ["old_string"] = "a", ["new_string"] = "b" },
            }),
        },
    };

    private static JsonObject EditResult(string id, string path) => new()
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = "The file has been updated." }) },
        ["tool_use_result"] = new JsonObject { ["filePath"] = path, ["oldString"] = "a", ["newString"] = "b", ["originalFile"] = "a\n" },
    };

    private static void Drag(Window window, Control edge, double by)
    {
        var from = edge.TranslatePoint(new Point(edge.Bounds.Width / 2, edge.Bounds.Height / 2), window)!.Value;
        var to = from.WithX(from.X + by);
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        UiText.Settle(window);
    }

    private static Color Accent(Control view) =>
        view.TryFindResource("AccentStatusBrush", view.ActualThemeVariant, out var brush) && brush is ISolidColorBrush solid ? solid.Color : default;

    private static TextBlock Label(Button page) => page.GetVisualDescendants().OfType<TextBlock>().First();

    /// <summary>The line under a page's tab: its template's border.</summary>
    private static Color LineUnder(Button page) =>
        page.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter").BorderBrush is ISolidColorBrush solid
            ? solid.Color
            : Colors.Transparent;
}
