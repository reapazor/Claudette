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

public sealed record RateLimitUpdated(RateLimitEventMessage Message) : SessionEvent;

/// <summary>
/// Claude Code needs a sign-in (DESIGN.md §11, "Detecting"): an <c>assistant</c> message with a sign-in error, or an
/// <c>auth_status</c> message with an error.
/// </summary>
public sealed record AuthenticationRequired(string? Detail) : SessionEvent;

/// <summary>The conversation was cleared (for example by <c>/clear</c>); drop the view and any cached title.</summary>
public sealed record ConversationReset(string? Trigger) : SessionEvent;

/// <summary>Any other <c>system</c> message (status, tasks, retries, …).</summary>
public sealed record SystemNotice(SystemMessage Message) : SessionEvent;

/// <summary>A message type Claudette doesn't know yet (DESIGN.md §16, "Staying tolerant at runtime").</summary>
public sealed record UnrecognizedMessage(string MessageType) : SessionEvent;

/// <summary>A line that couldn't be parsed. The session keeps going.</summary>
public sealed record ProtocolError(string Line, string Error) : SessionEvent;

public sealed record SessionExited(TransportExit Exit) : SessionEvent;
