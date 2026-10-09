using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Claudette.Core.Diffs;
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
    private readonly PendingPrompts _prompts = new();
    private readonly HookRuns _hookRuns;
    private readonly Dictionary<string, ConversationBuilder> _subagents = [];
    private readonly HashSet<string> _todoToolUses = [];
    private TodoList? _todoList;
    private readonly Func<string?, string?> _modelName;
    private readonly StreamingBlocks _blocks;
    private NoteItem? _retryNote;

    // The plan's draft (DESIGN.md §5, "Tasks"): Write and Edit calls to the plan file in Plan mode, until their results.
    private readonly Dictionary<string, (string Name, JsonObject Input)> _draftCalls = [];

    // File changes and the task in progress they count toward, until their results.
    private readonly Dictionary<string, (TodoItem Task, string Path)> _taskFiles = [];

    // The task list changed in the turn under way, so its end says where the list stands. The main agent's builder keeps it.
    private bool _tasksChangedThisTurn;

    // The last entry of the main conversation Claude Code gave an id: where a resume would stop to leave out what comes
    // next (DESIGN.md §5, "Rewind and branch").
    private string? _lastEntryUuid;
    private AgentMap? _agents;
    private RunningTasks? _tasks;

    // The agent whose stream this builder shows: the main agent, or the subagent whose group it fills.
    private AgentNode? _agent;

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
        _blocks = new StreamingBlocks(items, item => Stamp(item, Now()), () => ExpandThinking);
        _hookRuns = new HookRuns(run =>
        {
            _blocks.Close();
            Items.Add(run);
        });
    }

    public ObservableCollection<ConversationItem> Items { get; }

    /// <summary>
    /// Start new thinking rows expanded rather than collapsed (Settings → Appearance, or the tab's own choice). A
    /// subagent's builder asks the builder that made it, so a group already running follows a change.
    /// </summary>
    public bool ExpandThinking
    {
        get => _parent?.ExpandThinking ?? _expandThinking;
        set => _expandThinking = value;
    }

    private bool _expandThinking;

    /// <summary>
    /// <b>Collapse all thinking</b> or <b>Expand all thinking</b> (DESIGN.md §5): every thinking row, subagents' groups'
    /// too, and the ones still to come.
    /// </summary>
    public void ExpandAllThinking(bool expanded)
    {
        ExpandThinking = expanded;
        Expand(Items);

        void Expand(IEnumerable<ConversationItem> items)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case ThinkingItem thinking:
                        thinking.IsExpanded = expanded;
                        break;
                    case SubagentItem subagent:
                        Expand(subagent.Items);
                        break;
                }
            }
        }
    }

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
    public bool ShowAllHookRuns
    {
        get => _hookRuns.ShowAll;
        set => _hookRuns.ShowAll = value;
    }

    /// <summary>
    /// What a prompt Claude Code withdraws says: "Answered in the Claude app" while the tab is connected to it (DESIGN.md
    /// §18, "Remote Control"). Null, or no function, keeps <see cref="PromptItem.WithdrawnOutcome"/>.
    /// </summary>
    public Func<string?>? WithdrawnOutcome
    {
        get => _prompts.WithdrawnOutcome;
        set => _prompts.WithdrawnOutcome = value;
    }

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

    /// <summary>
    /// The tab is in Plan mode, where Claude Code lets Claude change only its plan file: the main agent's Write or Edit to
    /// a Markdown file is the plan's draft (DESIGN.md §5, "Tasks").
    /// </summary>
    public Func<bool>? IsInPlanMode { get; set; }

    /// <summary>The row the latest turn's end added about the task list; null when it added none.</summary>
    public TasksSummaryItem? TurnTasksSummary { get; private set; }

    private bool IsReplaying => _parent?.IsReplaying ?? _replaying;

    private ConversationBuilder Root => _parent?.Root ?? this;

    /// <summary>This agent's builder and those above it, up to the main agent's: whose task its work counts toward.</summary>
    private List<object> AgentChain()
    {
        var chain = new List<object>();
        for (var builder = this; builder is not null; builder = builder._parent)
        {
            chain.Add(builder);
        }
        return chain;
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
        _blocks.Close();
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
        _blocks.Close();
        Items.Add(new NoteItem(text, kind) { Link = link });
    }

    /// <summary>Clears the conversation, for example after <c>/clear</c>.</summary>
    public void Clear()
    {
        Items.Clear();
        _toolUses.Clear();
        _prompts.OnConversationCleared();
        _hookRuns.Clear();
        _subagents.Clear();
        _todoToolUses.Clear();
        if (_parent is null)
        {
            _todoList?.Clear();
        }
        _agents?.Clear();
        _tasks?.OnConversationCleared();
        _blocks.Clear();
        _retryNote = null;
        _draftCalls.Clear();
        _taskFiles.Clear();
        _tasksChangedThisTurn = false;
        TurnTasksSummary = null;
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
                _blocks.OnTextDelta(delta.Text);
                break;

            case ThinkingDelta delta:
                _blocks.OnThinkingDelta(delta.Text);
                break;

            case AssistantMessageReceived assistant:
                if (!_carriedOnNoted && assistant.Message.ResumeReason is not null)
                {
                    // A turn Claude Code re-runs after a restart cut it off (DESIGN.md §9, "Working on Claudette").
                    _carriedOnNoted = true;
                    AddNote("Carrying on with the turn the restart cut off.");
                }
                ApplyAssistant(assistant.Message);
                _lastEntryUuid = assistant.Message.Uuid ?? _lastEntryUuid;
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
                var sent = replayed.Message.PromptText;
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
                _blocks.Close();
                Items.Add(_prompts.Add(elicitation.Request, OpenUrl));
                break;

            case ElicitationCancelled withdrawn:
                _prompts.WithdrawMcpInput(withdrawn.RequestId);
                break;

            case SystemNotice { Message.ElicitationComplete: { } complete }:
                _prompts.CompleteMcpInput(complete.ElicitationId);
                break;

            case PermissionCancelled cancelled:
                _prompts.CancelPermission(cancelled.RequestId);
                break;

            case SystemNotice { Message.HookRun: { } hook }:
                _hookRuns.Apply(hook);
                break;

            case SystemNotice { Message.ApiRetry: { } retry }:
                ApplyRetry(retry);
                break;

            case SystemNotice { Message.CompactBoundary: { } compacted }:
                AddNote(compacted.IsAutomatic ? "Claude Code compacted the conversation to free up context." : "Conversation compacted.");
                break;

            case SystemNotice { Message.Informational: { } informational }:
                // Claude Code's warnings and notices, and hooks' messages to the user (DESIGN.md §5, "Notices"): plain
                // text at its level.
                if (informational.Content?.Trim() is { Length: > 0 } notice)
                {
                    AddNote(notice, informational.Level == NoticeLevel.Warning ? NoteKind.Warning : NoteKind.Info);
                }
                break;

            case SystemNotice { Message.PermissionDenied: { } denied }:
                // Denied without asking, by a rule or the permission mode.
                var tool = denied.ToolName ?? "a tool";
                AddNote(denied.Reason is { } why ? $"Claude Code denied {tool}: {why}" : $"Claude Code denied {tool}.", NoteKind.Warning);
                break;

            case TurnCompleted completed:
                _blocks.Close();
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
                _blocks.Close();
                // Claude Code took what was waiting with it.
                foreach (var queued in Items.OfType<UserMessageItem>().Where(m => m.IsQueued))
                {
                    queued.IsQueued = false;
                }
                _prompts.OnSessionExited();
                _todoList?.EndWaitingPlans();
                _tasksChangedThisTurn = false;
                var code = exited.Exit.ExitCode?.ToString() ?? "unknown";
                var detail = string.IsNullOrWhiteSpace(exited.Exit.StandardErrorTail) ? "" : $"\n{LastLines(exited.Exit.StandardErrorTail, 5)}";
                AddNote($"Claude Code exited (code {code}).{detail}", exited.Exit.ExitCode == 0 ? NoteKind.Info : NoteKind.Error);
                _agents?.OnSessionExited();
                _tasks?.Clear();
                break;
        }
    }

    /// <summary>
    /// A permission prompt, a clarifying question or a plan to approve (DESIGN.md §7). A question or plan replaces its
    /// own tool row, which would only repeat it.
    /// </summary>
    private void AddPrompt(PermissionRequest request)
    {
        _blocks.Close();
        var item = _prompts.Add(request);
        if (item is not PermissionItem && request.ToolUseId is { } id && _toolUses.TryGetValue(id, out var row))
        {
            Items.Remove(row);
        }
        if (item is PlanItem plan && _parent is null && _todoList?.OnPlanProposed(request.ToolUseId, plan.HasPlan ? plan.Plan.ToString() : null) is { } version)
        {
            // A version of the plan (DESIGN.md §5, "Tasks"): its number and changes on its card, and the draft's text
            // when the request had none.
            plan.SetVersion(version);
        }
        Items.Add(item);
        _agents?.OnPrompt(item);
    }

    private void ApplyAssistant(AssistantMessage message)
    {
        if (_todoList is not null && message.MessageId is { } callId && CallUsage.TokensOf(message) is { } tokens)
        {
            // The call's tokens count toward the task in progress (DESIGN.md §5, "What each task did").
            _todoList.CountCall(callId, tokens, _todoList.TaskFor(AgentChain()));
        }
        var (streamedText, streamedThinking) = _blocks.TakeStreamed();
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case ThinkingBlock thinking:
                    if (!streamedThinking)
                    {
                        _blocks.AppendThinking(thinking.Thinking);
                    }
                    _blocks.CloseThinking();
                    break;

                case TextBlock text:
                    _blocks.CloseThinking();
                    if (!streamedText)
                    {
                        _blocks.AppendText(text.Text);
                    }
                    _blocks.CloseText();
                    _agent?.OnText(text.Text);
                    break;

                case ToolUseBlock toolUse:
                    _blocks.Close();
                    // Tasks are the session's, whichever agent makes them; a subagent's TodoWrite list is its own, and
                    // shows as a card in its group (DESIGN.md §5, "Tasks").
                    if (_todoList is not null && !(_parent is not null && toolUse.Name == "TodoWrite"))
                    {
                        var startsNewList = toolUse.Name is "TodoWrite" or "TaskCreate" && _todoList.StartsNewList;
                        if (_todoList.ApplyToolUse(toolUse.Id, toolUse.Name, toolUse.Input, agent: this))
                        {
                            // To-do updates show in the pinned list, not as cards; a task that starts gets a row here.
                            _todoToolUses.Add(toolUse.Id);
                            foreach (var started in _todoList.Started.ToArray())
                            {
                                AddTaskStart(started);
                            }
                            if (!IsReplaying)
                            {
                                // Reading the list changes nothing.
                                Root._tasksChangedThisTurn |= toolUse.Name is not ("TaskList" or "TaskGet");
                                if (startsNewList && _parent is null && _todoList.HasItems)
                                {
                                    SuggestPlan();
                                }
                            }
                            break;
                        }
                    }
                    if (_agents is not null && _agent is not null)
                    {
                        _agents.OnToolUse(_agent, toolUse.Id, toolUse.Name, toolUse.Input);
                    }
                    if (toolUse.Name is "Agent" or "Task")
                    {
                        var subagent = new SubagentItem(toolUse.Id, toolUse.Name, toolUse.Input);
                        var child = new ConversationBuilder(subagent.Items, todoList: null, _modelName) { _parent = this, _todoList = _todoList };
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
                    TrackPlanDraft(toolUse);
                    TrackTaskFile(toolUse);
                    break;
            }
        }
    }

    /// <summary>A task went in progress: a row where it starts, unless it had one already (DESIGN.md §5, "Tasks").</summary>
    private void AddTaskStart(TodoItem task)
    {
        if (task.Start is not null)
        {
            return;
        }
        var row = new TaskStartItem(task);
        task.Start = row;
        Items.Add(row);
    }

    /// <summary>
    /// Claude started a new task list: its latest reply is offered as the plan when it reads like one and is newer than
    /// the newest plan (DESIGN.md §5, "Suggesting a reply as the plan").
    /// </summary>
    private void SuggestPlan()
    {
        if (Items.OfType<AssistantTextItem>().LastOrDefault(r => !r.IsStreaming) is not { } reply || !TodoList.LooksLikePlan(reply.Text))
        {
            return;
        }
        if (_todoList!.NewestPlan is { } plan && (plan.At is not { } at || reply.SentAt is not { } sent || sent <= at))
        {
            return;
        }
        _todoList.Suggest(reply);
    }

    /// <summary>The main agent's Write or Edit to a Markdown file in Plan mode: the plan's draft, once it's made. Only live.</summary>
    private void TrackPlanDraft(ToolUseBlock toolUse)
    {
        if (_parent is null && !_replaying && _todoList is not null && toolUse.Name is "Write" or "Edit"
            && toolUse.Input.GetString("file_path") is { } path && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && IsInPlanMode?.Invoke() == true)
        {
            _draftCalls[toolUse.Id] = (toolUse.Name, toolUse.Input);
        }
    }

    /// <summary>A file change counts toward the task in progress, once it's made (DESIGN.md §5, "What each task did").</summary>
    private void TrackTaskFile(ToolUseBlock toolUse)
    {
        if (_todoList is not null && ChangedFiles.IsFileTool(toolUse.Name)
            && (toolUse.Input.GetString("file_path") ?? toolUse.Input.GetString("notebook_path")) is { } path
            && _todoList.TaskFor(AgentChain()) is { } task)
        {
            _taskFiles[toolUse.Id] = (task, path);
        }
    }

    /// <summary>
    /// The plan as a Write or Edit left it: a Write's content, or an Edit's replacement made to the file as its result had
    /// it, else to the draft so far. Null when it can't be told.
    /// </summary>
    private string? DraftAfter((string Name, JsonObject Input) call, JsonNode? toolUseResult)
    {
        if (call.Name == "Write")
        {
            return call.Input.GetString("content");
        }
        var before = (toolUseResult as JsonObject)?.GetString("originalFile") ?? _todoList?.DraftText;
        if (before is null || call.Input.GetString("old_string") is not { Length: > 0 } old || call.Input.GetString("new_string") is not { } replacement
            || before.IndexOf(old, StringComparison.Ordinal) is var at && at < 0)
        {
            return null;
        }
        return call.Input.GetBool("replace_all") == true
            ? before.Replace(old, replacement, StringComparison.Ordinal)
            : string.Concat(before.AsSpan(0, at), replacement, before.AsSpan(at + old.Length));
    }

    private void ApplyToolResults(UserMessage message)
    {
        foreach (var result in message.Content.OfType<ToolResultBlock>())
        {
            if (_draftCalls.Remove(result.ToolUseId, out var draft) && !result.IsError && DraftAfter(draft, message.ToolUseResult) is { } text)
            {
                _todoList?.OnPlanDrafted(text);
            }
            if (_taskFiles.Remove(result.ToolUseId, out var credited) && !result.IsError)
            {
                credited.Task.AddFile(credited.Path, result.ToolUseId);
            }
            if (_todoToolUses.Remove(result.ToolUseId))
            {
                _todoList?.ApplyToolResult(result.ToolUseId, result.Text, message.ToolUseResult);
            }
            else if (_toolUses.TryGetValue(result.ToolUseId, out var tool))
            {
                tool.ApplyResult(result.Text, result.IsError, message.ToolUseResult);
                if (tool is SubagentItem && _agents?.Find(result.ToolUseId) is { } node)
                {
                    node.OnResult(result.Text, result.IsError, message.ToolUseResult, message.WasInterrupted(result.ToolUseId));
                }
                if (tool.Name == "ExitPlanMode" && _parent is null && _todoList is not null)
                {
                    // What became of a version of the plan (DESIGN.md §5, "Tasks"). Its result says so however it was
                    // answered: on its card, in the Claude app, or before the tab was restored.
                    var card = Items.OfType<PlanItem>().LastOrDefault(p => p.Request.ToolUseId == result.ToolUseId);
                    _todoList.OnPlanAnswered(result.ToolUseId, !result.IsError,
                        result.IsError ? tool.Input.GetString("plan") : ApprovedPlan(tool, message.ToolUseResult),
                        card?.SentBackFeedback,
                        interrupted: result.IsError && message.WasInterrupted(result.ToolUseId));
                }
            }
        }
    }

    /// <summary>The plan an approved ExitPlanMode result carries, else the one the call was given; null without either.</summary>
    private static string? ApprovedPlan(ToolUseItem tool, JsonNode? toolUseResult)
    {
        var plan = (toolUseResult as JsonObject)?.GetString("plan") ?? tool.Input.GetString("plan");
        return string.IsNullOrWhiteSpace(plan) ? null : plan;
    }

    /// <summary>One note per retry sequence, updated in place rather than one row per attempt.</summary>
    private void ApplyRetry(ApiRetryNotice retry)
    {
        var (attempt, max, delay, reason) = (retry.Attempt, retry.MaxRetries, retry.RetryDelayMs, retry.Category);
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
            _blocks.Close();
            _retryNote = new NoteItem(text, NoteKind.Warning);
            Items.Add(_retryNote);
        }
    }

    private void ApplyTurnCompleted(ResultMessage result)
    {
        var tasksChanged = _tasksChangedThisTurn;
        _tasksChangedThisTurn = false;
        TurnTasksSummary = null;
        // A plan still waiting can't be answered once its turn is over.
        _todoList?.EndWaitingPlans();
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
        if (tasksChanged && _todoList?.TurnSummaryText(Now()) is { } tasks)
        {
            // Where the task list stands (DESIGN.md §5, "Tasks"), before the footer.
            TurnTasksSummary = new TasksSummaryItem(tasks, _todoList.AllDone);
            Items.Add(TurnTasksSummary);
        }
        if (TurnSummaryItem.For(result, _modelName) is { } summary)
        {
            Items.Add(summary);
        }
    }

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

    private static string LastLines(string text, int count) =>
        string.Join('\n', text.Split('\n').TakeLast(count));
}
