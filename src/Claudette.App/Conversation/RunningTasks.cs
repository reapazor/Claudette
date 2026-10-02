using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.Conversation;

/// <summary>What a running task is, for its icon and label (DESIGN.md §5, "Running tasks").</summary>
public enum TaskKind
{
    /// <summary>A <c>task_type</c> Claudette doesn't know yet.</summary>
    Unknown,

    /// <summary>A shell command in the background (<c>local_bash</c>).</summary>
    Shell,

    /// <summary>A Monitor watch: also <c>local_bash</c>, told apart by the tool call that started it.</summary>
    Monitor,

    /// <summary>A subagent in the background (<c>local_agent</c>).</summary>
    Agent,

    /// <summary>A cloud session (<c>remote_agent</c>).</summary>
    Remote,

    /// <summary>A dynamic workflow (<c>local_workflow</c>).</summary>
    Workflow,
}

/// <summary>
/// One task Claude Code reported with <c>task_started</c> (DESIGN.md §5, "Running tasks"). Only a task in the
/// background counts as running: a foreground one is part of the turn.
/// </summary>
public sealed partial class RunningTask : ObservableObject
{
    private readonly RunningTasks _owner;
    private bool? _backgrounded;
    private string? _subagentType;

    // The call that started it, kept when its card goes (after /clear), since they say what kind of task it is.
    private string? _toolName;
    private JsonObject? _toolInput;

    internal RunningTask(RunningTasks owner, string taskId, string? toolUseId)
    {
        _owner = owner;
        TaskId = taskId;
        ToolUseId = toolUseId;
        StartedAt = owner.Now;
    }

    /// <summary>Claude Code's id for it, which <c>stop_task</c> takes.</summary>
    public string TaskId { get; }

    /// <summary>The tool call that started it, whose card the row goes to.</summary>
    public string? ToolUseId { get; }

    /// <summary><c>task_type</c>: <c>local_bash</c>, <c>local_agent</c>, <c>remote_agent</c>, …</summary>
    public string? TaskType { get; private set; }

    /// <summary>When Claudette heard it start, from the injected clock.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Claude Code does this for its own operation (<c>ambient</c>), such as a live-update watcher: the SDK says to
    /// leave these out of activity indicators.
    /// </summary>
    internal bool IsAmbient { get; private set; }

    /// <summary>It completed, failed, or was stopped or killed.</summary>
    internal bool IsEnded { get; private set; }

    /// <summary>The card of the call that started it: a Bash or Monitor call, or a subagent's group.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind), nameof(IconKey), nameof(KindText), nameof(Title), nameof(Tooltip), nameof(CanShow))]
    public partial ToolUseItem? Tool { get; internal set; }

    partial void OnToolChanged(ToolUseItem? value)
    {
        if (value is not null)
        {
            _toolName = value.Name;
            _toolInput = value.Input;
        }
    }

    /// <summary>For a subagent: its node in the agent map, which says whether it runs in the background and when it ends.</summary>
    public AgentNode? Agent { get; internal set; }

    /// <summary>What Claude Code calls it: the call's <c>description</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Tooltip))]
    public partial string? Description { get; private set; }

    public TaskKind Kind => _toolName == "Monitor" ? TaskKind.Monitor : TaskType switch
    {
        "local_bash" => TaskKind.Shell,
        "local_agent" => TaskKind.Agent,
        "remote_agent" => TaskKind.Remote,
        "local_workflow" => TaskKind.Workflow,
        _ => TaskKind.Unknown,
    };

    /// <summary>A vector icon in App.axaml, like the tool cards'.</summary>
    public string IconKey => IconFor(Kind);

    public static string IconFor(TaskKind kind) => kind switch
    {
        TaskKind.Shell => "IconToolBash",
        TaskKind.Monitor => "IconToolMonitor",
        TaskKind.Agent or TaskKind.Workflow => "IconToolAgent",
        TaskKind.Remote => "IconToolRemote",
        _ => ToolIcons.Default,
    };

    public string KindText => Kind switch
    {
        TaskKind.Shell => "Shell command",
        TaskKind.Monitor => "Monitor",
        TaskKind.Agent => "Subagent",
        TaskKind.Remote => "Remote agent",
        TaskKind.Workflow => "Workflow",
        _ => "Task",
    };

    /// <summary>Its description, else the command, else the subagent type.</summary>
    public string Title =>
        Description is { Length: > 0 } description ? OneLine(description)
        : Str(_toolInput, "command") is { Length: > 0 } command ? OneLine(command)
        : (Agent?.AgentType ?? _subagentType) is { Length: > 0 } type ? type
        : KindText;

    /// <summary>The kind, the title and the command in full.</summary>
    public string Tooltip
    {
        get
        {
            var tip = $"{KindText}: {Title}";
            if (Str(_toolInput, "command") is { Length: > 0 } command && OneLine(command) != Title)
            {
                tip += $"\n{command.Trim()}";
            }
            return CanShow ? $"{tip}\nClick to show it in the conversation." : tip;
        }
    }

    /// <summary>How long it's been running, from the injected clock.</summary>
    public string RunningText => AgentNode.FormatElapsed(_owner.Now - StartedAt);

    /// <summary>Its card is in the conversation to go to.</summary>
    public bool CanShow => Tool is not null;

    /// <summary>
    /// Running in the background. <c>is_backgrounded</c> says so for Bash and subagents (and a <c>task_updated</c> when a
    /// foreground one moves there); a Monitor watch always does; other kinds, such as a remote agent, are background
    /// work unless Claude Code says otherwise. Without the field, the call's own <c>run_in_background</c> decides.
    /// </summary>
    internal bool IsBackground => Kind switch
    {
        TaskKind.Monitor => true,
        TaskKind.Agent => _backgrounded == true || Agent?.IsBackground == true || _backgrounded is null && RunInBackground,
        TaskKind.Shell => _backgrounded ?? RunInBackground,
        _ => _backgrounded != false,
    };

    /// <summary>Counts as a running task: in the background, not Claude Code's own, and not over.</summary>
    internal bool IsRunning => !IsEnded && !IsAmbient && IsBackground && Agent is not { IsLive: true, IsActive: false };

    private bool RunInBackground => _toolInput?["run_in_background"] is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    internal void OnStarted(JsonObject raw)
    {
        TaskType = raw.GetString("task_type") ?? TaskType;
        _backgrounded = raw.GetBool("is_backgrounded") ?? _backgrounded;
        IsAmbient = raw.GetBool("ambient") ?? IsAmbient;
        _subagentType = raw.GetString("subagent_type") ?? _subagentType;
        Description = raw.GetString("description") ?? Description;
        Refresh();
    }

    /// <summary>
    /// A <c>task_updated</c> patch: it moved to the background (<c>is_backgrounded</c>), was renamed, or ended. Other
    /// statuses (<c>pending</c>, <c>running</c>, or ones Claude Code adds later) leave it running.
    /// </summary>
    internal void OnUpdated(JsonObject patch)
    {
        _backgrounded = patch.GetBool("is_backgrounded") ?? _backgrounded;
        Description = patch.GetString("description") ?? Description;
        if (RunningTasks.IsTerminal(patch.GetString("status")))
        {
            IsEnded = true;
        }
        Refresh();
    }

    internal void End() => IsEnded = true;

    /// <summary>Its running time changed (every second while the list is open).</summary>
    internal void Tick() => OnPropertyChanged(nameof(RunningText));

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Tooltip));
    }

    private static string OneLine(string text) => AgentNode.FirstLine(text) ?? text.Trim();

    private static string? Str(JsonObject? obj, string name) => obj?.GetString(name);
}

/// <summary>
/// A tab's running tasks (DESIGN.md §5, "Running tasks"): the work Claude Code keeps going in the background, such as a
/// backgrounded shell command, a background subagent, a Monitor watch or a remote agent, from its task messages
/// (<c>task_started</c>, <c>task_updated</c>, <c>task_notification</c>). Kept by the <see cref="ConversationBuilder"/>
/// beside the <see cref="AgentMap"/>, whose nodes it follows for subagents so the two agree. It also knows every task's
/// id by its tool call, for the process monitor's Stop (DESIGN.md §4). Must be used on the UI thread.
/// </summary>
public sealed class RunningTasks
{
    private readonly TimeProvider _time;
    private readonly AgentMap? _agents;
    private readonly Dictionary<string, RunningTask> _byId = [];
    private readonly Dictionary<string, RunningTask> _byToolUse = [];

    /// <summary>The tasks that haven't ended, in the order they started: the only ones that can be running.</summary>
    private readonly List<RunningTask> _open = [];

    public RunningTasks(TimeProvider time, AgentMap? agents = null)
    {
        _time = time;
        _agents = agents;
        if (agents is not null)
        {
            // A subagent's node can move it to the background (its call returned async_launched) or end it.
            agents.Changed += () => Update(notify: false);
        }
    }

    /// <summary>The tasks running now, in the order they started: what the tab's row counts once the turn is over.</summary>
    public ObservableCollection<RunningTask> Running { get; } = [];

    public int Count => Running.Count;

    /// <summary>
    /// The running tasks the composer bar's chip and the info card list: all but subagents, which the Agents button and
    /// the Agents page show, with their own Stop (DESIGN.md §18, "Agent map").
    /// </summary>
    public ObservableCollection<RunningTask> Listed { get; } = [];

    /// <summary>How many running tasks are subagents.</summary>
    public int AgentCount => Count - Listed.Count;

    /// <summary>The running tasks, or something the list or the info card shows about them, changed.</summary>
    public event Action? Changed;

    internal DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>For the tab info card: one line per listed task, or null when none are. Its Agents row has the subagents.</summary>
    public string? Summary
    {
        get
        {
            if (Listed.Count == 0)
            {
                return null;
            }
            const int shown = 4;
            var lines = Listed.Take(shown).Select(t => $"{t.KindText}: {t.Title}").ToList();
            if (Listed.Count > shown)
            {
                lines.Add($"and {Listed.Count - shown} more");
            }
            return string.Join('\n', lines);
        }
    }

    public RunningTask? Find(string taskId) => _byId.GetValueOrDefault(taskId);

    /// <summary>The id of the task a tool call started (running or not), for <c>stop_task</c>.</summary>
    public string? TaskIdFor(string toolUseId) => _byToolUse.GetValueOrDefault(toolUseId)?.TaskId;

    /// <summary>A task status that means it's over.</summary>
    internal static bool IsTerminal(string? status) => status is "completed" or "failed" or "stopped" or "killed";

    /// <summary>
    /// Claude Code's task messages. <paramref name="findTool"/> finds the card of the call that started a task, at any
    /// depth; a call that shows up later is matched in <see cref="OnToolUse"/>.
    /// </summary>
    internal void OnTask(SystemMessage message, Func<string, ToolUseItem?> findTool)
    {
        var raw = message.Raw;
        if (raw.GetString("task_id") is not { } taskId)
        {
            return;
        }
        var task = _byId.GetValueOrDefault(taskId);
        switch (message.Subtype)
        {
            case "task_started":
                if (task is null)
                {
                    task = new RunningTask(this, taskId, raw.GetString("tool_use_id"));
                    _byId[taskId] = task;
                    _open.Add(task);
                    if (task.ToolUseId is { } toolUseId)
                    {
                        _byToolUse[toolUseId] = task;
                        task.Tool = findTool(toolUseId);
                    }
                }
                task.OnStarted(raw);
                break;
            case "task_updated" when task is not null && raw.GetObject("patch") is { } patch:
                task.OnUpdated(patch);
                break;
            case "task_notification" when task is not null && IsTerminal(raw.GetString("status")):
                task.End();
                _open.Remove(task);
                break;
            default:
                return;
        }
        Update(notify: true);
    }

    /// <summary>A tool call appeared in the conversation: the card for a task that started before it showed.</summary>
    internal void OnToolUse(ToolUseItem item)
    {
        if (_byToolUse.TryGetValue(item.ToolUseId, out var task) && task.Tool is null)
        {
            task.Tool = item;
            Update(notify: true);
        }
    }

    /// <summary>The conversation was cleared (<c>/clear</c>): the tasks keep running, but their cards are gone.</summary>
    internal void OnConversationCleared()
    {
        foreach (var task in _byId.Values)
        {
            task.Tool = null;
        }
        Update(notify: true);
    }

    /// <summary>The tab's <c>claude</c> exited or is restarting: whatever it was running went with it.</summary>
    public void Clear()
    {
        if (_byId.Count == 0)
        {
            return;
        }
        _byId.Clear();
        _byToolUse.Clear();
        _open.Clear();
        Update(notify: false);
    }

    /// <summary>Updates running times; called every second while the list is open.</summary>
    public void Tick()
    {
        foreach (var task in Listed)
        {
            task.Tick();
        }
    }

    private void Update(bool notify)
    {
        foreach (var task in _open.Where(t => t is { Agent: null, TaskType: "local_agent" }))
        {
            task.Agent = _agents?.FindByTask(task.TaskId) ?? (task.ToolUseId is { } id ? _agents?.Find(id) : null);
            if (task.Agent is not null)
            {
                task.Refresh();
            }
        }
        var running = _open.Where(t => t.IsRunning).ToList();
        var changed = Follow(Running, running);
        changed |= Follow(Listed, [.. running.Where(t => t.Kind != TaskKind.Agent)]);
        if (changed || notify)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Makes <paramref name="shown"/> what <paramref name="wanted"/> is, in place. True when it changed.</summary>
    private static bool Follow(ObservableCollection<RunningTask> shown, List<RunningTask> wanted)
    {
        if (wanted.SequenceEqual(shown))
        {
            return false;
        }
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(shown[i]))
            {
                shown.RemoveAt(i);
            }
        }
        // What's left is in order, so each new task goes in where the list first differs.
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= shown.Count || !ReferenceEquals(shown[i], wanted[i]))
            {
                shown.Insert(i, wanted[i]);
            }
        }
        return true;
    }
}
