using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

public enum SessionState
{
    Starting,
    Idle,
    Working,
    Exited,
}

/// <summary>Something that happened in a session, in the order Claude Code reported it.</summary>
public abstract record SessionEvent;

/// <summary>A turn started (<c>system/init</c>), with the session's current details.</summary>
public sealed record TurnStarted(SystemInitMessage Init) : SessionEvent;

public sealed record TextDelta(string Text, string? ParentToolUseId) : SessionEvent;

public sealed record ThinkingDelta(string Text, string? ParentToolUseId) : SessionEvent;

public sealed record AssistantMessageReceived(AssistantMessage Message) : SessionEvent;

public sealed record ToolResultsReceived(UserMessage Message) : SessionEvent;

/// <summary>Output of a local command, such as the note Claude Code posts after a model change.</summary>
public sealed record LocalCommandOutputReceived(string Text) : SessionEvent;

public sealed record TurnCompleted(ResultMessage Result) : SessionEvent;

public sealed record StateChanged(SessionState State) : SessionEvent;

public sealed record PermissionRequested(PermissionRequest Request) : SessionEvent;

/// <summary>Claude Code withdrew a permission request (for example after an interrupt).</summary>
public sealed record PermissionCancelled(string RequestId) : SessionEvent;

/// <summary>An MCP server asks the user for input (DESIGN.md §7, "MCP servers asking for input").</summary>
public sealed record ElicitationRequested(ElicitationRequest Request) : SessionEvent;

/// <summary>Claude Code withdrew an <see cref="ElicitationRequested"/> before it was answered.</summary>
public sealed record ElicitationCancelled(string RequestId) : SessionEvent;

/// <summary>
/// A prompt Claudette sent, echoed back with its <c>uuid</c> (<c>--replay-user-messages</c>): the point the conversation
/// and its files can be rewound to (DESIGN.md §5, "Rewind and branch").
/// </summary>
public sealed record PromptReplayed(UserMessage Message) : SessionEvent;

public sealed record RateLimitUpdated(RateLimitEventMessage Message) : SessionEvent;

/// <summary>A tool call is still running, for example a subagent (a heartbeat, or waiting out an API error).</summary>
public sealed record ToolProgress(ToolProgressMessage Message) : SessionEvent;

/// <summary>
/// Claude Code needs a sign-in (DESIGN.md §11, "Detecting"): an <c>assistant</c> message with a sign-in error, or an
/// <c>auth_status</c> message with an error.
/// </summary>
public sealed record AuthenticationRequired(string? Detail) : SessionEvent;

/// <summary>The conversation was cleared (for example by <c>/clear</c>); drop the view and any cached title.</summary>
public sealed record ConversationReset(string? Trigger) : SessionEvent;

/// <summary>Claude Code's auto-compaction settings for this session, sent with each turn (undocumented).</summary>
public sealed record AutocompactStateChanged(AutocompactStateMessage State) : SessionEvent;

/// <summary>Any other <c>system</c> message (status, tasks, retries, …).</summary>
public sealed record SystemNotice(SystemMessage Message) : SessionEvent;

/// <summary>A message type Claudette doesn't know yet (DESIGN.md §16, "Staying tolerant at runtime").</summary>
public sealed record UnrecognizedMessage(string MessageType, System.Text.Json.Nodes.JsonObject Raw) : SessionEvent;

/// <summary>A line that couldn't be parsed. The session keeps going.</summary>
public sealed record ProtocolError(string Line, string Error) : SessionEvent;

public sealed record SessionExited(TransportExit Exit) : SessionEvent;
