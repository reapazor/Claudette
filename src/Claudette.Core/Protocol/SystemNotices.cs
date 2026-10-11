namespace Claudette.Core.Protocol;

/// <summary>Which of a hook run's messages arrived.</summary>
public enum HookRunStage
{
    /// <summary><c>hook_started</c>: the hook began.</summary>
    Started,

    /// <summary><c>hook_progress</c>: what the hook printed so far.</summary>
    Progress,

    /// <summary><c>hook_response</c>: the hook ended.</summary>
    Response,
}

/// <summary>How a hook's run ended (<c>hook_response</c>'s <c>outcome</c>).</summary>
public enum HookOutcome
{
    /// <summary>Not said, or a value Claudette doesn't know.</summary>
    Unknown,

    Success,

    Error,

    Cancelled,
}

/// <summary>
/// A hook's run, as Claude Code reports it with <c>--include-hook-events</c> (DESIGN.md §5, "Hook runs"):
/// <c>system/hook_started</c>, then <c>hook_progress</c> while it prints, then <c>hook_response</c> when it ends. All
/// three carry the same <see cref="HookId"/>.
/// </summary>
/// <param name="HookName">The hook's name, such as <c>PreToolUse:Bash</c>.</param>
/// <param name="HookEvent">The event that ran it, such as <c>PreToolUse</c>.</param>
/// <param name="Output">Everything it printed so far; <see cref="Stdout"/> and <see cref="Stderr"/> hold the two apart.</param>
/// <param name="ExitCode">How its process exited, on <see cref="HookRunStage.Response"/>.</param>
/// <param name="Outcome">How it ended, on <see cref="HookRunStage.Response"/>.</param>
public sealed record HookRunNotice(
    HookRunStage Stage,
    string HookId,
    string? HookName,
    string? HookEvent,
    string? Output,
    string? Stdout,
    string? Stderr,
    int? ExitCode,
    HookOutcome Outcome)
{
    /// <summary>The run a hook message reports; null for another subtype, or a message without a <c>hook_id</c>.</summary>
    internal static HookRunNotice? From(SystemMessage message)
    {
        HookRunStage? stage = message.Subtype switch
        {
            "hook_started" => HookRunStage.Started,
            "hook_progress" => HookRunStage.Progress,
            "hook_response" => HookRunStage.Response,
            _ => null,
        };
        var raw = message.Raw;
        if (stage is null || raw.GetString("hook_id") is not { Length: > 0 } id)
        {
            return null;
        }
        return new HookRunNotice(
            stage.Value,
            id,
            raw.GetString("hook_name"),
            raw.GetString("hook_event"),
            raw.GetString("output"),
            raw.GetString("stdout"),
            raw.GetString("stderr"),
            raw.GetDouble("exit_code") is { } code ? (int)code : null,
            raw.GetString("outcome") switch
            {
                "success" => HookOutcome.Success,
                "error" => HookOutcome.Error,
                "cancelled" => HookOutcome.Cancelled,
                _ => HookOutcome.Unknown,
            });
    }
}

/// <summary>
/// <c>system/api_retry</c>: a request to the API failed and Claude Code is about to try it again.
/// </summary>
/// <param name="ErrorStatus">The HTTP status of the failed request; null when there was no answer.</param>
/// <param name="Category">What went wrong, such as <c>overloaded</c>: <c>error_category</c>, else <c>error</c>.</param>
public sealed record ApiRetryNotice(int? Attempt, int? MaxRetries, long? RetryDelayMs, int? ErrorStatus, string? Category)
{
    internal static ApiRetryNotice? From(SystemMessage message)
    {
        if (message.Subtype != "api_retry")
        {
            return null;
        }
        var raw = message.Raw;
        return new ApiRetryNotice(
            raw.GetDouble("attempt") is { } attempt ? (int)attempt : null,
            raw.GetDouble("max_retries") is { } max ? (int)max : null,
            raw.GetDouble("retry_delay_ms") is { } delay ? (long)delay : null,
            raw.GetDouble("error_status") is { } status ? (int)status : null,
            raw.GetString("error_category") ?? raw.GetString("error"));
    }
}

/// <summary><c>system/compact_boundary</c>: Claude Code compacted the conversation.</summary>
/// <param name="Trigger"><c>manual</c> for <c>/compact</c>, <c>auto</c> when Claude Code did it to free up context.</param>
public sealed record CompactBoundaryNotice(string? Trigger)
{
    /// <summary>Claude Code compacted by itself, not because it was asked to.</summary>
    public bool IsAutomatic => Trigger == "auto";

    internal static CompactBoundaryNotice? From(SystemMessage message) =>
        message.Subtype == "compact_boundary" ? new CompactBoundaryNotice(message.Raw.GetObject("compact_metadata")?.GetString("trigger")) : null;
}

/// <summary>
/// <c>system/status</c>: what Claude Code is busy with. Its <c>status</c> is <c>compacting</c> while it compacts the
/// conversation, and null once it's done.
/// </summary>
public sealed record StatusNotice(string? Status)
{
    public bool IsCompacting => Status == "compacting";

    internal static StatusNotice? From(SystemMessage message) =>
        message.Subtype == "status" ? new StatusNotice(message.Raw.GetString("status")) : null;
}

/// <summary>How much an informational notice matters (its <c>level</c>).</summary>
public enum NoticeLevel
{
    /// <summary>Not said, or a value Claudette doesn't know.</summary>
    Unknown,

    Info,

    Notice,

    Suggestion,

    Warning,
}

/// <summary>
/// <c>system/informational</c>: one of Claude Code's warnings or notices, or a hook's message to the user (DESIGN.md §5,
/// "Notices").
/// </summary>
/// <param name="Content">The text, plain.</param>
public sealed record InformationalNotice(string? Content, NoticeLevel Level)
{
    internal static InformationalNotice? From(SystemMessage message) =>
        message.Subtype == "informational"
            ? new InformationalNotice(
                message.Raw.GetString("content"),
                message.Raw.GetString("level") switch
                {
                    "info" => NoticeLevel.Info,
                    "notice" => NoticeLevel.Notice,
                    "suggestion" => NoticeLevel.Suggestion,
                    "warning" => NoticeLevel.Warning,
                    _ => NoticeLevel.Unknown,
                })
            : null;
}

/// <summary><c>system/permission_denied</c>: a tool call denied without asking, by a rule or the permission mode.</summary>
/// <param name="Reason">Why: <c>decision_reason</c>, else <c>message</c>.</param>
public sealed record PermissionDeniedNotice(string? ToolName, string? Reason)
{
    internal static PermissionDeniedNotice? From(SystemMessage message) =>
        message.Subtype == "permission_denied"
            ? new PermissionDeniedNotice(message.Raw.GetString("tool_name"), message.Raw.GetString("decision_reason") ?? message.Raw.GetString("message"))
            : null;
}

/// <summary>
/// <c>system/elicitation_complete</c>: an MCP server says a URL request is done; the user finished in the browser
/// (DESIGN.md §7, "MCP servers asking for input").
/// </summary>
/// <param name="ElicitationId">The request's <c>elicitation_id</c>.</param>
public sealed record ElicitationCompleteNotice(string ElicitationId)
{
    /// <summary>Null for another subtype, or a message without an <c>elicitation_id</c>.</summary>
    internal static ElicitationCompleteNotice? From(SystemMessage message) =>
        message.Subtype == "elicitation_complete" && message.Raw.GetString("elicitation_id") is { } id ? new ElicitationCompleteNotice(id) : null;
}
