using Claudette.App.Conversation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The tab's running tasks (DESIGN.md §5, "Running tasks"): work Claude Code keeps going in the background, such as a
/// backgrounded shell command, a background subagent, a Monitor watch or a remote agent. A chip in the composer bar
/// lists them, the tab's row counts them once the turn is over (DESIGN.md §4), and the info card has a row for them.
/// </summary>
public sealed partial class TabViewModel
{
    private ITimer? _taskTicker;

    /// <summary>Kept by the conversation builder from Claude Code's task messages.</summary>
    public RunningTasks Tasks { get; }

    public int RunningTaskCount => Tasks.Count;

    /// <summary>The composer bar shows the chip.</summary>
    public bool HasRunningTasks => Tasks.Count > 0;

    /// <summary>"1 running task", "3 running tasks": the chip, and the head of its list.</summary>
    public string RunningTasksText => Tasks.Count == 1 ? "1 running task" : $"{Tasks.Count} running tasks";

    /// <summary>The turn is over but tasks are still running: the tab's row shows how many, from any tab.</summary>
    public bool ShowTaskBadge => HasRunningTasks && !IsWorking;

    public string TaskBadgeTip => Tasks.Count == 1 ? "1 task still running" : $"{Tasks.Count} tasks still running";

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

    private void UpdateTaskTicker()
    {
        if (IsTaskListOpen && HasRunningTasks)
        {
            _taskTicker ??= _services.Time.CreateTimer(_ => _services.Dispatcher.Post(Tasks.Tick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        else
        {
            StopTaskTicker();
        }
    }

    private void StopTaskTicker()
    {
        _taskTicker?.Dispose();
        _taskTicker = null;
    }
}
