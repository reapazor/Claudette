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
    public static JsonObject UserMessage(string text, IReadOnlyList<MessageImage> images) => UserMessage(text, images, null);

    /// <summary>
    /// A user message with attached images and quick suffixes (DESIGN.md §5, "Quick suffixes"). The suffixes follow
    /// the text after a blank line, except after a slash command: there they go in a text block of their own before it.
    /// </summary>
    /// <remarks>
    /// Claude Code takes everything after a command's name as its arguments, so a suffix after the command would become
    /// its arguments (or, for a local command such as <c>/compact</c>, change what it does). It reads a command only
    /// from the last content block, and hands the blocks before it to the command's prompt (checked against 2.1.284).
    /// </remarks>
    public static JsonObject UserMessage(string text, IReadOnlyList<MessageImage> images, string? suffix)
    {
        var separate = !string.IsNullOrEmpty(suffix) && IsSlashCommand(text);
        if (!separate && !string.IsNullOrEmpty(suffix))
        {
            text = text.Length == 0 ? suffix : $"{text}\n\n{suffix}";
        }
        if (images.Count == 0 && !separate)
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
        if (separate)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = suffix });
        }
        if (text.Length > 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }
        var message = UserText("");
        message["message"]!["content"] = content;
        return message;
    }

    /// <summary>Whether Claude Code reads <paramref name="text"/> as a slash command: it starts with <c>/</c>, as Claude Code checks.</summary>
    public static bool IsSlashCommand(string text) => text.TrimStart().StartsWith('/');

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
