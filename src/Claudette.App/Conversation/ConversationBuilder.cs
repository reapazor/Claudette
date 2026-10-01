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
    private readonly Dictionary<string, HookRunItem> _hookRuns = [];
    private readonly Dictionary<string, McpInputItem> _mcpInputs = [];
    private readonly Dictionary<string, ConversationBuilder> _subagents = [];
    private readonly HashSet<string> _todoToolUses = [];
    private TodoList? _todoList;
    private readonly Func<string?, string?> _modelName;
    private AssistantTextItem? _openText;
    private ThinkingItem? _openThinking;
    private NoteItem? _retryNote;

    // The last entry of the main conversation Claude Code gave an id: where a resume would stop to leave out what comes
    // next (DESIGN.md §5, "Rewind and branch").
    private string? _lastEntryUuid;
    private AgentMap? _agents;
    private RunningTasks? _tasks;

    // The agent whose stream this builder shows: the main agent, or the subagent whose group it fills.
    private AgentNode? _agent;

    // Claude Code streams a block's deltas, then sends the complete block as an assistant message. If deltas arrived
    // since the last assistant message, its text has already been shown.
    private bool _textDeltasSinceAssistant;
    private bool _thinkingDeltasSinceAssistant;

    // Replaying a transcript: messages take the entry's time rather than now (DESIGN.md §5, "Copy and times").
    // A subagent's builder asks the builder that made it, so a replay reaches every level.
    private ConversationBuilder? _parent;
    private bool _replaying;
    private DateTimeOffset? _replayTime;

    /// <param name="todoList">The pinned to-do list and the Tasks page; a subagent's builder shares its parent's.</param>
    /// <param name="modelName">Turns a model id into a display name for turn summaries.</param>
    public ConversationBuilder(ObservableCollection<ConversationItem> items, TodoList? todoList = null, Func<string?, string?>? modelName = null)
    {
        Items = items;
        _todoList = todoList;
        if (todoList is not null)
        {
            todoList.Clock = Now;
        }
        _modelName = modelName ?? (m => m);
    }

    public ObservableCollection<ConversationItem> Items { get; }

    /// <summary>Show thinking expanded rather than collapsed (Settings → Appearance).</summary>
    public bool ExpandThinking { get; set; }

    /// <summary>
    /// The clock that says when a message was sent, and what "today" is when its time is shown (DESIGN.md §5). Without
    /// one, messages have no time.
    /// </summary>
    public TimeProvider? Time { get; init; }

    /// <summary>Show messages Claudette skipped as rows with their JSON: protocol logging is on (DESIGN.md §16).</summary>
    public bool ShowUnsupportedMessages { get; set; }

    /// <summary>Opens an MCP server's link in the browser, for its requests that ask the user to finish something there.</summary>
    public Func<string, Task>? OpenUrl { get; set; }

    /// <summary>
    /// Show a row for every hook run (Settings → Sessions). Off, only runs that fail, or print something while they
    /// work, get one (DESIGN.md §5, "Hook runs").
    /// </summary>
    public bool ShowAllHookRuns { get; set; }

    /// <summary>
    /// What a prompt Claude Code withdraws says: "Answered in the Claude app" while the tab is connected to it (DESIGN.md
    /// §18, "Remote Control"). Null, or no function, keeps <see cref="PromptItem.WithdrawnOutcome"/>.
    /// </summary>
    public Func<string?>? WithdrawnOutcome { get; set; }

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

    /// <summary>
    /// The tab's running tasks (DESIGN.md §5, "Running tasks"): Claude Code's task messages, matched to the cards of the
    /// calls that started them.
    /// </summary>
    public RunningTasks? Tasks
    {
        get => _tasks;
        init => _tasks = value;
    }

    /// <summary>The turn under way is one Claude Code carries on after a restart, and the conversation says so.</summary>
    private bool _carriedOnNoted;

    /// <summary>Ids of messages Claudette sent without a card, such as Compact's <c>/compact</c>: their echoes are no prompt's.</summary>
    private readonly HashSet<string> _sentWithoutCard = new(StringComparer.Ordinal);

    /// <summary>A message went with this id and no card of its own.</summary>
    public void SentWithoutCard(string uuid) => _sentWithoutCard.Add(uuid);

    /// <summary>Takes a prompt's card away: Claude Code cancelled it before it ran (DESIGN.md §5, "Queued messages").</summary>
    public void Remove(UserMessageItem prompt) => Items.Remove(prompt);

    public UserMessageItem AddUserMessage(string text, string? suffixText = null, bool isCheckIn = false, IReadOnlyList<MessageImage>? images = null, bool isAutoContinue = false) =>
        AddUser(new UserMessageItem(text, suffixText, isCheckIn, isAutoContinue) { Images = images ?? [], ResumeAt = _lastEntryUuid }, Now());

    /// <summary>
    /// A prompt from a transcript, sent at <paramref name="sentAt"/>: null when its entry has no time. Its entry's
    /// <paramref name="uuid"/> and <paramref name="parentUuid"/> are where to rewind or branch from it.
    /// </summary>
    public UserMessageItem ReplayUserMessage(string text, IReadOnlyList<MessageImage> images, DateTimeOffset? sentAt, string? uuid = null, string? parentUuid = null)
    {
        var item = AddUser(new UserMessageItem(text) { Images = images, Uuid = uuid, ResumeAt = parentUuid ?? _lastEntryUuid }, sentAt);
        _lastEntryUuid = uuid ?? _lastEntryUuid;
        return item;
    }

    private UserMessageItem AddUser(UserMessageItem item, DateTimeOffset? sentAt)
    {
        CloseOpen();
        Stamp(item, sentAt);
        Items.Add(item);
        return item;
    }

    /// <summary>
    /// Applies an event from a transcript: the messages it adds were sent at <paramref name="sentAt"/> (null when its
    /// entry has no time), not now.
    /// </summary>
    public void Replay(SessionEvent sessionEvent, DateTimeOffset? sentAt)
    {
        _replaying = true;
        _replayTime = sentAt;
        try
        {
            Apply(sessionEvent);
        }
        finally
        {
            _replaying = false;
            _replayTime = null;
        }
    }

    /// <summary>When a message built now was sent: now, or the transcript entry's time during a replay.</summary>
    private DateTimeOffset? Now() =>
        _parent is not null ? _parent.Now()
        : _replaying ? _replayTime
        : Time?.GetUtcNow();

    private TimeProvider? Clock => _parent?.Clock ?? Time;

    private void Stamp(MessageItem item, DateTimeOffset? sentAt)
    {
        if (Clock is { } clock)
        {
            item.Stamp(sentAt, clock);
        }
    }

    public void AddNote(string text, NoteKind kind = NoteKind.Info, string? link = null)
    {
        CloseOpen();
        Items.Add(new NoteItem(text, kind) { Link = link });
    }

    /// <summary>Clears the conversation, for example after <c>/clear</c>.</summary>
    public void Clear()
    {
        Items.Clear();
        _toolUses.Clear();
        _permissions.Clear();
        _hookRuns.Clear();
        _subagents.Clear();
        _todoToolUses.Clear();
        if (_parent is null)
        {
            _todoList?.Clear();
        }
        _agents?.Clear();
        _tasks?.OnConversationCleared();
        _openText = null;
        _openThinking = null;
        _retryNote = null;
        // A cleared conversation is a new one: nothing before it to go back to.
        _lastEntryUuid = null;
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
                if (!_carriedOnNoted && assistant.Message.Raw.GetString("resume_reason") is not null)
                {
                    // A turn Claude Code re-runs after a restart cut it off (DESIGN.md §9, "Working on Claudette").
                    _carriedOnNoted = true;
                    AddNote("Carrying on with the turn the restart cut off.");
                }
                ApplyAssistant(assistant.Message);
                _lastEntryUuid = assistant.Message.Raw.GetString("uuid") ?? _lastEntryUuid;
                break;

            case ToolResultsReceived results:
                ApplyToolResults(results.Message);
                _lastEntryUuid = results.Message.Uuid ?? _lastEntryUuid;
                break;

            case PromptReplayed replayed when replayed.Message.Uuid is { } uuid:
                // A prompt that was sent, echoed back with its id: the id Claudette sent it with. A message Claudette sent
                // without a card of its own (Compact) is no prompt's. Otherwise, as for a Claude Code that made its own
                // id: prompts sent while Claude worked wait their turn, so it's the earliest still without one that reads
                // the same, else the earliest still without one.
                var waiting = Items.OfType<UserMessageItem>().Where(m => m.Uuid is null).ToArray();
                var sent = replayed.Message.Raw.GetObject("message")?["content"] switch
                {
                    JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
                    JsonArray blocks => string.Join("\n", blocks.OfType<JsonObject>().Where(b => b.GetString("type") == "text").Select(b => b.GetString("text"))),
                    _ => null,
                };
                var echoed = waiting.FirstOrDefault(m => m.SentId == uuid)
                    ?? (_sentWithoutCard.Remove(uuid) ? null : waiting.FirstOrDefault(m => sent is not null && m.CopyText.Trim() == sent.Trim()) ?? waiting.FirstOrDefault());
                if (echoed is { } prompt)
                {
                    prompt.Uuid = uuid;
                    prompt.IsQueued = false;
                    // One sent while Claude worked joined the conversation later than it was sent: it follows what came
                    // before it now.
                    prompt.ResumeAt = _lastEntryUuid ?? prompt.ResumeAt;
                }
                _lastEntryUuid = uuid;
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
                _tasks?.OnTask(task.Message, FindToolUse);
                break;

            case ToolProgress progress:
                _agents?.OnToolProgress(progress.Message);
                break;

            case ElicitationRequested elicitation:
                CloseOpen();
                var input = new McpInputItem(elicitation.Request, OpenUrl);
                _mcpInputs[elicitation.Request.RequestId] = input;
                Items.Add(input);
                break;

            case ElicitationCancelled withdrawn:
                if (_mcpInputs.Remove(withdrawn.RequestId, out var withdrawnInput))
                {
                    withdrawnInput.Withdraw(WithdrawnOutcome?.Invoke());
                }
                break;

            case SystemNotice { Message.Subtype: "elicitation_complete" } complete:
                // A URL request the server says is done: the user finished in the browser.
                var elicitationId = complete.Message.Raw.GetString("elicitation_id");
                if (_mcpInputs.Values.FirstOrDefault(i => i.IsUrl && i.Request.ElicitationId == elicitationId && elicitationId is not null) is { } done)
                {
                    done.Complete();
                }
                break;

            case PermissionCancelled cancelled:
                if (_permissions.TryGetValue(cancelled.RequestId, out var cancelledItem))
                {
                    cancelledItem.Cancel(WithdrawnOutcome?.Invoke());
                }
                break;

            case SystemNotice { Message.Subtype: "hook_started" or "hook_progress" or "hook_response" } hook:
                ApplyHook(hook.Message);
                break;

            case SystemNotice { Message.Subtype: "api_retry" } retry:
                ApplyRetry(retry.Message.Raw);
                break;

            case SystemNotice { Message.Subtype: "compact_boundary" } compacted:
                var trigger = compacted.Message.Raw.GetObject("compact_metadata")?.GetString("trigger");
                AddNote(trigger == "auto" ? "Claude Code compacted the conversation to free up context." : "Conversation compacted.");
                break;

            case SystemNotice { Message.Subtype: "informational" } informational:
                // Claude Code's warnings and notices, and hooks' messages to the user (DESIGN.md §5, "Notices"): plain
                // text at its level.
                if (informational.Message.Raw.GetString("content")?.Trim() is { Length: > 0 } notice)
                {
                    AddNote(notice, informational.Message.Raw.GetString("level") == "warning" ? NoteKind.Warning : NoteKind.Info);
                }
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
                _carriedOnNoted = false;
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
                // Claude Code took what was waiting with it.
                foreach (var queued in Items.OfType<UserMessageItem>().Where(m => m.IsQueued))
                {
                    queued.IsQueued = false;
                }
                foreach (var pending in _permissions.Values)
                {
                    pending.Cancel();
                }
                foreach (var pending in _mcpInputs.Values)
                {
                    pending.Withdraw();
                }
                _mcpInputs.Clear();
                var code = exited.Exit.ExitCode?.ToString() ?? "unknown";
                var detail = string.IsNullOrWhiteSpace(exited.Exit.StandardErrorTail) ? "" : $"\n{LastLines(exited.Exit.StandardErrorTail, 5)}";
                AddNote($"Claude Code exited (code {code}).{detail}", exited.Exit.ExitCode == 0 ? NoteKind.Info : NoteKind.Error);
                _agents?.OnSessionExited();
                _tasks?.Clear();
                break;
        }
    }

    /// <summary>
    /// A hook's run (DESIGN.md §5, "Hook runs"): a row from its start when every run shows, else from the moment it fails
    /// or prints something. Its output and how it ended fill in as they come.
    /// </summary>
    private void ApplyHook(SystemMessage message)
    {
        var raw = message.Raw;
        if (raw.GetString("hook_id") is not { Length: > 0 } id)
        {
            return;
        }
        if (!_hookRuns.TryGetValue(id, out var item))
        {
            var failed = message.Subtype == "hook_response" && raw.GetString("outcome") is "error";
            var printed = message.Subtype == "hook_progress" && !string.IsNullOrEmpty(raw.GetString("output") ?? raw.GetString("stdout"));
            if (!ShowAllHookRuns && !failed && !printed)
            {
                return;
            }
            CloseOpen();
            item = new HookRunItem(id, raw.GetString("hook_name") ?? "", raw.GetString("hook_event") ?? "Hook");
            _hookRuns[id] = item;
            Items.Add(item);
        }
        switch (message.Subtype)
        {
            case "hook_progress":
                // Each progress message has the output so far.
                item.Output = "";
                item.Append(HookOutput(raw));
                break;
            case "hook_response":
                item.Output = "";
                item.Append(HookOutput(raw));
                item.ExitCode = raw.GetDouble("exit_code") is { } code ? (int)code : null;
                item.State = raw.GetString("outcome") switch
                {
                    "error" => HookRunState.Failed,
                    "cancelled" => HookRunState.Cancelled,
                    _ => HookRunState.Succeeded,
                };
                // A failure opens, so what went wrong is in view.
                item.IsExpanded = item.IsFailed && item.HasOutput;
                _hookRuns.Remove(id);
                break;
        }

        static string HookOutput(JsonObject raw)
        {
            var output = raw.GetString("output");
            if (!string.IsNullOrEmpty(output))
            {
                return output.TrimEnd();
            }
            var stdout = raw.GetString("stdout") ?? "";
            var stderr = raw.GetString("stderr") ?? "";
            return string.Join('\n', new[] { stdout.TrimEnd(), stderr.TrimEnd() }.Where(s => s.Length > 0));
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
        if (item is PlanItem plan && _parent is null && _todoList is { } todos)
        {
            // An approved plan heads the Tasks page (DESIGN.md §5, "Tasks").
            plan.Answered += (_, _) =>
            {
                if (plan.State == PermissionState.Allowed && plan.HasPlan)
                {
                    todos.SetPlan(plan.Plan.ToString());
                }
            };
        }
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
                    // Tasks are the session's, whichever agent makes them; a subagent's TodoWrite list is its own, and
                    // shows as a card in its group (DESIGN.md §5, "Tasks").
                    if (!(_parent is not null && toolUse.Name == "TodoWrite") && _todoList?.ApplyToolUse(toolUse.Id, toolUse.Name, toolUse.Input) == true)
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
                        var child = new ConversationBuilder(subagent.Items, todoList: null, _modelName) { ExpandThinking = ExpandThinking, _parent = this, _todoList = _todoList };
                        // Its traffic fills its group and its node in the agent map, under this agent.
                        child._agents = _agents;
                        child._tasks = _tasks;
                        child._agent = _agent is not null ? _agents?.Add(_agent, subagent) : null;
                        _subagents[toolUse.Id] = child;
                        _toolUses[toolUse.Id] = subagent;
                        Items.Add(subagent);
                        _tasks?.OnToolUse(subagent);
                    }
                    else
                    {
                        var tool = new ToolUseItem(toolUse.Id, toolUse.Name, toolUse.Input);
                        _toolUses[toolUse.Id] = tool;
                        Items.Add(tool);
                        _tasks?.OnToolUse(tool);
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
            // An error Claude Code also sent as the reply (such as "Not logged in · Please run /login") is shown once,
            // as the error.
            if (Items.Count > 0 && Items[^1] is AssistantTextItem reply && reply.Text.Trim() == error.Trim())
            {
                Items.RemoveAt(Items.Count - 1);
            }
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

    /// <summary>The card of a tool call, in this conversation or any subagent group in it.</summary>
    internal ToolUseItem? FindToolUse(string toolUseId)
    {
        if (_toolUses.TryGetValue(toolUseId, out var direct))
        {
            return direct;
        }
        foreach (var child in _subagents.Values)
        {
            if (child.FindToolUse(toolUseId) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

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
            Stamp(_openText, Now());
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
        _openThinking.Append(text);
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
