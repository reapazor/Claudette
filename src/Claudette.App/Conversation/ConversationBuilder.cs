using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.App.Conversation;

/// <summary>
/// Turns session events into conversation items (DESIGN.md §5). Must run on the UI thread, because the Markdown
/// builders it appends to are bound to the view. Subagent traffic goes to a child builder for that subagent's group.
/// </summary>
public sealed class ConversationBuilder
{
    private readonly Dictionary<string, ToolUseItem> _toolUses = [];
    private readonly Dictionary<string, PromptItem> _permissions = [];
    private readonly Dictionary<string, ConversationBuilder> _subagents = [];
    private readonly HashSet<string> _todoToolUses = [];
    private readonly TodoList? _todoList;
    private readonly Func<string?, string?> _modelName;
    private AssistantTextItem? _openText;
    private ThinkingItem? _openThinking;
    private NoteItem? _retryNote;
    private AgentMap? _agents;

    // The agent whose stream this builder shows: the main agent, or the subagent whose group it fills.
    private AgentNode? _agent;

    // Claude Code streams a block's deltas, then sends the complete block as an assistant message. If deltas arrived
    // since the last assistant message, its text has already been shown.
    private bool _textDeltasSinceAssistant;
    private bool _thinkingDeltasSinceAssistant;

    /// <param name="todoList">The pinned to-do list; null for subagent groups, whose to-dos aren't shown.</param>
    /// <param name="modelName">Turns a model id into a display name for turn summaries.</param>
    public ConversationBuilder(ObservableCollection<ConversationItem> items, TodoList? todoList = null, Func<string?, string?>? modelName = null)
    {
        Items = items;
        _todoList = todoList;
        _modelName = modelName ?? (m => m);
    }

    public ObservableCollection<ConversationItem> Items { get; }

    /// <summary>Show thinking expanded rather than collapsed (Settings → Appearance).</summary>
    public bool ExpandThinking { get; set; }

    /// <summary>Show messages Claudette skipped as rows with their JSON: protocol logging is on (DESIGN.md §16).</summary>
    public bool ShowUnsupportedMessages { get; set; }

    /// <summary>
    /// The tab's agent map (DESIGN.md §18), kept from the same routing as the subagent groups so the two agree.
    /// </summary>
    public AgentMap? Agents
    {
        get => _agents;
        init
        {
            _agents = value;
            _agent = value?.Root;
        }
    }

    public UserMessageItem AddUserMessage(string text, string? suffixText = null, bool isCheckIn = false, IReadOnlyList<MessageImage>? images = null)
    {
        CloseOpen();
        var item = new UserMessageItem(text, suffixText, isCheckIn) { Images = images ?? [] };
        Items.Add(item);
        return item;
    }

    public void AddNote(string text, NoteKind kind = NoteKind.Info)
    {
        CloseOpen();
        Items.Add(new NoteItem(text, kind));
    }

    /// <summary>Clears the conversation, for example after <c>/clear</c>.</summary>
    public void Clear()
    {
        Items.Clear();
        _toolUses.Clear();
        _permissions.Clear();
        _subagents.Clear();
        _todoToolUses.Clear();
        _todoList?.Clear();
        _agents?.Clear();
        _openText = null;
        _openThinking = null;
        _retryNote = null;
    }

    public void Apply(SessionEvent sessionEvent)
    {
        if (ParentOf(sessionEvent) is { } parent)
        {
            // Subagent traffic belongs in that subagent's group, however deep.
            if (FindSubagent(parent) is { } child)
            {
                child.Apply(WithoutParent(sessionEvent));
            }
            return;
        }

        switch (sessionEvent)
        {
            case TextDelta delta:
                _textDeltasSinceAssistant = true;
                AppendText(delta.Text);
                break;

            case ThinkingDelta delta:
                _thinkingDeltasSinceAssistant = true;
                AppendThinking(delta.Text);
                break;

            case AssistantMessageReceived assistant:
                ApplyAssistant(assistant.Message);
                break;

            case ToolResultsReceived results:
                ApplyToolResults(results.Message);
                break;

            case LocalCommandOutputReceived local:
                AddNote(local.Text.Replace("`", "", StringComparison.Ordinal));
                break;

            case PermissionRequested permission:
                AddPrompt(permission.Request);
                break;

            case StateChanged changed:
                _agents?.OnSessionState(changed.State);
                break;

            case SystemNotice { Message.Subtype: "task_started" or "task_progress" or "task_notification" or "task_updated" } task:
                _agents?.OnTask(task.Message);
                break;

            case ToolProgress progress:
                _agents?.OnToolProgress(progress.Message);
                break;

            case PermissionCancelled cancelled:
                if (_permissions.TryGetValue(cancelled.RequestId, out var cancelledItem))
                {
                    cancelledItem.Cancel();
                }
                break;

            case SystemNotice { Message.Subtype: "api_retry" } retry:
                ApplyRetry(retry.Message.Raw);
                break;

            case SystemNotice { Message.Subtype: "compact_boundary" } compacted:
                var trigger = compacted.Message.Raw.GetObject("compact_metadata")?.GetString("trigger");
                AddNote(trigger == "auto" ? "Claude Code compacted the conversation to free up context." : "Conversation compacted.");
                break;

            case SystemNotice { Message.Subtype: "permission_denied" } denied:
                // Denied without asking, by a rule or the permission mode.
                var raw = denied.Message.Raw;
                var tool = raw.GetString("tool_name") ?? "a tool";
                var why = raw.GetString("decision_reason") ?? raw.GetString("message");
                AddNote(why is null ? $"Claude Code denied {tool}." : $"Claude Code denied {tool}: {why}", NoteKind.Warning);
                break;

            case TurnCompleted completed:
                CloseOpen();
                _retryNote = null;
                ApplyTurnCompleted(completed.Result);
                _agents?.OnTurnCompleted();
                break;

            case ConversationReset:
                Clear();
                AddNote("Conversation cleared.");
                break;

            case UnrecognizedMessage unrecognized when ShowUnsupportedMessages:
                Items.Add(new UnsupportedMessageItem(unrecognized.MessageType,
                    unrecognized.Raw.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })));
                break;

            case ProtocolError protocolError:
                AddNote($"Skipped a message Claudette couldn't read: {protocolError.Error}", NoteKind.Warning);
                break;

            case SessionExited exited:
                CloseOpen();
                foreach (var pending in _permissions.Values)
                {
                    pending.Cancel();
                }
                var code = exited.Exit.ExitCode?.ToString() ?? "unknown";
                var detail = string.IsNullOrWhiteSpace(exited.Exit.StandardErrorTail) ? "" : $"\n{LastLines(exited.Exit.StandardErrorTail, 5)}";
                AddNote($"Claude Code exited (code {code}).{detail}", exited.Exit.ExitCode == 0 ? NoteKind.Info : NoteKind.Error);
                _agents?.OnSessionExited();
                break;
        }
    }

    /// <summary>
    /// A permission prompt, a clarifying question or a plan to approve (DESIGN.md §7). A question or plan replaces its
    /// own tool row, which would only repeat it.
    /// </summary>
    private void AddPrompt(PermissionRequest request)
    {
        CloseOpen();
        PromptItem item = request.ToolName switch
        {
            "AskUserQuestion" => new QuestionItem(request),
            "ExitPlanMode" => new PlanItem(request),
            _ => new PermissionItem(request),
        };
        if (item is not PermissionItem && request.ToolUseId is { } id && _toolUses.TryGetValue(id, out var row))
        {
            Items.Remove(row);
        }
        _permissions[request.RequestId] = item;
        Items.Add(item);
        _agents?.OnPrompt(item);
    }

    private void ApplyAssistant(AssistantMessage message)
    {
        var streamedText = _textDeltasSinceAssistant;
        var streamedThinking = _thinkingDeltasSinceAssistant;
        _textDeltasSinceAssistant = false;
        _thinkingDeltasSinceAssistant = false;
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case ThinkingBlock thinking:
                    if (!streamedThinking)
                    {
                        AppendThinking(thinking.Thinking);
                    }
                    CloseThinking();
                    break;

                case TextBlock text:
                    CloseThinking();
                    if (!streamedText)
                    {
                        AppendText(text.Text);
                    }
                    CloseText();
                    _agent?.OnText(text.Text);
                    break;

                case ToolUseBlock toolUse:
                    CloseOpen();
                    if (_todoList?.ApplyToolUse(toolUse.Id, toolUse.Name, toolUse.Input) == true)
                    {
                        // To-do updates show in the pinned list, not as cards.
                        _todoToolUses.Add(toolUse.Id);
                        break;
                    }
                    if (_agents is not null && _agent is not null)
                    {
                        _agents.OnToolUse(_agent, toolUse.Id, toolUse.Name, toolUse.Input);
                    }
                    if (toolUse.Name is "Agent" or "Task")
                    {
                        var subagent = new SubagentItem(toolUse.Id, toolUse.Name, toolUse.Input);
                        var child = new ConversationBuilder(subagent.Items, todoList: null, _modelName) { ExpandThinking = ExpandThinking };
                        // Its traffic fills its group and its node in the agent map, under this agent.
                        child._agents = _agents;
                        child._agent = _agent is not null ? _agents?.Add(_agent, subagent) : null;
                        _subagents[toolUse.Id] = child;
                        _toolUses[toolUse.Id] = subagent;
                        Items.Add(subagent);
                    }
                    else
                    {
                        var tool = new ToolUseItem(toolUse.Id, toolUse.Name, toolUse.Input);
                        _toolUses[toolUse.Id] = tool;
                        Items.Add(tool);
                    }
                    break;
            }
        }
    }

    private void ApplyToolResults(UserMessage message)
    {
        foreach (var result in message.Content.OfType<ToolResultBlock>())
        {
            if (_todoToolUses.Remove(result.ToolUseId))
            {
                _todoList?.ApplyToolResult(result.ToolUseId, result.Text, message.ToolUseResult);
            }
            else if (_toolUses.TryGetValue(result.ToolUseId, out var tool))
            {
                tool.ApplyResult(result.Text, result.IsError, message.ToolUseResult);
                if (tool is SubagentItem && _agents?.Find(result.ToolUseId) is { } node)
                {
                    node.OnResult(result.Text, result.IsError, message.ToolUseResult, WasInterrupted(message, result.ToolUseId));
                }
            }
        }
    }

    /// <summary>One note per retry sequence, updated in place rather than one row per attempt.</summary>
    private void ApplyRetry(JsonObject raw)
    {
        var attempt = Number(raw["attempt"]);
        var max = Number(raw["max_retries"]);
        var delay = Number(raw["retry_delay_ms"]);
        var reason = (raw["error_category"] ?? raw["error"]) is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
        var text = $"The API returned an error{(reason is null ? "" : $" ({reason.Replace('_', ' ')})")}. Retrying"
            + (attempt is null ? "" : $" (attempt {attempt}{(max is null ? "" : $" of {max}")})")
            + (delay is null ? "" : $" in {delay / 1000.0:0.#} s")
            + "…";
        if (_retryNote is not null && Items.Count > 0 && ReferenceEquals(Items[^1], _retryNote))
        {
            _retryNote.Text = text;
        }
        else
        {
            CloseOpen();
            _retryNote = new NoteItem(text, NoteKind.Warning);
            Items.Add(_retryNote);
        }
    }

    private void ApplyTurnCompleted(ResultMessage result)
    {
        if (result.TerminalReason == "aborted_streaming")
        {
            AddNote("Stopped.", NoteKind.Warning);
            return;
        }
        if (result.IsError && result.Result is { Length: > 0 } error)
        {
            AddNote(error, NoteKind.Error);
            return;
        }
        if (TurnSummary(result) is { } summary)
        {
            Items.Add(new TurnSummaryItem(summary));
        }
    }

    private string? TurnSummary(ResultMessage result)
    {
        var parts = new List<string>();
        if (result.DurationMs is { } ms)
        {
            parts.Add(ms >= 60_000 ? $"{(int)(ms / 60_000)}m {ms % 60_000 / 1000:0}s" : $"{ms / 1000:0.#}s");
        }
        if (result.Usage is { } usage)
        {
            var input = (Number(usage["input_tokens"]) ?? 0) + (Number(usage["cache_creation_input_tokens"]) ?? 0) + (Number(usage["cache_read_input_tokens"]) ?? 0);
            var output = Number(usage["output_tokens"]) ?? 0;
            parts.Add($"{Tokens(input)} in · {Tokens(output)} out");
        }
        if (result.ModelUsage is { Count: > 0 } models)
        {
            parts.Add(string.Join(", ", models.Select(m => _modelName(m.Key) ?? m.Key)));
        }
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static string Tokens(long count) => count switch
    {
        >= 1_000_000 => $"{count / 1_000_000.0:0.#}M",
        >= 1_000 => $"{count / 1_000.0:0.#}k",
        _ => count.ToString(),
    };

    private ConversationBuilder? FindSubagent(string toolUseId)
    {
        if (_subagents.TryGetValue(toolUseId, out var direct))
        {
            return direct;
        }
        foreach (var child in _subagents.Values)
        {
            if (child.FindSubagent(toolUseId) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    /// <summary>Claude Code marks a call cut off by an interrupt or a stop in <c>tool_result_meta</c>.</summary>
    private static bool WasInterrupted(UserMessage message, string toolUseId) =>
        message.Raw["tool_result_meta"] is JsonArray meta
        && meta.OfType<JsonObject>().Any(m => m.GetString("id") == toolUseId && m.GetString("non_execution_kind") == "interrupted");

    private static string? ParentOf(SessionEvent sessionEvent) => sessionEvent switch
    {
        TextDelta d => d.ParentToolUseId,
        ThinkingDelta d => d.ParentToolUseId,
        AssistantMessageReceived a => a.Message.ParentToolUseId,
        ToolResultsReceived r => r.Message.ParentToolUseId,
        _ => null,
    };

    private static SessionEvent WithoutParent(SessionEvent sessionEvent) => sessionEvent switch
    {
        TextDelta d => d with { ParentToolUseId = null },
        ThinkingDelta d => d with { ParentToolUseId = null },
        AssistantMessageReceived a => new AssistantMessageReceived(a.Message with { ParentToolUseId = null }),
        ToolResultsReceived r => new ToolResultsReceived(r.Message with { ParentToolUseId = null }),
        _ => sessionEvent,
    };

    private void AppendText(string text)
    {
        CloseThinking();
        if (_openText is null)
        {
            _openText = new AssistantTextItem();
            Items.Add(_openText);
        }
        _openText.Append(text);
    }

    private void AppendThinking(string text)
    {
        if (text.Length == 0 && _openThinking is not null)
        {
            return;
        }
        CloseText();
        if (_openThinking is null)
        {
            _openThinking = new ThinkingItem { IsExpanded = ExpandThinking };
            Items.Add(_openThinking);
        }
        _openThinking.Text += text;
    }

    private void CloseText()
    {
        if (_openText is not null)
        {
            _openText.IsStreaming = false;
            _openText = null;
        }
    }

    private void CloseThinking()
    {
        if (_openThinking is not null)
        {
            _openThinking.IsStreaming = false;
            _openThinking = null;
        }
    }

    private void CloseOpen()
    {
        CloseText();
        CloseThinking();
    }

    private static long? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? (long)value.GetValue<double>() : null;

    private static string LastLines(string text, int count) =>
        string.Join('\n', text.Split('\n').TakeLast(count));
}
