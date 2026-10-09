using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>The side panel's Tasks page (DESIGN.md §5, "Tasks") and MCP page (DESIGN.md §4, "MCP servers").</summary>
public class TasksAndMcpPagesUiTests
{
    private static List<string?> Texts(Control root) =>
        [.. root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Concat(t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []))];

    [AvaloniaFact]
    public async Task The_tasks_page_shows_each_task_with_what_it_waits_on_and_the_badge_counts_them()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1200, 800);
        void Tool(string id, string name, JsonObject input) => h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input }) },
        });
        void Result(string id, JsonObject result) => h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["tool_use_result"] = result,
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = "ok" }) },
        });

        Tool("t1", "TaskCreate", new JsonObject { ["subject"] = "Write the migration", ["description"] = "Add the users.email index." });
        Result("t1", new JsonObject { ["task"] = new JsonObject { ["id"] = "1", ["subject"] = "Write the migration" } });
        Tool("t2", "TaskCreate", new JsonObject { ["subject"] = "Run the migration" });
        Result("t2", new JsonObject { ["task"] = new JsonObject { ["id"] = "2", ["subject"] = "Run the migration" } });
        Tool("t3", "TaskUpdate", new JsonObject { ["taskId"] = "2", ["addBlockedBy"] = new JsonArray("1") });
        await UiText.SettleUntilAsync(window, () => tab.TodoList.Items.Count == 2 && tab.TodoList.Items[1].IsBlocked, "the tasks");

        tab.IsSidePanelOpen = true;
        tab.ShowTasksPageCommand.Execute(null);
        UiText.Settle(window);
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        var texts = Texts(view);

        Assert.Contains("0 of 2", texts);
        Assert.Contains("Add the users.email index.", texts);
        Assert.Contains("Waiting on #1", texts);
        Assert.Contains(texts, t => t?.Contains("#2", StringComparison.Ordinal) == true && t.Contains("Run the migration", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task A_replys_Show_as_the_plan_puts_it_at_the_head_of_the_tasks_page()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1200, 800);
        h.Transport.EmitTurn("Here's my plan: read the build script, then fix the config.");
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        Button ShowAsPlan() => view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Show as the plan");
        await UiText.SettleUntilAsync(window, () => view.GetVisualDescendants().OfType<Button>().Any(b => AutomationProperties.GetName(b) == "Show as the plan"), "the reply's chip");
        var button = ShowAsPlan();
        Assert.True(button.IsVisible);
        Assert.IsType<Claudette.App.Conversation.AssistantTextItem>(button.CommandParameter);

        button.Command!.Execute(button.CommandParameter);
        // The plan's Markdown lays out in the background: the page is read once it shows the plan's text.
        await UiText.SettleUntilAsync(window, () => tab.IsTasksPage && Texts(view).Any(t => t?.Contains("read the build script", StringComparison.Ordinal) == true), "the plan on the tasks page");
        var texts = Texts(view);

        Assert.Contains("Plan", texts);
        Assert.Contains(texts, t => t?.StartsWith("From Claude's reply at ", StringComparison.Ordinal) == true);
    }

    [AvaloniaFact]
    public async Task The_mcp_page_lists_the_servers_and_turns_one_off()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var servers = JsonNode.Parse("""[{"name":"tickets","status":"connected","tools":[{"name":"a"}]},{"name":"broken","status":"failed","error":"spawn ENOENT"}]""")!.AsArray();
        h.Transport.Answers["mcp_status"] = _ => new JsonObject { ["mcpServers"] = servers.DeepClone() };
        h.Transport.Answers["mcp_toggle"] = request =>
        {
            servers.OfType<JsonObject>().Single(s => s["name"]!.GetValue<string>() == request["serverName"]!.GetValue<string>())["status"] = "disabled";
            return [];
        };
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1200, 800);
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default","mcp_servers":[{"name":"tickets","status":"connected"},{"name":"broken","status":"failed"}]}""");
        await UiText.SettleUntilAsync(window, () => tab.HasMcpServers, "the servers");

        tab.IsSidePanelOpen = true;
        tab.ShowMcpPageCommand.Execute(null);
        await UiText.SettleUntilAsync(window, () => tab.McpServers.Servers.Count == 2, "the list");
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        var texts = Texts(view);
        Assert.Contains("Connected · 1 tool", texts);
        Assert.Contains("Failed", texts);
        Assert.Equal("1 needs attention", tab.McpServers.Attention);

        var turnOff = view.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == "Turn off" && b.DataContext is McpServerRow { Name: "tickets" });
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)turnOff.Command!).ExecuteAsync(turnOff.CommandParameter);
        await UiText.SettleUntilAsync(window, () => tab.McpServers.Servers.Any(s => s is { Name: "tickets", IsDisabled: true }), "the server off");
        Assert.Contains("mcp_toggle", h.Transport.SentControlSubtypes);
    }

    [AvaloniaFact]
    public async Task An_mcp_server_row_lights_up_under_the_pointer()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        h.Transport.Answers["mcp_status"] = _ => new JsonObject { ["mcpServers"] = JsonNode.Parse("""[{"name":"tickets","status":"connected"}]""") };
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1200, 800);
        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default","mcp_servers":[{"name":"tickets","status":"connected"}]}""");
        await UiText.SettleUntilAsync(window, () => tab.HasMcpServers, "the servers");
        tab.IsSidePanelOpen = true;
        tab.ShowMcpPageCommand.Execute(null);
        var view = window.GetVisualDescendants().OfType<TabView>().Single();
        List<Border> Rows() => [.. view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("mcprow"))];
        await UiText.SettleUntilAsync(window, () => Rows().Count == 1, "the server's row");
        var row = Rows()[0];
        var hover = Assert.IsAssignableFrom<ISolidColorBrush>(window.FindResource(window.ActualThemeVariant, "HoverBrush")).Color;
        Assert.Equal(Colors.Transparent, Fill(row));

        window.MouseMove(row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value);
        UiText.Settle(window);
        Assert.Equal(hover, Fill(row));

        window.MouseMove(new Point(5, 5));
        UiText.Settle(window);
        Assert.Equal(Colors.Transparent, Fill(row));
        window.Close();
    }

    private static Color? Fill(Border border) => (border.Background as ISolidColorBrush)?.Color;
}
