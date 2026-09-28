using System.Text.Json.Nodes;

namespace Claudette.Core.Protocol;

/// <summary>Builds the JSON lines Claudette writes to Claude Code's standard input (DESIGN.md §13, "Wire format").</summary>
public static class OutgoingMessages
{
    public static JsonObject UserText(string text) => new()
    {
        ["type"] = "user",
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        ["parent_tool_use_id"] = null,
        ["session_id"] = "",
    };

    public static JsonObject ControlRequest(string requestId, JsonObject request) => new()
    {
        ["type"] = "control_request",
        ["request_id"] = requestId,
        ["request"] = request,
    };

    public static JsonObject ControlSuccess(string requestId, JsonObject? response) => new()
    {
        ["type"] = "control_response",
        ["response"] = new JsonObject
        {
            ["subtype"] = "success",
            ["request_id"] = requestId,
            ["response"] = response ?? new JsonObject(),
        },
    };

    public static JsonObject ControlError(string requestId, string error) => new()
    {
        ["type"] = "control_response",
        ["response"] = new JsonObject
        {
            ["subtype"] = "error",
            ["request_id"] = requestId,
            ["error"] = error,
        },
    };
}
