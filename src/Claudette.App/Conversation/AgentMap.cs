using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Transcripts;

namespace Claudette.App.Conversation;

/// <summary>
/// A tab's agents (DESIGN.md §18, "Agent map"): the main agent, and every subagent under the agent that started it.
/// Kept by the <see cref="ConversationBuilder"/> as it routes subagent traffic into groups, and completed from Claude
/// Code's task messages (<c>task_started</c>, <c>task_progress</c>, <c>task_notification</c>, <c>task_updated</c>) and
/// <c>tool_progress</c>. Must be used on the UI thread, like the builder.
/// </summary>
public sealed class AgentMap
{
    private readonly TimeProvider _time;
    private readonly Dictionary<string, AgentNode> _byToolUse = [];
    private readonly Dictionary<string, AgentNode> _byTask = [];
    private readonly Dictionary<string, AgentNode> _toolCallOwners = [];
    private readonly List<PromptItem> _unattributed = [];

    // task_started for an Agent call the stream hasn't shown yet, by that call's id.
    private readonly Dictionary<string, SystemMessage> _earlyTasks = [];

    /// <param name="modelName">Turns a model id into a display name, as the tab does.</param>
    public AgentMap(TimeProvider time, Func<string?, string?>? modelName = null)
    {
        _time = time;
        ModelName = modelName ?? (m => m);
        Root = new AgentNode(this, null, null);
        Roots = [Root];
    }

    /// <summary>The main agent.</summary>
    public AgentNode Root { get; }

    /// <summary>The tree's top level: just the main agent.</summary>
    public ObservableCollection<AgentNode> Roots { get; }

    /// <summary>Something changed that the counts or the info card show.</summary>
    public event Action? Changed;

    /// <summary>Replaying a transcript: new agents aren't live (DESIGN.md §18, "Restored tabs").</summary>
    public bool IsReplaying { get; set; }

    internal DateTimeOffset Now => _time.GetUtcNow();

    internal Func<string?, string?> ModelName { get; }

    public IEnumerable<AgentNode> Subagents => Root.DescendantsAndSelf().Skip(1);

    public bool HasSubagents => _byToolUse.Count > 0;

    public int ActiveCount => Subagents.Count(n => n.IsActive);

    public int WaitingCount => Subagents.Count(n => n.IsWaiting);

    /// <summary>Something's running time is ticking.</summary>
    public bool IsTicking => Root.DescendantsAndSelf().Any(n => n.IsTicking);

    /// <summary>For the tab info card: "3 agents running", or how the finished ones ended. Null without subagents.</summary>
    public string? Summary
    {
        get
        {
            if (!HasSubagents)
            {
                return null;
            }
            var agents = Subagents.ToArray();
            var active = agents.Count(n => n.IsActive);
            var waiting = agents.Count(n => n.IsWaiting);
            var done = agents.Count(n => n.IsDone);
            var failed = agents.Count(n => n.IsFailed);
            var stopped = agents.Count(n => n.IsStopped);
            var finished = new List<string>();
            if (done > 0)
            {
                finished.Add($"{done} done");
            }
            if (failed > 0)
            {
                finished.Add($"{failed} failed");
            }
            if (stopped > 0)
            {
                finished.Add($"{stopped} stopped");
            }
            if (active == 0)
            {
                return agents.Length == 1 ? $"1 agent, {StatusWord(agents[0])}" : $"{agents.Length} agents: {string.Join(", ", finished)}";
            }
            var summary = active == 1 ? "1 agent running" : $"{active} agents running";
            if (waiting > 0)
            {
                summary += $" ({waiting} waiting on you)";
            }
            return finished.Count > 0 ? $"{summary}; {string.Join(", ", finished)}" : summary;

            static string StatusWord(AgentNode node) => node.Status switch
            {
                AgentStatus.Done => "done",
                AgentStatus.Failed => "failed",
                _ => "stopped",
            };
        }
    }

    public AgentNode? Find(string toolUseId) => _byToolUse.GetValueOrDefault(toolUseId);

    public AgentNode? FindByTask(string taskId) => _byTask.GetValueOrDefault(taskId);

    /// <summary>The agent that made a tool call, whose group holds its card; null when Claudette hasn't seen the call.</summary>
    public AgentNode? OwnerOf(string toolUseId) => _toolCallOwners.GetValueOrDefault(toolUseId);

    /// <summary>A subagent's <c>Agent</c> call appeared in <paramref name="parent"/>'s stream.</summary>
    internal AgentNode Add(AgentNode parent, SubagentItem item)
    {
        var node = new AgentNode(this, parent, item);
        _byToolUse[item.ToolUseId] = node;
        parent.Children.Add(node);
        parent.Refresh();
        if (_earlyTasks.Remove(item.ToolUseId, out var started))
        {
            OnTask(started);
        }
        return node;
    }

    internal void OnToolUse(AgentNode agent, string toolUseId, string name, JsonObject input)
    {
        agent.OnToolUse(name, input);
        _toolCallOwners[toolUseId] = agent;
        // A subagent's permission request can arrive before the tool call it's for.
        foreach (var prompt in _unattributed.Where(p => p.Request.ToolUseId == toolUseId).ToArray())
        {
            _unattributed.Remove(prompt);
            Attribute(prompt, agent);
        }
    }

    /// <summary>
    /// A permission prompt: it belongs to the subagent Claude Code names (<c>agent_id</c>, its task id), else to the
    /// agent whose tool call it's for, else to the main agent.
    /// </summary>
    internal void OnPrompt(PromptItem prompt)
    {
        var request = prompt.Request;
        var node = request.AgentId is { } agentId ? FindByTask(agentId) : null;
        if (node is null && request.ToolUseId is { } toolUseId)
        {
            node = _toolCallOwners.GetValueOrDefault(toolUseId);
        }
        if (node is null && request.AgentId is not null)
        {
            // From a subagent Claudette can't place yet: wait for its task_started or the tool call.
            _unattributed.Add(prompt);
            return;
        }
        Attribute(prompt, node ?? Root);
    }

    /// <summary>Claude Code's task messages, for subagents: their task id, progress, and how they ended.</summary>
    internal void OnTask(SystemMessage message)
    {
        var raw = message.Raw;
        var taskId = raw.GetString("task_id");
        var node = raw.GetString("tool_use_id") is { } toolUseId ? Find(toolUseId) : null;
        node ??= taskId is not null ? FindByTask(taskId) : null;
        if (node is null)
        {
            // A Bash command or another kind of task, not a subagent; or a subagent whose Agent call comes next.
            if (message.Subtype == "task_started" && raw.GetString("task_type") == "local_agent" && raw.GetString("tool_use_id") is { } early)
            {
                _earlyTasks[early] = message;
            }
            return;
        }
        switch (message.Subtype)
        {
            case "task_started" when taskId is not null:
                IndexTask(taskId, node);
                node.OnTaskStarted(taskId, raw);
                break;
            case "task_progress":
                node.OnTaskProgress(raw);
                break;
            case "task_notification":
                node.OnTaskEnded(raw.GetString("status"), raw.GetString("summary"), raw.GetObject("usage"));
                break;
            case "task_updated" when raw.GetObject("patch") is { } patch:
                node.OnTaskUpdated(patch);
                break;
        }
    }

    /// <summary>A background task's result read back from a transcript (DESIGN.md §18, "Restored tabs").</summary>
    public void OnTaskNotification(TranscriptTaskNotification notification)
    {
        var node = notification.ToolUseId is { } toolUseId ? Find(toolUseId) : null;
        node ??= notification.TaskId is { } taskId ? FindByTask(taskId) : null;
        if (node is null)
        {
            return;
        }
        if (notification.TaskId is { } id)
        {
            IndexTask(id, node);
        }
        node.ApplyReported(notification.Tokens, notification.ToolUses, notification.DurationMs);
        node.OnTaskEnded(notification.Status, notification.Result ?? notification.Summary, null);
    }

    /// <summary><c>tool_progress</c> for a subagent: <c>subagent_retry</c> while it waits out an API error.</summary>
    internal void OnToolProgress(ToolProgressMessage progress)
    {
        if (progress.ParentToolUseId is not { } parent || Find(parent) is not { } node)
        {
            return;
        }
        if (progress.Raw.GetObject("subagent_retry") is { } retry)
        {
            node.OnRetry(retry);
        }
        else if (!progress.IsHeartbeat)
        {
            node.OnRetry(null);
        }
    }

    internal void OnSessionState(SessionState state)
    {
        Root.SetWorking(state == SessionState.Working);
        if (state == SessionState.Exited)
        {
            OnSessionExited();
        }
    }

    /// <summary>
    /// A turn ended. A subagent in the foreground can't outlive its turn, so one that hasn't reported back was cut off
    /// (for example by an interrupt); background ones keep going.
    /// </summary>
    internal void OnTurnCompleted()
    {
        foreach (var node in Subagents.Where(n => n.IsActive && n.IsLive && !n.IsDetachedFromTurn()).ToArray())
        {
            node.Finish(AgentStatus.Stopped, null);
        }
    }

    /// <summary>Claude Code exited: nothing it was running is running any more.</summary>
    internal void OnSessionExited()
    {
        Root.SetWorking(false);
        foreach (var node in Subagents.Where(n => n.IsActive).ToArray())
        {
            node.Finish(AgentStatus.Stopped, null);
        }
    }

    /// <summary>
    /// A transcript has been replayed: the tree is finished, with no live status. A subagent with no recorded result
    /// didn't finish in that session (DESIGN.md §18, "Restored tabs").
    /// </summary>
    public void FinishReplay()
    {
        IsReplaying = false;
        foreach (var node in Subagents.Where(n => n.IsActive && !n.IsLive).ToArray())
        {
            node.Finish(AgentStatus.Stopped, null, unfinished: true);
        }
        _unattributed.Clear();
    }

    /// <summary>The conversation was cleared (<c>/clear</c>).</summary>
    internal void Clear()
    {
        Root.Children.Clear();
        _byToolUse.Clear();
        _byTask.Clear();
        _toolCallOwners.Clear();
        _unattributed.Clear();
        _earlyTasks.Clear();
        Root.Refresh();
    }

    /// <summary>Updates running times; called every second while something runs.</summary>
    public void Tick()
    {
        foreach (var node in Root.DescendantsAndSelf().Where(n => n.IsTicking))
        {
            node.Tick();
        }
    }

    internal void IndexTask(string taskId, AgentNode node)
    {
        _byTask[taskId] = node;
        node.SetTaskId(taskId);
        foreach (var prompt in _unattributed.Where(p => p.Request.AgentId == taskId).ToArray())
        {
            _unattributed.Remove(prompt);
            Attribute(prompt, node);
        }
    }

    internal void NotifyChanged() => Changed?.Invoke();

    private static void Attribute(PromptItem prompt, AgentNode node)
    {
        if (!node.IsRoot)
        {
            prompt.Asker = $"{node.AgentType} subagent: {node.Title}";
        }
        node.AddPrompt(prompt);
    }
}
