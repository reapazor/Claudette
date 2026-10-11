using Claudette.App.Conversation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The tab's running tasks (DESIGN.md §5, "Running tasks"): work Claude Code keeps going in the background, such as a
/// backgrounded shell command, a background subagent, a Monitor watch or a remote agent. A chip in the composer bar
/// lists them, but for subagents, which the Agents button and page have; the tab's row counts them all once the turn
/// is over (DESIGN.md §4), and the info card has a row for those the chip lists. And how far Claude's own task list has
/// got (DESIGN.md §5, "Tasks"): the row's badge and line, going to where a task started, and <b>Continue</b>.
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

    // ---- Claude's task list and plan (DESIGN.md §5, "Tasks") ---------------------------------------------------------

    /// <summary>Settings → Appearance → Show task progress on tab rows.</summary>
    private bool TaskProgressOnRows => _services.Settings.Appearance.ShowTaskProgressOnTabs;

    /// <summary>The row's "3/7" badge: Claude's tasks aren't all done, and Settings shows it.</summary>
    public bool ShowTaskProgressBadge => TaskProgressOnRows && TodoList.HasTasksLeft;

    /// <summary>The task the row's second line says while the tab works with it in progress; null otherwise.</summary>
    private string? WorkingTaskText => TaskProgressOnRows && Status == TabStatus.Working && TodoList.Current is { } current ? current.DisplayText : null;

    /// <summary>The latest row at a turn's end about the task list: the one that may offer <b>Continue</b>.</summary>
    private TasksSummaryItem? _tasksSummary;

    private void OnTodoListChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Conversation.TodoList.HasTasksLeft):
                OnPropertyChanged(nameof(ShowTaskProgressBadge));
                // The last of Claude's tasks just done, as it works (not a transcript loading): confetti (DESIGN.md §5).
                if (TodoList.AllDone && Status is TabStatus.Working or TabStatus.NeedsInput)
                {
                    TellMascot(mascot => mascot.AllTasksDone());
                }
                break;
            case nameof(Conversation.TodoList.Current) or nameof(Conversation.TodoList.InfoText):
                OnPropertyChanged(nameof(RowDetail));
                OnPropertyChanged(nameof(InfoRows));
                break;
        }
    }

    /// <summary>A turn ended: only its row about the task list, if it has one, offers <b>Continue</b>, and only with tasks left.</summary>
    private void OnTurnEndedForTasks()
    {
        WithdrawContinue();
        if (_conversation.TurnTasksSummary is { } summary)
        {
            summary.IsContinueOffered = !summary.AllDone;
            _tasksSummary = summary;
        }
    }

    /// <summary>A turn started: <b>Continue</b> isn't offered any more.</summary>
    private void WithdrawContinue()
    {
        if (_tasksSummary is not null)
        {
            _tasksSummary.IsContinueOffered = false;
        }
    }

    /// <summary>The row's badge: selects the tab and opens the Tasks page.</summary>
    [RelayCommand]
    private void ShowTasksFromRow()
    {
        _shell.SelectedTab = this;
        OpenSidePanelPage(SidePanelPage.Tasks);
    }

    /// <summary>A task on the Tasks page or the pinned list: where it started in the conversation, its groups expanded.</summary>
    [RelayCommand]
    private void GoToTask(TodoItem? task)
    {
        if (task?.Start is not { } row)
        {
            return;
        }
        Expand(Items, row);
        ScrollToRequested?.Invoke(row);

        static bool Expand(IEnumerable<ConversationItem> items, ConversationItem target)
        {
            foreach (var item in items)
            {
                if (ReferenceEquals(item, target))
                {
                    return true;
                }
                if (item is SubagentItem group && Expand(group.Items, target))
                {
                    group.IsExpanded = true;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>A file a task changed: in the diff view, as Changed files shows it.</summary>
    [RelayCommand]
    private Task OpenTaskFileAsync(TaskFileRow? file) =>
        file is null ? Task.CompletedTask : ChangedFiles.OpenChangeDiffAsync(file.ToolUseId);

    /// <summary>The pinned list's <b>Use as the plan</b>: the suggested reply, as <b>Show as the plan</b> makes it.</summary>
    [RelayCommand]
    private void UsePlanSuggestion()
    {
        if (TodoList.SuggestedReply is { } reply)
        {
            ShowAsPlan(reply);
        }
        TodoList.DismissSuggestion();
    }

    [RelayCommand]
    private void DismissPlanSuggestion() => TodoList.DismissSuggestion();

    /// <summary>The message <b>Continue</b> sends, as the user's own.</summary>
    internal const string ContinueTasksMessage = "Carry on with the tasks that are left.";

    /// <summary><b>Continue</b> on the row at a turn's end: asks Claude to carry on with the tasks that are left.</summary>
    [RelayCommand]
    private async Task ContinueTasksAsync(TasksSummaryItem? row)
    {
        if (row is not { IsContinueOffered: true } || IsWorking || IsReadOnly)
        {
            return;
        }
        row.IsContinueOffered = false;
        _autoContinue.UserSent();
        var stamp = NewStamp(fromUser: true);
        _conversation.AddUserMessage(ContinueTasksMessage).SentId = stamp.Uuid;
        await SendRawAsync(ContinueTasksMessage, stamp: stamp);
    }
}
