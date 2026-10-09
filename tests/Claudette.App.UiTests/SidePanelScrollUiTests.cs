using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Claudette.App.Controls;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>
/// Every page of the side panel wraps its text at the width shown, clear of the scroll bar (DESIGN.md §3, "Side panel"):
/// a path, a process's name and a log line wrap, and a row's numbers line up at its right edge. Only a tree's row nested
/// past the edge scrolls sideways.
/// </summary>
public class SidePanelScrollUiTests
{
    private static readonly string LongName = "very_long_" + string.Concat(Enumerable.Repeat("unbreakable_", 30));

    [AvaloniaFact]
    public void A_tree_rows_text_wraps_clear_of_the_scroll_bar()
    {
        var name = new TextBlock { Text = LongName, TextWrapping = TextWrapping.Wrap };
        var stats = new TextBlock { Text = "50 MB" };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { name, stats } };
        Grid.SetColumn(stats, 1);
        // Rows enough to scroll up and down, so there's a scroll bar to keep clear of.
        var rows = new StackPanel { Margin = new Thickness(8, 0, 0, 0), Children = { new ShownWidth { Child = row } } };
        rows.Children.AddRange(Enumerable.Range(0, 40).Select(i => new TextBlock { Text = $"row {i}" }));
        var viewer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows };
        var window = UiText.Show(viewer, 300, 200);

        Assert.Equal(viewer.Viewport.Width, viewer.Extent.Width, 1);
        Assert.True(name.Bounds.Height > name.FontSize * 3, "the long name wraps");
        var bar = viewer.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Orientation.Vertical);
        Assert.True(bar.IsVisible, "the scroll bar shows");
        Assert.InRange(RightEdge(stats, viewer), LeftEdge(bar, viewer) - 6, LeftEdge(bar, viewer));
        window.Close();
    }

    [AvaloniaFact]
    public void Only_a_tree_row_nested_past_the_edge_scrolls_sideways()
    {
        var deep = new ShownWidth { Margin = new Thickness(250, 0, 0, 0), Child = new TextBlock { Text = LongName, TextWrapping = TextWrapping.Wrap } };
        var viewer = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new StackPanel { Children = { deep } } };
        var window = UiText.Show(viewer, 300, 200);

        // Its text wraps at the least width, and the row goes past the edge.
        var text = deep.Child!;
        Assert.InRange(text.Bounds.Width, deep.Minimum - 20, deep.Minimum);
        Assert.True(viewer.Extent.Width > viewer.Viewport.Width + 50, "the row scrolls sideways");

        // Scrolled sideways, it keeps its width.
        var width = text.Bounds.Width;
        viewer.Offset = new Vector(70, 0);
        UiText.Settle(window);
        Assert.Equal(width, text.Bounds.Width, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_project_runs_long_log_lines_wrap()
    {
        var launcher = new FakeLauncher();
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher(), launcher: launcher);
        UnrealFixture.Write(h.Root, h.WorkFolder);
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        await UiText.SettleUntilAsync(window, () => tab.ProjectTools.Project is not null, "the project");
        await tab.ProjectTools.RunActionCommand.ExecuteAsync(tab.ProjectTools.Actions.Single(a => a.Id == "build-editor"));
        var run = tab.ProjectTools.Runs.Items.Single();
        run.OpenCommand.Execute(null);
        launcher.Processes.Last().WriteOutput($"error C2065: {LongName}: undeclared identifier");
        await UiText.SettleUntilAsync(window, () => run.Output.Count > 0, "the output");
        UiText.Settle(window);

        var output = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ProjectOutputList");
        var viewer = Viewer(output);
        var line = output.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.Contains(LongName, StringComparison.Ordinal) == true);
        Assert.Equal(viewer.Viewport.Width, viewer.Extent.Width, 1);
        Assert.True(line.Bounds.Height > line.FontSize * 3, "the log line wraps");
        Assert.InRange(RightEdge(line, viewer), viewer.Viewport.Width - 24, viewer.Viewport.Width - 16);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_changed_files_path_wraps_while_its_counts_stay_at_the_right_edge()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
        // The panel's pages are only in the visual tree once it has been laid out open.
        UiText.Settle(window);
        var folder = Directory.CreateDirectory(Path.Combine([h.WorkFolder, .. Enumerable.Repeat("a_folder_with_a_long_name_that_goes_on", 6), "src"])).FullName;
        var file = Path.Combine(folder, "auth.cs");
        await File.WriteAllTextAsync(file, "b\n", TestContext.Current.CancellationToken);
        h.Transport.Emit(Edit("e1", file));
        h.Transport.Emit(EditResult("e1", file));
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ChangedFilesList");
        await UiText.SettleUntilAsync(window, () => list.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "+1 −1"), "the changed file");
        UiText.Settle(window);

        var viewer = Viewer(list);
        var counts = list.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "+1 −1");
        var path = list.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text?.Contains("a_folder_with_a_long_name", StringComparison.Ordinal) == true);
        Assert.Equal(viewer.Viewport.Width, viewer.Extent.Width, 1);
        Assert.True(path.Bounds.Height > path.FontSize * 3, "the path wraps");
        Assert.InRange(RightEdge(counts, viewer), viewer.Viewport.Width - 24, viewer.Viewport.Width - 16);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_processes_name_wraps_its_numbers_stay_clear_of_the_scroll_bar_and_its_arguments_stay_on_one_line()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true, dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        h.Trees.Trees[4242].Children.Add((5001, LongName));
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
        tab.OpenSidePanelPage(SidePanelPage.Processes);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        // The panel's pages are only in the visual tree once it has been laid out open.
        UiText.Settle(window);
        var viewer = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "ProcessesScroller");
        TextBlock? Name() => viewer.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(t => t.Inlines?.OfType<Run>().Any(r => r.Text == LongName) == true);
        await UiText.SettleUntilAsync(window, () => Name() is not null, "the process");
        UiText.Settle(window);

        var memory = viewer.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "50 MB");
        var name = Name()!;
        Assert.Equal(viewer.Viewport.Width, viewer.Extent.Width, 1);
        Assert.True(name.Bounds.Height > name.FontSize * 3, "the name wraps");
        Assert.InRange(RightEdge(memory, viewer), viewer.Viewport.Width - 24, viewer.Viewport.Width - 16);

        // Its arguments, cut short, end its second line; the whole command line is in its tooltip.
        var arguments = viewer.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "--serve");
        Assert.True(arguments.Bounds.Height < arguments.FontSize * 2, "the arguments stay on one line");
        Assert.True(RightEdge(arguments, viewer) <= viewer.Viewport.Width, $"The arguments end at {RightEdge(arguments, viewer)}, past {viewer.Viewport.Width}");
        var row = name.GetVisualAncestors().OfType<Border>().First(b => ToolTip.GetTip(b) is not null);
        Assert.Contains($"{LongName} --serve", ToolTip.GetTip(row) as string, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Tasks_wrap_at_the_width_shown()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "tool_use", ["id"] = "t1", ["name"] = "TodoWrite",
                    ["input"] = new JsonObject { ["todos"] = new JsonArray(new JsonObject { ["content"] = string.Join(' ', Enumerable.Repeat("a task that wraps", 30)), ["status"] = "pending", ["activeForm"] = "Doing it" }) },
                }),
            },
        });
        await UiText.SettleUntilAsync(window, () => tab.TodoList.HasItems, "the task");
        tab.OpenSidePanelPage(SidePanelPage.Tasks);
        UiText.Settle(window);

        var viewer = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "TasksScroller");
        Assert.Equal(viewer.Viewport.Width, viewer.Extent.Width, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Only_the_trees_scroll_sideways()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
        // The agent map's views are built once its page shows.
        tab.ShowAgentsPageCommand.Execute(null);
        UiText.Settle(window);
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        var panel = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SidePanel");

        // Every page's scroll viewer and list, shown or not. One that scrolls sideways gives its text all the width it
        // likes, so the text never wraps; only the trees do, for rows nested past the edge.
        var scrolling = panel.GetVisualDescendants().OfType<Control>()
            .Where(c => c is ScrollViewer or ListBox or TreeView && c.TemplatedParent is null)
            .ToList();
        // The selected agent's details are built once there's an agent: on their own here.
        var details = new AgentDetails { DataContext = tab };
        var detailsWindow = UiText.Show(details, 300, 300);
        scrolling.AddRange(details.GetVisualDescendants().OfType<ScrollViewer>().Where(v => v.TemplatedParent is null));
        detailsWindow.Close();
        // Changed files, Agents (tree and details), Project, Tasks, MCP and Processes.
        Assert.True(scrolling.Count >= 7, $"only {scrolling.Count} scrolling controls found");
        foreach (var control in scrolling)
        {
            var sideways = control is ScrollViewer viewer ? viewer.HorizontalScrollBarVisibility : ScrollViewer.GetHorizontalScrollBarVisibility(control);
            var tree = control is TreeView || control.Name == "ProcessesScroller";
            Assert.True(sideways == (tree ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled), $"{control.GetType().Name} {control.Name} scrolls sideways: {sideways}");
        }
        window.Close();
    }

    private static ScrollViewer Viewer(Control list) => list.GetVisualDescendants().OfType<ScrollViewer>().First();

    private static double RightEdge(Control control, Visual to) => control.TranslatePoint(new Point(control.Bounds.Width, 0), to)!.Value.X;

    private static double LeftEdge(Control control, Visual to) => control.TranslatePoint(default, to)!.Value.X;

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
}
