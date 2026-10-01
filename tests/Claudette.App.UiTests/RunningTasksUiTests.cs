using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Views;
using Claudette.Core.Settings;
using Path = Avalonia.Controls.Shapes.Path;

namespace Claudette.App.UiTests;

/// <summary>
/// Running tasks rendered (DESIGN.md §5, "Running tasks"): the chip in the composer bar and its list, and the count on
/// the tab's row once the turn is over (DESIGN.md §4, "Sidebar"), in both styles and densities.
/// </summary>
public class RunningTasksUiTests
{
    [AvaloniaFact]
    public async Task The_chip_lists_the_running_tasks_with_their_times_and_Stop()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var chip = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TasksChip");
        Assert.False(chip.IsEffectivelyVisible);

        StartThreeTasks(h);
        await UiText.SettleUntilAsync(window, () => tab.Tasks.Count == 3, "the tasks");
        h.Time.Advance(TimeSpan.FromSeconds(65));

        Assert.True(chip.IsEffectivelyVisible);
        Assert.Equal("3 running tasks", AutomationProperties.GetName(chip));
        Assert.Contains("\"3 running tasks\"", UiText.Describe(chip), StringComparison.Ordinal);

        var flyout = Assert.IsType<Flyout>(chip.Flyout);
        flyout.ShowAt(chip);
        UiText.Settle(window);
        var list = Assert.IsAssignableFrom<Control>(flyout.Content);
        await UiText.SettleUntilAsync(window, () => list.IsEffectivelyVisible && list.GetVisualDescendants().OfType<Path>().Count() == 3, "the list");
        Assert.True(tab.IsTaskListOpen);
        var shown = UiText.Describe(list);
        // Each row has its kind's icon, drawn like the tool cards'.
        var icons = list.GetVisualDescendants().OfType<Path>().ToList();
        Assert.All(icons, icon => Assert.Equal(12, icon.Width));
        Assert.All(icons, icon => Assert.NotNull(icon.Data));
        var rows = list.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("taskrow")).ToList();
        Assert.Equal(["task-1", "task-m", "task-bg"], rows.Select(r => ((RunningTask)r.DataContext!).TaskId));
        Assert.Equal("Shell command: Start the dev server\nnpm run dev\nClick to show it in the conversation.", ToolTip.GetTip(rows[0]));

        // The running time ticks while the list is open.
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await UiText.SettleUntilAsync(window, () => UiText.Describe(list).Contains("\"1m 07s\"", StringComparison.Ordinal), "a tick");

        // Stop asks first, then goes through Claude Code.
        var stop = list.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Stop");
        Assert.Same(tab.StopTaskCommand, stop.Command);
        Assert.Same(tab.Tasks.Running[0], stop.CommandParameter);
        stop.Command!.Execute(stop.CommandParameter);
        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UiText.Settle(window);
        Assert.False(flyout.IsOpen);
        Assert.False(tab.IsTaskListOpen);
        Assert.Equal("Stop \"Start the dev server\"?", h.Shell.Confirmation!.Title);
        await h.Shell.Confirmation.ConfirmCommand.ExecuteAsync(null);
        await UiText.SettleUntilAsync(window, () => h.Transport.SentControlSubtypes.Contains("stop_task"), "stop_task");

        // A row goes to its card, which opens up to show its command.
        flyout.ShowAt(chip);
        UiText.Settle(window);
        var row = list.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("taskrow"));
        row.Command!.Execute(row.CommandParameter);
        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        UiText.Settle(window);
        Assert.False(flyout.IsOpen);
        Assert.True(tab.Items.OfType<ToolUseItem>().Single(t => t.ToolUseId == "b1").IsExpanded);

        // The last one ending takes the chip away.
        foreach (var id in new[] { "task-1", "task-m", "task-bg" })
        {
            h.Transport.Emit(Updated(id, "completed"));
        }
        await UiText.SettleUntilAsync(window, () => tab.Tasks.Count == 0, "the tasks to end");
        Assert.False(chip.IsEffectivelyVisible);

        // Verify resumes off the UI thread, so it comes last.
        await Verify(shown);
    }

    [AvaloniaFact]
    public async Task The_tabs_row_counts_the_tasks_once_the_turn_is_over_in_both_styles_and_densities()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });
        var badge = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "TaskBadge");
        var row = badge.FindAncestorOfType<Button>()!;
        Assert.Contains("tabrow", row.Classes);
        Assert.Same(tab, row.DataContext);
        var height = row.Bounds.Height;
        Assert.False(badge.IsEffectivelyVisible);

        tab.ComposerText = "start everything";
        await tab.SendCommand.ExecuteAsync(null);
        StartThreeTasks(h);
        await UiText.SettleUntilAsync(window, () => tab.Tasks.Count == 3 && tab.IsWorking, "the tasks during the turn");
        // While Claude works, the status icon already says so.
        Assert.False(badge.IsEffectivelyVisible);

        h.Transport.Emit(TurnResult);
        await UiText.SettleUntilAsync(window, () => !tab.IsWorking, "the end of the turn");

        Assert.True(badge.IsEffectivelyVisible);
        Assert.Equal("3 tasks still running", ToolTip.GetTip(badge));
        Assert.Equal("3 tasks still running", AutomationProperties.GetName(badge));
        Assert.Equal("\"3\"", UiText.Describe(badge).Trim());
        // No taller than the name beside it, so the row keeps its height; and a small mark, like the row's icons.
        Assert.Equal(height, row.Bounds.Height, precision: 3);
        Assert.True(badge.Bounds.Width <= 20, $"The badge is {badge.Bounds.Width} px wide.");
        Assert.Equal(new CornerRadius(4), badge.CornerRadius);

        h.Services.Settings.Appearance.Density = Density.Compact;
        h.Services.SaveSettings();
        UiText.Settle(window);
        Assert.True(badge.IsEffectivelyVisible);
        Assert.Equal(height - 6, row.Bounds.Height, precision: 3);

        h.Services.Settings.Appearance.Style = AppStyle.Claude;
        h.Services.SaveSettings();
        UiText.Settle(window);
        Assert.True(badge.IsEffectivelyVisible);
        Assert.Equal(new CornerRadius(8), badge.CornerRadius);
        Assert.Equal(height - 6, row.Bounds.Height, precision: 3);

        // The list's rows follow the density and the style too.
        var chip = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TasksChip");
        var flyout = Assert.IsType<Flyout>(chip.Flyout);
        flyout.ShowAt(chip);
        UiText.Settle(window);
        var list = Assert.IsAssignableFrom<Control>(flyout.Content);
        await UiText.SettleUntilAsync(window, () => list.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("taskrow")), "the list");
        var taskRow = list.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("taskrow"));
        Assert.Equal(new Thickness(6, 2), taskRow.Padding);
        Assert.Equal(new CornerRadius(8), taskRow.CornerRadius);
        flyout.Hide();

        h.Services.Settings.Appearance.Density = Density.Comfortable;
        h.Services.Settings.Appearance.Style = AppStyle.Standard;
        h.Services.SaveSettings();
        UiText.Settle(window);
        Assert.Equal(height, row.Bounds.Height, precision: 3);
        window.Close();
    }

    private const string TurnResult = """{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"s1","duration_ms":1000}""";

    /// <summary>A command in the background, a Monitor watch and a background subagent.</summary>
    private static void StartThreeTasks(TabTestHarness h)
    {
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default"}""");
        h.Transport.Emit(ToolUse("b1", "Bash", new JsonObject { ["command"] = "npm run dev", ["description"] = "Start the dev server", ["run_in_background"] = true }));
        h.Transport.Emit(TaskStarted("task-1", "b1", "local_bash", "Start the dev server", backgrounded: true));
        h.Transport.Emit(ToolUse("m1", "Monitor", new JsonObject { ["description"] = "Watch the build log", ["command"] = "tail -f build.log", ["timeout_ms"] = 300000 }));
        h.Transport.Emit(TaskStarted("task-m", "m1", "local_bash", "Watch the build log", backgrounded: true));
        h.Transport.Emit(ToolUse("bg", "Agent", new JsonObject
        {
            ["description"] = "Review the auth code", ["prompt"] = "Look for token checks.", ["subagent_type"] = "Explore", ["run_in_background"] = true,
        }));
        h.Transport.Emit(TaskStarted("task-bg", "bg", "local_agent", "Review the auth code", backgrounded: true));
    }

    private static string ToolUse(string id, string name, JsonObject input) => new JsonObject
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input }),
        },
    }.ToJsonString();

    private static string Updated(string taskId, string status) => new JsonObject
    {
        ["type"] = "system", ["subtype"] = "task_updated", ["task_id"] = taskId, ["patch"] = new JsonObject { ["status"] = status },
    }.ToJsonString();

    private static string TaskStarted(string taskId, string toolUseId, string type, string description, bool backgrounded) => new JsonObject
    {
        ["type"] = "system", ["subtype"] = "task_started", ["task_id"] = taskId, ["tool_use_id"] = toolUseId, ["description"] = description,
        ["task_type"] = type, ["is_backgrounded"] = backgrounded, ["session_id"] = "s1",
    }.ToJsonString();
}
