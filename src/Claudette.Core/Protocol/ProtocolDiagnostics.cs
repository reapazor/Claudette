namespace Claudette.Core.Protocol;

/// <summary>
/// What Claudette has seen from Claude Code that it doesn't know yet (DESIGN.md §16, "Staying tolerant at runtime"):
/// message types it skipped, top-level fields it hasn't seen before, and lines it couldn't read. Shared by every
/// session; shown on Settings → Advanced → Diagnostics.
/// </summary>
public sealed class ProtocolDiagnostics
{
    /// <summary>
    /// The top-level fields of each message type in the tested Claude Code version, from the recorded fixtures and the
    /// fields Claudette reads. A field outside the list is new. Types without a list aren't checked.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> KnownFields = new(StringComparer.Ordinal)
    {
        ["assistant"] = Fields("aborted", "error", "is_api_error_message", "message", "parent_tool_use_id", "session_id", "timestamp", "type", "uuid"),
        ["user"] = Fields("isReplay", "isSynthetic", "message", "parent_tool_use_id", "session_id", "timestamp", "tool_result_meta", "tool_use_result", "type", "uuid"),
        ["result"] = Fields("api_error_status", "duration_api_ms", "duration_ms", "errors", "fast_mode_disabled_reason", "fast_mode_state",
            "first_content_frame_ms", "is_error", "local_command", "modelUsage", "num_turns", "permission_denials", "queued_turn_count", "result",
            "result_index", "session_id", "stop_reason", "subagent_stats", "subtype", "terminal_reason", "time_origin_ms",
            "time_to_request_from_spawn_ms", "time_to_request_ms", "total_cost_usd", "ttft_ms", "ttft_stream_ms", "type", "usage", "uuid",
            "warm_spare_claimed"),
        ["stream_event"] = Fields("event", "parent_tool_use_id", "session_id", "thinking_display", "ttft_ms", "type", "uuid"),
        ["control_request"] = Fields("request", "request_id", "type"),
        ["autocompact_state"] = Fields("session_id", "type", "uuid", "value"),
        ["control_response"] = Fields("response", "type"),
        ["system/init"] = Fields("agents", "analytics_disabled", "apiKeySource", "capabilities", "claude_code_version", "cwd", "fast_mode_disabled_reason",
            "fast_mode_state", "mcp_servers", "memory_paths", "messaging_socket_path", "model", "output_style", "per_turn_effort_active",
            "permissionMode", "plugins", "powershell_path", "product_feedback_disabled", "session_id", "skills", "slash_commands", "startup_timing", "subtype",
            "terminal_slash_commands", "tools", "type", "uuid", "view_mode"),
        ["system/status"] = Fields("compact_result", "permissionMode", "session_id", "status", "subtype", "type", "uuid"),
        ["system/task_started"] = Fields("description", "is_backgrounded", "session_id", "subtype", "task_id", "task_type", "tool_use_id", "type", "uuid"),
        ["system/task_notification"] = Fields("output_file", "session_id", "status", "subtype", "summary", "task_id", "tool_use_id", "type", "uuid"),
    };

    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _unknownTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _unknownFields = new(StringComparer.Ordinal);
    private int _parseErrors;

    private static HashSet<string> Fields(params string[] names) => new(names, StringComparer.Ordinal);

    /// <summary>A message of a type Claudette doesn't handle, which it skipped.</summary>
    public void RecordUnknownMessage(string type)
    {
        lock (_lock)
        {
            _unknownTypes[type] = _unknownTypes.GetValueOrDefault(type) + 1;
        }
    }

    /// <summary>A line that wasn't JSON, or a message that failed to handle.</summary>
    public void RecordParseError() => Interlocked.Increment(ref _parseErrors);

    /// <summary>Counts the message's top-level fields that its type didn't have in the tested version.</summary>
    public void RecordFields(ClaudeMessage message)
    {
        var key = message.Type == "system" && message.Raw.GetString("subtype") is { } subtype ? $"system/{subtype}" : message.Type;
        if (!KnownFields.TryGetValue(key, out var known))
        {
            return;
        }
        List<string>? unknown = null;
        foreach (var (name, _) in message.Raw)
        {
            if (!known.Contains(name))
            {
                (unknown ??= []).Add($"{key}.{name}");
            }
        }
        if (unknown is null)
        {
            return;
        }
        lock (_lock)
        {
            foreach (var field in unknown)
            {
                _unknownFields[field] = _unknownFields.GetValueOrDefault(field) + 1;
            }
        }
    }

    public DiagnosticsSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new DiagnosticsSnapshot(
                new SortedDictionary<string, int>(_unknownTypes, StringComparer.Ordinal),
                new SortedDictionary<string, int>(_unknownFields, StringComparer.Ordinal),
                Volatile.Read(ref _parseErrors));
        }
    }
}

/// <summary>The counts so far, by message type (<c>type</c>) and field (<c>type.field</c>).</summary>
public sealed record DiagnosticsSnapshot(IReadOnlyDictionary<string, int> UnknownMessageTypes, IReadOnlyDictionary<string, int> UnknownFields, int ParseErrors)
{
    public int UnknownMessageCount => UnknownMessageTypes.Values.Sum();
}
