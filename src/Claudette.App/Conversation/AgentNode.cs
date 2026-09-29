using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveMarkdown.Avalonia;

namespace Claudette.App.Conversation;

public enum AgentStatus
{
    /// <summary>The main agent between turns.</summary>
    Idle,
    Running,
    /// <summary>A permission prompt from this agent is waiting on the user.</summary>
    Waiting,
    Done,
    Failed,
    Stopped,
}

/// <summary>
/// One agent in a tab's agent map (DESIGN.md §18, "Agent map"): the main agent at the root, or a subagent under the
/// agent whose <c>Agent</c> call started it. <see cref="ConversationBuilder"/> builds it from the same routing that
/// fills the conversation's subagent groups, so the map and the groups can't disagree.
/// </summary>
public sealed partial class AgentNode : ObservableObject
{
    private readonly AgentMap _map;
    private readonly List<PromptItem> _prompts = [];
    private AgentStatus? _outcome;
    private int _seenToolCalls;
    private string? _lastText;
    private bool _working;
    private bool _unfinished;

    internal AgentNode(AgentMap map, AgentNode? parent, SubagentItem? item)
    {
        _map = map;
        Parent = parent;
        Item = item;
        IsLive = !map.IsReplaying;
        if (item is not null)
        {
            AgentType = item.AgentType;
            Prompt = Str(item.Input, "prompt");
            PromptMarkdown = new ObservableStringBuilder(Prompt ?? "");
            IsBackground = item.Input["run_in_background"] is JsonValue background && background.GetValueKind() == JsonValueKind.True;
            if (IsLive)
            {
                StartedAt = map.Now;
            }
        }
        else
        {
            AgentType = "Main agent";
        }
    }

    public AgentNode? Parent { get; }

    /// <summary>The subagent's group in the conversation; null for the main agent.</summary>
    public SubagentItem? Item { get; }

    public bool IsRoot => Item is null;

    public ObservableCollection<AgentNode> Children { get; } = [];

    /// <summary>The <c>Agent</c> tool call that started it.</summary>
    public string? ToolUseId => Item?.ToolUseId;

    /// <summary>Claude Code's id for the running subagent (<c>task_started</c>), which <c>stop_task</c> takes.</summary>
    public string? TaskId { get; private set; }

    /// <summary>Live, rather than read back from a transcript: running time and Stop only mean something then.</summary>
    public bool IsLive { get; }

    /// <summary>The subagent type, for example <c>Explore</c> or <c>general-purpose</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Meta), nameof(Tooltip), nameof(Subtitle))]
    public partial string AgentType { get; private set; }

    /// <summary>The task description: what the row shows first.</summary>
    public string Title => Item is null ? "Main agent" : Str(Item.Input, "description") is { Length: > 0 } description ? description : AgentType;

    /// <summary>The instructions its parent sent it: the <c>prompt</c> input of its <c>Agent</c> call.</summary>
    public string? Prompt { get; }

    public bool HasPrompt => !string.IsNullOrEmpty(Prompt);

    public ObservableStringBuilder? PromptMarkdown { get; }

    /// <summary>Running in the background: the turn can end while it keeps going.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Tooltip), nameof(CanStop))]
    public partial bool IsBackground { get; private set; }

    // ---- Status ------------------------------------------------------------------------------------------------

    public AgentStatus Status => IsRoot
        ? HasWaitingPrompt ? AgentStatus.Waiting : _working ? AgentStatus.Running : AgentStatus.Idle
        : _outcome ?? (HasWaitingPrompt ? AgentStatus.Waiting : AgentStatus.Running);

    /// <summary>Running, or waiting on the user: not finished.</summary>
    public bool IsActive => Status is AgentStatus.Running or AgentStatus.Waiting;

    public bool IsRunning => Status == AgentStatus.Running;

    public bool IsWaiting => Status == AgentStatus.Waiting;

    public bool IsDone => Status == AgentStatus.Done;

    public bool IsFailed => Status == AgentStatus.Failed;

    public bool IsStopped => Status == AgentStatus.Stopped;

    /// <summary>A dot, like the conversation's tool rows (DESIGN.md §3), except where the user should look twice.</summary>
    public string StatusGlyph => Status switch
    {
        AgentStatus.Running => "●",
        AgentStatus.Waiting => "!",
        AgentStatus.Done => "●",
        AgentStatus.Failed => "✕",
        AgentStatus.Stopped => "■",
        _ => "○",
    };

    public string StatusText => Status switch
    {
        AgentStatus.Running => IsBackground ? "Running in the background" : "Running",
        AgentStatus.Waiting => "Waiting on your permission",
        AgentStatus.Done => "Done",
        AgentStatus.Failed => "Failed",
        AgentStatus.Stopped => _unfinished ? "Didn't finish" : "Stopped",
        _ => "Idle",
    };

    /// <summary>
    /// It can be stopped on its own: live, still running, and with a task id for <c>stop_task</c>, or else tied to the
    /// turn, which the whole-turn Stop ends.
    /// </summary>
    public bool CanStop => !IsRoot && IsLive && IsActive && (TaskId is not null || !IsDetachedFromTurn());

    public string StopText => TaskId is not null ? "Stop subagent" : "Stop turn";

    /// <summary>What clicking it does.</summary>
    public string ShowText => IsWaiting ? "Go to the prompt" : "Show in conversation";

    /// <summary>The permission prompt it's waiting on, if any: clicking the node goes there.</summary>
    public PromptItem? WaitingPrompt => _prompts.FirstOrDefault(p => p.IsPending);

    private bool HasWaitingPrompt => WaitingPrompt is not null;

    // ---- What it's doing ---------------------------------------------------------------------------------------

    /// <summary>Its latest tool call, as the conversation summarizes it, or a line of its text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityLine), nameof(Subtitle))]
    public partial string? Activity { get; private set; }

    /// <summary>Waiting out an API error (<c>tool_progress</c> with <c>subagent_retry</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityLine), nameof(Subtitle))]
    public partial string? RetryText { get; private set; }

    /// <summary>The row's second line: the subagent type, then what it's doing or how it ended.</summary>
    public string Subtitle => IsRoot ? ActivityLine : $"{AgentType} · {ActivityLine}";

    /// <summary>What it's doing now, or how it ended.</summary>
    public string ActivityLine => Status switch
    {
        AgentStatus.Waiting => WaitingPrompt is { } prompt ? $"Needs your permission: {Describe(prompt.Request.ToolName, prompt.Request.Input)}" : StatusText,
        AgentStatus.Running => RetryText ?? Activity ?? (IsRoot ? "Working…" : "Starting…"),
        AgentStatus.Done => FirstLine(ResultText) ?? "Done",
        AgentStatus.Failed => FirstLine(ResultText) is { } error ? $"Failed: {error}" : "Failed",
        AgentStatus.Stopped => StatusText,
        _ => Children.Count switch { 0 => "Idle", 1 => "Idle · 1 subagent", var n => $"Idle · {n} subagents" },
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Meta))]
    public partial int ToolCalls { get; private set; }

    /// <summary>Tokens, where Claude Code reports them: the subagent's latest request, not a running total.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Meta))]
    public partial long? Tokens { get; private set; }

    /// <summary>The model it ran on, when Claude Code says (<c>resolvedModel</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Meta))]
    public partial string? Model { get; private set; }

    // ---- Running time (from the injected clock) --------------------------------------------------------------

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>How long Claude Code says it ran; the only running time a restored tab has.</summary>
    public TimeSpan? ReportedDuration { get; private set; }

    public TimeSpan? Elapsed => ReportedDuration is { } reported && !IsActive ? reported
        : StartedAt is { } started ? (EndedAt ?? _map.Now) - started
        : ReportedDuration;

    public string RunningText => Elapsed is { } elapsed ? FormatElapsed(elapsed) : "";

    /// <summary>Ticking: its running time changes every second.</summary>
    internal bool IsTicking => IsLive && StartedAt is not null && EndedAt is null && (IsRoot ? _working : IsActive);

    /// <summary>For the details: type, model, running time, tool calls and tokens.</summary>
    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (!IsRoot)
            {
                parts.Add(AgentType);
                if (Model is { } model)
                {
                    parts.Add(_map.ModelName(model) ?? model);
                }
                if (RunningText is { Length: > 0 } running)
                {
                    parts.Add(running);
                }
                parts.Add(ToolCalls == 1 ? "1 tool call" : $"{ToolCalls} tool calls");
                if (Tokens is { } tokens)
                {
                    parts.Add(TokenTotals.Short(tokens));
                }
            }
            else
            {
                parts.Add(Children.Count == 1 ? "1 subagent" : $"{Children.Count} subagents");
            }
            return string.Join(" · ", parts);
        }
    }

    public string Tooltip => IsRoot ? "The main conversation" : $"{AgentType}: {Title}\n{StatusText}";

    // ---- The result it handed back -----------------------------------------------------------------------------

    /// <summary>What the subagent returned: the tool result its parent received, once it finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ActivityLine), nameof(Subtitle))]
    public partial string? ResultText { get; private set; }

    public bool HasResult => !string.IsNullOrEmpty(ResultText);

    public ObservableStringBuilder ResultMarkdown { get; } = new();

    partial void OnResultTextChanged(string? value)
    {
        ResultMarkdown.Clear();
        if (value is not null)
        {
            ResultMarkdown.Append(value);
        }
    }

    /// <summary>Expanded in the tree.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    // ---- Updates, from the conversation builder and Claude Code's task messages --------------------------------

    internal void OnToolUse(string name, JsonObject input)
    {
        _seenToolCalls++;
        ToolCalls = Math.Max(ToolCalls, _seenToolCalls);
        Activity = Describe(name, input);
        RetryText = null;
    }

    /// <summary>A tool call in one line, as the conversation's tool rows summarize it: <c>Grep auth  in src/</c>.</summary>
    private static string Describe(string name, JsonObject input) =>
        ToolUseItem.Summarize(name, input) is { Length: > 0 } summary ? $"{name} {summary}" : name;

    internal void OnText(string text)
    {
        if (FirstLine(text) is { } line)
        {
            _lastText = text.Trim();
            Activity = line;
            RetryText = null;
        }
    }

    /// <summary>The <c>Agent</c> call's result reached its parent.</summary>
    internal void OnResult(string text, bool isError, JsonNode? toolUseResult, bool interrupted)
    {
        if (toolUseResult is JsonObject result)
        {
            AgentType = result.GetString("agentType") ?? AgentType;
            Model = result.GetString("resolvedModel") ?? Model;
            if (result.GetString("agentId") is { } agentId)
            {
                _map.IndexTask(agentId, this);
            }
            if (result.GetString("status") is "async_launched" or "remote_launched")
            {
                // It keeps going after its parent moves on; a task_notification says how it ended.
                IsBackground = true;
                Refresh();
                return;
            }
            if (result.GetDouble("totalTokens") is { } tokens)
            {
                Tokens = (long)tokens;
            }
            if (result.GetDouble("totalToolUseCount") is { } count)
            {
                ToolCalls = (int)count;
            }
            if (result.GetDouble("totalDurationMs") is { } ms)
            {
                ReportedDuration = TimeSpan.FromMilliseconds(ms);
            }
        }
        // The report its parent got; failing that, the last thing it said, which is what it hands back.
        var report = SubagentItem.Report(text, toolUseResult) is { Length: > 0 } read ? read : null;
        if (!isError)
        {
            Finish(AgentStatus.Done, report ?? _lastText ?? text);
        }
        else if (interrupted || _outcome == AgentStatus.Stopped || IsRejected(text, toolUseResult))
        {
            Finish(AgentStatus.Stopped, null);
        }
        else
        {
            Finish(AgentStatus.Failed, report ?? text);
        }
    }

    internal void OnTaskStarted(string taskId, JsonObject raw)
    {
        TaskId = taskId;
        if (raw.GetBool("is_backgrounded") == true)
        {
            IsBackground = true;
        }
        if (Item?.Input["subagent_type"] is null && raw.GetString("subagent_type") is { } type)
        {
            AgentType = type;
        }
        Refresh();
    }

    internal void OnTaskProgress(JsonObject raw)
    {
        ApplyUsage(raw.GetObject("usage"), duration: false);
        // Claude Code's own one-line description of what it's doing, when the stream hasn't shown a tool call.
        if (Activity is null && raw.GetString("description") is { Length: > 0 } description)
        {
            Activity = description;
        }
    }

    /// <summary>A task ended: <c>completed</c>, <c>failed</c> or <c>stopped</c>. The summary is its report when it completed.</summary>
    internal void OnTaskEnded(string? status, string? summary, JsonObject? usage)
    {
        ApplyUsage(usage, duration: true);
        switch (status)
        {
            case "completed" when _outcome is null:
                Finish(AgentStatus.Done, _lastText ?? summary);
                break;
            case "failed":
                Finish(AgentStatus.Failed, ResultText ?? summary);
                break;
            case "stopped" or "killed":
                Finish(AgentStatus.Stopped, null);
                break;
        }
    }

    internal void OnTaskUpdated(JsonObject patch)
    {
        if (patch.GetBool("is_backgrounded") == true)
        {
            IsBackground = true;
        }
        switch (patch.GetString("status"))
        {
            case "killed":
                Finish(AgentStatus.Stopped, null);
                break;
            case "failed":
                Finish(AgentStatus.Failed, ResultText ?? patch.GetString("error"));
                break;
        }
    }

    internal void OnRetry(JsonObject? retry)
    {
        if (retry is null)
        {
            RetryText = null;
            return;
        }
        var reason = retry.GetString("error_category") switch
        {
            "rate_limit" => "a rate limit",
            "overloaded" => "the API being overloaded",
            "authentication_failed" => "a sign-in problem",
            "server_error" or "cloud_credential_error" => "an API error",
            _ => "an API error",
        };
        var attempt = retry.GetDouble("attempt") is { } a ? $" (attempt {a:0}{(retry.GetDouble("max_retries") is { } max ? $" of {max:0}" : "")})" : "";
        RetryText = $"Retrying after {reason}{attempt}…";
    }

    internal void AddPrompt(PromptItem prompt)
    {
        _prompts.Add(prompt);
        prompt.PropertyChanged += OnPromptChanged;
        for (var parent = Parent; parent is not null; parent = parent.Parent)
        {
            parent.IsExpanded = true;
        }
        Refresh();
    }

    internal void SetWorking(bool working)
    {
        if (_working == working)
        {
            return;
        }
        _working = working;
        if (IsLive)
        {
            if (working)
            {
                StartedAt = _map.Now;
                EndedAt = null;
            }
            else if (StartedAt is not null)
            {
                EndedAt = _map.Now;
            }
        }
        Refresh();
    }

    /// <summary>Ends it, unless it already has: <paramref name="result"/> fills in the report if there isn't one yet.</summary>
    internal void Finish(AgentStatus outcome, string? result, bool unfinished = false)
    {
        if (IsRoot)
        {
            return;
        }
        // A stop is final: the error result that follows it isn't a failure.
        if (_outcome == AgentStatus.Stopped && outcome == AgentStatus.Failed)
        {
            return;
        }
        _outcome = outcome;
        _unfinished = unfinished;
        if (outcome != AgentStatus.Stopped && !string.IsNullOrWhiteSpace(result) && (ResultText is null || outcome == AgentStatus.Done))
        {
            ResultText = result.Trim();
        }
        if (IsLive && EndedAt is null)
        {
            EndedAt = _map.Now;
        }
        Item?.ShowEnded(outcome, ResultText);
        RetryText = null;
        Refresh();
    }

    /// <summary>Running time changed (every second while it runs).</summary>
    internal void Tick()
    {
        OnPropertyChanged(nameof(RunningText));
        OnPropertyChanged(nameof(Meta));
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActivityLine));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(WaitingPrompt));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(StopText));
        OnPropertyChanged(nameof(ShowText));
        Tick();
        _map.NotifyChanged();
    }

    internal void ApplyUsage(JsonObject? usage, bool duration)
    {
        if (usage is null)
        {
            return;
        }
        if (usage.GetDouble("total_tokens") is { } tokens)
        {
            Tokens = (long)tokens;
        }
        if (usage.GetDouble("tool_uses") is { } uses)
        {
            ToolCalls = Math.Max(_seenToolCalls, (int)uses);
        }
        if (duration && usage.GetDouble("duration_ms") is { } ms)
        {
            ReportedDuration = TimeSpan.FromMilliseconds(ms);
        }
    }

    internal void ApplyReported(long? tokens, int? toolUses, long? durationMs)
    {
        Tokens = tokens ?? Tokens;
        ToolCalls = toolUses is { } uses ? Math.Max(_seenToolCalls, uses) : ToolCalls;
        ReportedDuration = durationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : ReportedDuration;
    }

    internal void SetTaskId(string taskId) => TaskId ??= taskId;

    internal IEnumerable<AgentNode> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.DescendantsAndSelf())
            {
                yield return node;
            }
        }
    }

    /// <summary>It, or an agent above it, runs in the background, so it isn't tied to the turn.</summary>
    internal bool IsDetachedFromTurn()
    {
        for (var node = this; node is not null; node = node.Parent)
        {
            if (node.IsBackground)
            {
                return true;
            }
        }
        return false;
    }

    private void OnPromptChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PromptItem.State))
        {
            Refresh();
        }
    }

    /// <summary>
    /// Claude Code rejects the call of a subagent that was stopped, or whose parent was ("User rejected tool use"),
    /// and records an interrupted one as "[Request interrupted by user…]".
    /// </summary>
    private static bool IsRejected(string text, JsonNode? toolUseResult) =>
        toolUseResult is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Contains("rejected", StringComparison.OrdinalIgnoreCase)
        || text.TrimStart().StartsWith("[Request interrupted", StringComparison.Ordinal);

    internal static string FormatElapsed(TimeSpan span) => span switch
    {
        { TotalSeconds: < 1 } => "<1s",
        { TotalMinutes: < 1 } => $"{(int)span.TotalSeconds}s",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes}m {span.Seconds:00}s",
        _ => $"{(int)span.TotalHours}h {span.Minutes:00}m",
    };

    internal static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 140 ? line[..137] + "…" : line;
    }

    private static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
