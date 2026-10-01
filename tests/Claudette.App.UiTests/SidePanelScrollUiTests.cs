using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Controls;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>
/// Every page of the side panel scrolls both ways (DESIGN.md §3, "Side panel"): a row's name and numbers stay in view,
/// long lines (a path, a command line, a log line) are shown whole and scroll sideways, and text wraps at the width shown.
/// </summary>
public class SidePanelScrollUiTests
{
    private static readonly string LongName = "very_long_" + string.Concat(Enumerable.Repeat("unbreakable_", 30));

    [AvaloniaFact]
    public void A_rows_summary_stays_in_view_while_its_long_text_scrolls_sideways()
    {
        var stats = new TextBlock { Text = "+12 −3" };
        var summary = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { new TextBlock { Text = "Program.cs" } } };
        Grid.SetColumn(stats, 1);
        summary.Children.Add(stats);
        var viewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Margin = new Thickness(8, 0), Children = { new ShownWidth { Child = summary }, new TextBlock { Text = LongName } } },
        };
        var window = UiText.Show(viewer, 300, 200);

        Assert.True(viewer.Extent.Width > viewer.Viewport.Width + 100, "the long line scrolls");
        Assert.InRange(RightEdge(stats, viewer), viewer.Viewport.Width - 20, viewer.Viewport.Width);

        // Scrolled sideways, the summary keeps its width.
        viewer.Offset = new Vector(150, 0);
        UiText.Settle(window);
        Assert.Equal(viewer.Viewport.Width - 8, summary.Bounds.Width, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_project_runs_long_log_lines_scroll_sideways()
    {
        var launcher = new FakeLauncher();
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher(), launcher: launcher);
        UnrealFixture.Write(h.Root, h.WorkFolder);
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        await UiText.SettleUntilAsync(window, () => tab.ProjectTools.Project is not null, "the project");
        await tab.ProjectTools.RunActionCommand.ExecuteAsync(tab.ProjectTools.Actions.Single(a => a.Id == "build-editor"));
        var run = tab.ProjectTools.Runs.Single();
        run.OpenCommand.Execute(null);
        launcher.Processes.Last().WriteOutput($"error C2065: {LongName}: undeclared identifier");
        await UiText.SettleUntilAsync(window, () => run.Output.Count > 0, "the output");
        UiText.Settle(window);

        var viewer = Viewer(window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ProjectOutputList"));
        Assert.True(viewer.Extent.Width > viewer.Viewport.Width + 100, "the log line scrolls sideways");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_changed_files_path_scrolls_sideways_while_its_name_and_counts_stay_in_view()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
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
        Assert.True(viewer.Extent.Width > viewer.Viewport.Width + 100, "the path scrolls sideways");
        Assert.InRange(RightEdge(counts, viewer), 0, viewer.Viewport.Width);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_processes_command_line_scrolls_sideways_while_its_numbers_stay_in_view()
    {
        await using var h = new TabTestHarness(s => s.Processes.ShowMonitor = true, dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        h.Trees.Trees[4242].Children.Add((5001, LongName));
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        tab.IsSidePanelOpen = true;
        tab.OpenSidePanelPage(SidePanelPage.Processes);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        var viewer = window.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "ProcessesScroller");
        await UiText.SettleUntilAsync(window, () => viewer.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == $"{LongName} --serve"), "the process");
        UiText.Settle(window);

        var memory = viewer.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "50 MB");
        Assert.True(viewer.Extent.Width > viewer.Viewport.Width + 100, "the command line scrolls sideways");
        Assert.InRange(RightEdge(memory, viewer), viewer.Viewport.Width - 40, viewer.Viewport.Width);
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
        Assert.Equal(ScrollBarVisibility.Auto, viewer.HorizontalScrollBarVisibility);
        Assert.Equal(viewer.Viewport.Width, viewer.Extent.Width, 1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Every_page_scrolls_sideways_when_it_has_to()
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

        // Every page's scroll viewer and list, shown or not: none is left unable to scroll sideways.
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
            Assert.True(sideways != ScrollBarVisibility.Disabled, $"{control.GetType().Name} {control.Name} can't scroll sideways");
        }
        window.Close();
    }

    private static ScrollViewer Viewer(Control list) => list.GetVisualDescendants().OfType<ScrollViewer>().First();

    private static double RightEdge(Control control, Visual to) => control.TranslatePoint(new Point(control.Bounds.Width, 0), to)!.Value.X;

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
