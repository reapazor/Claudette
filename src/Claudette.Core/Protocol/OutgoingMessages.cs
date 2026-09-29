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

    /// <summary>
    /// A user message with attached images (DESIGN.md §5, "Attachments"): base64 <c>image</c> blocks, then the text.
    /// Without images it's the same plain-text message as <see cref="UserText"/>.
    /// </summary>
    /// <remarks>
    /// The text goes last because Claude Code only expands <c>@path</c> mentions in the last content block, and only
    /// when that block is text (checked against 2.1.284).
    /// </remarks>
    public static JsonObject UserMessage(string text, IReadOnlyList<MessageImage> images)
    {
        if (images.Count == 0)
        {
            return UserText(text);
        }
        var content = new JsonArray();
        foreach (var image in images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MediaType, ["data"] = Convert.ToBase64String(image.Data) },
            });
        }
        if (text.Length > 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
        var message = UserText("");
        message["message"]!["content"] = content;
        return message;
    }

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
