using System.Text.Json.Nodes;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests.Support;

/// <summary>
/// Plays Claude Code's side for thread tests (DESIGN.md §18, "Threads"): named tabs, the threads' hook called for a
/// <c>SendMessage</c>, and the messages a turn starts with. Shared with the rendered UI tests.
/// </summary>
internal static class ThreadScript
{
    /// <summary>Opens a tab and names it, as the user would.</summary>
    public static async Task<TabViewModel> OpenAsync(TabTestHarness h, string name)
    {
        var tab = await h.OpenTabAsync();
        tab.StartRenameCommand.Execute(null);
        tab.RenameText = name;
        await tab.CommitRenameCommand.ExecuteAsync(null);
        return tab;
    }

    /// <summary>What the threads' hook is called with when Claude calls SendMessage.</summary>
    public static JsonObject SendMessage(string requestId, string to, string message, bool notify = false)
    {
        var input = new JsonObject { ["to"] = to, ["message"] = message };
        if (notify)
        {
            input["notify_when_idle"] = true;
        }
        return new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = requestId,
            ["request"] = new JsonObject
            {
                ["subtype"] = "hook_callback",
                // No Perforce in these tests, so the threads' hook is the first registered.
                ["callback_id"] = "hook_0",
                ["input"] = new JsonObject
                {
                    ["session_id"] = "s1", ["hook_event_name"] = "PreToolUse", ["tool_name"] = "SendMessage", ["tool_input"] = input, ["tool_use_id"] = $"toolu_{requestId}",
                },
                ["tool_use_id"] = $"toolu_{requestId}",
            },
        };
    }

    /// <summary>A turn starts.</summary>
    public static JsonObject Init() => new() { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "s1", ["model"] = "claude-opus-5-5", ["permissionMode"] = "default" };

    /// <summary>A permission prompt for a Bash command.</summary>
    public static JsonObject CanUseTool(string requestId, string command) => new()
    {
        ["type"] = "control_request",
        ["request_id"] = requestId,
        ["request"] = new JsonObject
        {
            ["subtype"] = "can_use_tool", ["tool_name"] = "Bash", ["input"] = new JsonObject { ["command"] = command }, ["tool_use_id"] = $"toolu_{requestId}",
        },
    };
}
