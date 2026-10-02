using Claudette.App.Conversation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The tab's running tasks (DESIGN.md §5, "Running tasks"): work Claude Code keeps going in the background, such as a
/// backgrounded shell command, a background subagent, a Monitor watch or a remote agent. A chip in the composer bar
/// lists them, but for subagents, which the Agents button and page have; the tab's row counts them all once the turn
/// is over (DESIGN.md §4), and the info card has a row for those the chip lists.
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>A running task's time ticks every second while the list shows.</summary>
    private UiTicker TaskTicker => field ??= new(_services.Time, _services.Dispatcher, TimeSpan.FromSeconds(1), Tasks.Tick);

    /// <summary>Kept by the conversation builder from Claude Code's task messages.</summary>
    public RunningTasks Tasks { get; }

    /// <summary>Every running task, subagents too: the tab row's count.</summary>
    public int RunningTaskCount => Tasks.Count;

    /// <summary>The composer bar shows the chip: tasks other than subagents are running.</summary>
    public bool HasRunningTasks => Tasks.Listed.Count > 0;

    /// <summary>"1 running task", "3 running tasks": the chip, and the head of its list.</summary>
    public string RunningTasksText => Tasks.Listed.Count == 1 ? "1 running task" : $"{Tasks.Listed.Count} running tasks";

    /// <summary>The turn is over but tasks are still running: the tab's row shows how many, from any tab.</summary>
    public bool ShowTaskBadge => Tasks.Count > 0 && !IsWorking;

    /// <summary>"2 agents and 1 task still running": the row's count, subagents told apart.</summary>
    public string TaskBadgeTip
    {
        get
        {
            var (agents, others) = (Tasks.AgentCount, Tasks.Listed.Count);
            var parts = new List<string>(2);
            if (agents > 0)
            {
                parts.Add(agents == 1 ? "1 agent" : $"{agents} agents");
            }
            if (others > 0 || agents == 0)
            {
                parts.Add(others == 1 ? "1 task" : $"{others} tasks");
            }
            return $"{string.Join(" and ", parts)} still running";
        }
    }

    /// <summary>The chip's list is open: running times tick every second, from the injected clock.</summary>
    [ObservableProperty]
    public partial bool IsTaskListOpen { get; set; }

    partial void OnIsTaskListOpenChanged(bool value)
    {
        if (value)
        {
            Tasks.Tick();
        }
        UpdateTaskTicker();
    }

    /// <summary>Clicking a task: the card of the call that started it, with the groups it's nested in expanded.</summary>
    [RelayCommand]
    private void ShowTask(RunningTask? task)
    {
        if (task?.Tool is not { } tool)
        {
            return;
        }
        if (task.Agent is { Item: not null } node)
        {
            ShowAgent(node);
            return;
        }
        for (var owner = Agents.OwnerOf(tool.ToolUseId); owner?.Item is { } group; owner = owner.Parent)
        {
            group.IsExpanded = true;
        }
        ScrollToRequested?.Invoke(tool);
    }

    /// <summary>
    /// Stops one task, after confirming, through Claude Code's <c>stop_task</c>, so Claude is told it ended: the same
    /// path as the process monitor's Stop (DESIGN.md §4) and, for a subagent, the agent map's (DESIGN.md §18).
    /// </summary>
    [RelayCommand]
    private void StopTask(RunningTask? task)
    {
        if (task is null || _session is not { } session)
        {
            return;
        }
        if (task.Agent is { CanStop: true } node)
        {
            StopAgent(node);
            return;
        }
        _shell.Confirm(
            $"Stop \"{task.Title}\"?",
            task.Kind switch
            {
                TaskKind.Shell => "Claude Code stops this background command and tells Claude it ended.",
                TaskKind.Monitor => "Claude Code ends this watch and tells Claude it was stopped.",
                TaskKind.Agent => "Claude Code stops this subagent, and any subagents it started, and tells Claude it was stopped.",
                _ => "Claude Code stops this task and tells Claude it ended.",
            },
            "Stop",
            async () =>
            {
                try
                {
                    await session.StopTaskAsync(task.TaskId);
                }
                catch (Exception ex)
                {
                    _conversation.AddNote($"Couldn't stop {task.Title}: {ex.Message}", NoteKind.Error);
                }
            });
    }

    private void OnTasksChanged()
    {
        OnPropertyChanged(nameof(RunningTaskCount));
        OnPropertyChanged(nameof(HasRunningTasks));
        OnPropertyChanged(nameof(RunningTasksText));
        OnPropertyChanged(nameof(ShowTaskBadge));
        OnPropertyChanged(nameof(TaskBadgeTip));
        OnPropertyChanged(nameof(InfoRows));
        UpdateTaskTicker();
    }

    private void UpdateTaskTicker() => TaskTicker.Run(IsTaskListOpen && HasRunningTasks);
}
