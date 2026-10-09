using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>
/// How far Claude's plan and tasks have got (DESIGN.md §5, "Tasks"): the tab row's badge, the status line, the rows
/// where tasks start and where a turn left them, and the plan's versions on the Tasks page and its card.
/// </summary>
public class TaskProgressUiTests
{
    private static List<string?> Texts(Control root) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Concat(t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []))];

    private const string Init = """{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default"}""";

    private static void Tool(TabTestHarness h, string id, string name, JsonObject input) => h.Transport.Emit(new JsonObject
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input }) },
    });

    private static void Result(TabTestHarness h, string id, JsonObject? result = null, bool isError = false, string text = "ok") => h.Transport.Emit(new JsonObject
    {
        ["type"] = "user",
        ["tool_use_result"] = result,
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = text, ["is_error"] = isError }),
        },
    });

    private static void Create(TabTestHarness h, string toolUseId, string id, string subject)
    {
        Tool(h, toolUseId, "TaskCreate", new JsonObject { ["subject"] = subject, ["activeForm"] = $"Doing {subject}" });
        Result(h, toolUseId, new JsonObject { ["task"] = new JsonObject { ["id"] = id, ["subject"] = subject } });
    }

    private static Rect InRow(Control control, Visual row) => new(control.TranslatePoint(default, row)!.Value, control.Bounds.Size);

    [AvaloniaFact]
    public async Task The_row_the_status_line_and_the_conversation_say_how_far_the_tasks_have_got()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1200, 800);
        var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabrow"));
        var badge = row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TaskProgressBadge");
        var height = row.Bounds.Height;
        Assert.False(badge.IsEffectivelyVisible);

        h.Transport.Emit(Init);
        Create(h, "c1", "1", "Fix the config");
        Create(h, "c2", "2", "Write the docs");
        Tool(h, "u1", "TaskUpdate", new JsonObject { ["taskId"] = "1", ["status"] = "in_progress" });
        await UiText.SettleUntilAsync(window, () => badge.IsEffectivelyVisible && tab.RowDetail == "Doing Fix the config", "the badge");

        // On the row's second line, after the task it's working on, without making the row taller.
        var detail = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Doing Fix the config");
        Assert.Contains("0/2", Texts(badge));
        Assert.Equal("0 of 2 tasks done · Now: Doing Fix the config", AutomationProperties.GetName(badge));
        Assert.True(InRow(detail, row).Right <= InRow(badge, row).Left, "The task on the row runs into the badge.");
        Assert.Equal(height, row.Bounds.Height, precision: 3);

        // The status line, at its right.
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        var status = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "TaskStatus");
        Assert.True(status.IsEffectivelyVisible);
        Assert.Contains("0 of 2 tasks · Doing Fix the config", Texts(status));

        // Where the task started, and where the turn left the list, with Continue.
        Tool(h, "u2", "TaskUpdate", new JsonObject { ["taskId"] = "1", ["status"] = "completed" });
        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"s1","duration_ms":1000}""");
        await UiText.SettleUntilAsync(window, () => Texts(view).Contains("1 of 2 tasks left · Next: #2 Write the docs"), "the row at the turn's end");
        var texts = Texts(view);
        Assert.Contains("#1 Fix the config", texts);
        Assert.Contains("1 of 2 tasks", Texts(status));
        var resume = view.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is TasksSummaryItem && Texts(b).Contains("Continue"));
        Assert.True(resume.IsEffectivelyVisible);
        // The model is back on the row once the turn is over.
        await UiText.SettleUntilAsync(window, () => row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == tab.ModelBadge), "the model on the row");
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_tasks_page_steps_through_the_plans_versions_and_shows_what_changed()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1200, 800);
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"plan"}""");

        void Propose(string requestId, string toolUseId, string plan)
        {
            Tool(h, toolUseId, "ExitPlanMode", new JsonObject());
            h.Transport.Emit(new JsonObject
            {
                ["type"] = "control_request",
                ["request_id"] = requestId,
                ["request"] = new JsonObject
                {
                    ["subtype"] = "can_use_tool", ["tool_name"] = "ExitPlanMode", ["tool_use_id"] = toolUseId,
                    ["input"] = new JsonObject { ["plan"] = plan, ["planFilePath"] = "plan.md" },
                },
            });
        }

        Propose("p1", "t1", "1. Read the code\n2. Rewrite the API");
        await UiText.SettleUntilAsync(window, () => tab.Items.OfType<PlanItem>().Any(), "the first plan");
        var first = tab.Items.OfType<PlanItem>().Single();
        first.StartFeedbackCommand.Execute(null);
        first.Feedback = "keep the old API";
        first.KeepPlanningCommand.Execute(null);
        Result(h, "t1", isError: true, text: "The user wants to keep planning and said: keep the old API");
        Propose("p2", "t2", "1. Read the code\n2. Extend the API");
        await UiText.SettleUntilAsync(window, () => tab.Items.OfType<PlanItem>().Count() == 2 && Texts(view).Contains("Claude's plan · v2"), "the second plan's card");

        tab.IsSidePanelOpen = true;
        tab.ShowTasksPageCommand.Execute(null);
        await UiText.SettleUntilAsync(window, () => Texts(view).Contains("v2 of 2"), "the versions on the page");
        Assert.Contains("Waiting for review", Texts(view));

        // What changed from v1, line by line, on the page.
        var panel = view.GetVisualDescendants().OfType<SidePanelView>().Single();
        var changes = panel.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.IsEffectivelyVisible && Texts(b).Contains("Changes from v1"));
        changes.IsChecked = true;
        await UiText.SettleUntilAsync(window, () => Texts(panel).Contains("2. Extend the API") && Texts(panel).Contains("2. Rewrite the API"), "the changes");

        // Back to v1: sent back, with what the user said.
        var earlier = panel.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Earlier version of the plan");
        earlier.Command!.Execute(null);
        await UiText.SettleUntilAsync(window, () => Texts(panel).Contains("v1 of 2"), "the first version");
        var texts = Texts(panel);
        Assert.Contains("“keep the old API”", texts);
        Assert.Contains(texts, t => t?.StartsWith("Sent back ", StringComparison.Ordinal) == true);
        window.Close();
    }
}
