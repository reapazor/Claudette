using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Json;

namespace Claudette.Core.Protocol;

/// <summary>Parses stream-json output lines. Never throws: a line that can't be read is reported as an error.</summary>
public static class MessageParser
{
    public static bool TryParse(string line, [NotNullWhen(true)] out ClaudeMessage? message, [NotNullWhen(false)] out string? error)
    {
        message = null;
        JsonObject? obj;
        try
        {
            obj = JsonTree.ParseObject(line);
        }
        catch (JsonException ex)
        {
            error = $"Not valid JSON: {ex.Message}";
            return false;
        }
        if (obj is null)
        {
            error = "Not a JSON object.";
            return false;
        }

        try
        {
            message = Parse(obj);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never let one line's shape throw out of here (CLAUDE.md, "Parse tolerantly").
            error = $"Unexpected shape: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// <see cref="TryParse(string, out ClaudeMessage?, out string?)"/> for a message already read as JSON, such as one a
    /// transcript entry holds: no text to write out and read back. Null when its shape can't be read.
    /// </summary>
    internal static ClaudeMessage? TryParse(JsonObject obj)
    {
        try
        {
            return Parse(obj);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Types Claude Code sends that Claudette has seen and has no use for, so they aren't counted as unknown
    /// (compat/surface.yaml).
    /// </summary>
    private static readonly HashSet<string> IgnoredTypes = new(StringComparer.Ordinal) { "active_goal" };

    private static ClaudeMessage Parse(JsonObject obj)
    {
        var type = obj.GetString("type") ?? "";
        return type switch
        {
            "system" when obj.GetString("subtype") == "init" => new SystemInitMessage(
                obj.GetString("session_id") ?? "",
                obj.GetString("model"),
                obj.GetString("permissionMode"),
                obj.GetString("cwd"),
                obj.GetString("claude_code_version"),
                obj.GetStringList("capabilities"),
                obj),
            "system" => new SystemMessage(obj.GetString("subtype") ?? "", obj),
            "assistant" => new AssistantMessage(
                obj.GetObject("message")?.GetString("id"),
                obj.GetObject("message")?.GetString("model"),
                ContentBlockParser.Parse(obj.GetObject("message")?["content"]),
                obj.GetString("parent_tool_use_id"),
                obj.GetString("error"),
                obj),
            "user" => new UserMessage(
                ContentBlockParser.Parse(obj.GetObject("message")?["content"]),
                obj["tool_use_result"],
                obj.GetString("parent_tool_use_id"),
                obj),
            "stream_event" => new StreamEventMessage(obj.GetObject("event") ?? [], obj.GetString("parent_tool_use_id"), obj),
            "result" => new ResultMessage(
                obj.GetString("subtype") ?? "",
                obj.GetBool("is_error") ?? false,
                obj.GetString("result"),
                obj.GetString("terminal_reason"),
                obj.GetString("session_id"),
                obj.GetDouble("total_cost_usd"),
                obj.GetObject("usage"),
                obj.GetObject("modelUsage"),
                obj),
            "rate_limit_event" => new RateLimitEventMessage(obj.GetObject("rate_limit_info") ?? [], obj),
            "tool_progress" => new ToolProgressMessage(
                obj.GetString("tool_use_id"),
                obj.GetString("tool_name"),
                obj.GetString("parent_tool_use_id"),
                obj.GetDouble("elapsed_time_seconds"),
                obj.GetBool("heartbeat") == true,
                obj),
            "auth_status" => new AuthStatusMessage(
                obj.GetBool("isAuthenticating") ?? false,
                obj.GetStringList("output"),
                obj.GetString("error"),
                obj),
            "control_request" => new ControlRequestMessage(
                obj.GetString("request_id") ?? "",
                obj.GetObject("request")?.GetString("subtype") ?? "",
                obj.GetObject("request") ?? [],
                obj),
            "control_response" => ParseControlResponse(obj),
            "control_cancel_request" => new ControlCancelRequestMessage(obj.GetString("request_id") ?? "", obj),
            "conversation_reset" => new ConversationResetMessage(obj.GetString("new_conversation_id"), obj.GetString("trigger"), obj),
            "autocompact_state" => new AutocompactStateMessage(
                obj.GetObject("value")?.GetBool("enabled") ?? false,
                obj.GetObject("value")?.GetDouble("effective_window") is { } window ? (long)window : null,
                obj.GetObject("value")?.GetDouble("threshold") is { } threshold ? (long)threshold : null,
                obj),
            _ when IgnoredTypes.Contains(type) => new IgnoredMessage(type, obj),
            _ => new UnknownMessage(type, obj),
        };
    }

    private static ControlResponseMessage ParseControlResponse(JsonObject obj)
    {
        var response = obj.GetObject("response") ?? [];
        return new ControlResponseMessage(
            response.GetString("request_id") ?? "",
            response.GetString("subtype") == "success",
            response.GetObject("response"),
            response.GetString("error"),
            obj);
    }
}
