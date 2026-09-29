using System.Text.Json.Nodes;

namespace Claudette.Core.Protocol;

/// <summary>
/// A message Claude Code wrote to standard output in stream-json mode. Every message keeps its original JSON in
/// <see cref="Raw"/>, so fields Claudette doesn't model yet are still available.
/// </summary>
public abstract record ClaudeMessage(string Type, JsonObject Raw);

/// <summary><c>system/init</c>: sent at the start of each turn.</summary>
public sealed record SystemInitMessage(
    string SessionId,
    string? Model,
    string? PermissionMode,
    string? Cwd,
    string? ClaudeCodeVersion,
    IReadOnlyList<string> Capabilities,
    JsonObject Raw) : ClaudeMessage("system", Raw);

/// <summary>Any other <c>system</c> message, such as <c>status</c>, <c>task_started</c> or <c>api_retry</c>.</summary>
public sealed record SystemMessage(string Subtype, JsonObject Raw) : ClaudeMessage("system", Raw);

public sealed record AssistantMessage(
    string? MessageId,
    string? Model,
    IReadOnlyList<ContentBlock> Content,
    string? ParentToolUseId,
    string? Error,
    JsonObject Raw) : ClaudeMessage("assistant", Raw);

/// <summary>
/// A <c>user</c> message from Claude Code: tool results (with structured details in <see cref="ToolUseResult"/>),
/// or local command output such as the note that follows a model change.
/// </summary>
public sealed record UserMessage(
    IReadOnlyList<ContentBlock> Content,
    JsonNode? ToolUseResult,
    string? ParentToolUseId,
    JsonObject Raw) : ClaudeMessage("user", Raw)
{
    private const string LocalCommandStart = "<local-command-stdout>";
    private const string LocalCommandEnd = "</local-command-stdout>";

    /// <summary>The text of a local command's output (for example "Set model to …"), or null.</summary>
    public string? LocalCommandOutput
    {
        get
        {
            if (Content is not [TextBlock text])
            {
                return null;
            }
            var value = text.Text.Trim();
            return value.StartsWith(LocalCommandStart, StringComparison.Ordinal) && value.EndsWith(LocalCommandEnd, StringComparison.Ordinal)
                ? value[LocalCommandStart.Length..^LocalCommandEnd.Length]
                : null;
        }
    }
}

/// <summary>A partial streaming event (requires <c>--include-partial-messages</c>).</summary>
public sealed record StreamEventMessage(JsonObject Event, string? ParentToolUseId, JsonObject Raw) : ClaudeMessage("stream_event", Raw)
{
    public string? TextDelta => Delta("text_delta", "text");

    public string? ThinkingDelta => Delta("thinking_delta", "thinking");

    private string? Delta(string deltaType, string field) =>
        Event.GetString("type") == "content_block_delta" && Event.GetObject("delta") is { } delta && delta.GetString("type") == deltaType
            ? delta.GetString(field)
            : null;
}

/// <summary>The end of a turn.</summary>
public sealed record ResultMessage(
    string Subtype,
    bool IsError,
    string? Result,
    string? TerminalReason,
    string? SessionId,
    double? TotalCostUsd,
    JsonObject? Usage,
    JsonObject? ModelUsage,
    JsonObject Raw) : ClaudeMessage("result", Raw)
{
    public double? DurationMs => Raw.GetDouble("duration_ms");

    public int? NumTurns => Raw.GetDouble("num_turns") is { } turns ? (int)turns : null;
}

public sealed record RateLimitEventMessage(JsonObject Info, JsonObject Raw) : ClaudeMessage("rate_limit_event", Raw);

public sealed record AuthStatusMessage(bool IsAuthenticating, IReadOnlyList<string> Output, string? Error, JsonObject Raw) : ClaudeMessage("auth_status", Raw);

/// <summary>A control request from Claude Code, such as <c>can_use_tool</c>.</summary>
public sealed record ControlRequestMessage(string RequestId, string Subtype, JsonObject Request, JsonObject Raw) : ClaudeMessage("control_request", Raw);

/// <summary>Claude Code's answer to a control request Claudette sent.</summary>
public sealed record ControlResponseMessage(string RequestId, bool IsSuccess, JsonObject? Response, string? Error, JsonObject Raw) : ClaudeMessage("control_response", Raw);

/// <summary>Claude Code withdrawing a control request it sent earlier.</summary>
public sealed record ControlCancelRequestMessage(string RequestId, JsonObject Raw) : ClaudeMessage("control_cancel_request", Raw);

/// <summary>The conversation was replaced without ending the session, for example by <c>/clear</c> (DESIGN.md §13).</summary>
public sealed record ConversationResetMessage(string? NewConversationId, string? Trigger, JsonObject Raw) : ClaudeMessage("conversation_reset", Raw);

/// <summary>
/// When Claude Code compacts the conversation by itself (<b>undocumented</b>): sent at the start of each turn, with the
/// context window it works to and the token count that triggers compaction (DESIGN.md §6, "Per-tab context").
/// </summary>
public sealed record AutocompactStateMessage(bool Enabled, long? EffectiveWindow, long? Threshold, JsonObject Raw) : ClaudeMessage("autocompact_state", Raw);

/// <summary>A type Claudette knows about and deliberately skips, such as <c>active_goal</c>. Not counted as unknown.</summary>
public sealed record IgnoredMessage(string MessageType, JsonObject Raw) : ClaudeMessage(MessageType, Raw);

/// <summary>A message type Claudette doesn't know yet. Skipped, and counted for diagnostics (DESIGN.md §16).</summary>
public sealed record UnknownMessage(string MessageType, JsonObject Raw) : ClaudeMessage(MessageType, Raw);
