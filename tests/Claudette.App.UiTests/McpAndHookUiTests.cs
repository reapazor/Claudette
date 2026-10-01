using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>
/// An MCP server's request for input as a card in the conversation (DESIGN.md §7), and a hook that failed as a row
/// (DESIGN.md §5, "Hook runs").
/// </summary>
public class McpAndHookUiTests
{
    [AvaloniaFact]
    public async Task An_mcp_form_shows_its_fields_and_sends_what_was_entered()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);

        h.Transport.Emit("""
            {"type":"control_request","request_id":"e1","request":{"subtype":"elicitation","mcp_server_name":"tickets","message":"Which ticket should I file this under?",
             "requested_schema":{"type":"object","properties":{"project":{"type":"string","title":"Project"},"urgent":{"type":"boolean","title":"Urgent"}},"required":["project"]}}}
            """);
        await UiText.SettleUntilAsync(window, () => tab.Items.OfType<McpInputItem>().Any(), "the card");
        var card = tab.Items.OfType<McpInputItem>().Single();
        UiText.Settle(window);
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("tickets asks", texts);
        Assert.Contains("Project (required)", texts);
        Assert.Equal(TabStatus.NeedsInput, tab.Status);

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.DataContext is McpField { Name: "project" });
        box.Text = "NEXUS";
        var send = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content as string == "Send");
        send.Command!.Execute(null);
        UiText.Settle(window);

        Assert.Equal("Sent", card.Outcome);
        await UiText.SettleUntilAsync(window, () => tab.Status != TabStatus.NeedsInput, "the tab to stop waiting");
        // The session writes the answer once the request completes, off the UI thread: wait for it.
        System.Text.Json.Nodes.JsonObject? answer = null;
        await UiText.SettleUntilAsync(window, () => (answer = h.Transport.Sent.LastOrDefault(m => m["type"]?.GetValue<string>() == "control_response")) is not null, "the answer");
        Assert.Equal("NEXUS", answer!["response"]!["response"]!["content"]!["project"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task A_failed_hook_shows_a_row_open_to_its_output()
    {
        await using var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
        var tab = await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell }, 1100, 800);

        h.Transport.Emit("""{"type":"system","subtype":"hook_started","hook_id":"h1","hook_name":"PreToolUse:Bash","hook_event":"PreToolUse","uuid":"x","session_id":"s1"}""");
        h.Transport.Emit("""{"type":"system","subtype":"hook_response","hook_id":"h1","hook_name":"PreToolUse:Bash","hook_event":"PreToolUse","output":"","stdout":"","stderr":"Blocked: rm -rf","exit_code":2,"outcome":"error","uuid":"y","session_id":"s1"}""");
        await UiText.SettleUntilAsync(window, () => tab.Items.OfType<HookRunItem>().Any(), "the row");
        UiText.Settle(window);

        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("PreToolUse hook (PreToolUse:Bash)", texts);
        Assert.Contains("failed (exit code 2)", texts);
        Assert.Contains("Blocked: rm -rf", window.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
    }
}
