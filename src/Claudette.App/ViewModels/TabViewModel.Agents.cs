using Claudette.App.Conversation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The tab's agent map (DESIGN.md §18, "Agent map"): the main agent and its subagents as a tree, on a page of the side
/// panel next to Changed files and Processes, and in a window of its own.
/// </summary>
public sealed partial class TabViewModel
{
    private ITimer? _agentTicker;

    /// <summary>Kept by the conversation builder from the same routing as the subagent groups.</summary>
    public AgentMap Agents { get; }

    /// <summary>The Agents page of the side panel is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilesPage))]
    public partial bool IsAgentsPage { get; set; }

    partial void OnIsAgentsPageChanged(bool value)
    {
        if (value)
        {
            IsProcessesPage = false;
        }
    }

    [RelayCommand]
    private void ShowAgentsPage() => IsAgentsPage = true;

    public bool HasAgents => Agents.HasSubagents;

    /// <summary>A subagent is running or waiting: the Agents page button shows a busy dot.</summary>
    public bool HasActiveAgents => Agents.ActiveCount > 0;

    /// <summary>The top of the Agents page.</summary>
    public string AgentsHeader => Agents.Summary ?? "Claude hasn't started any subagents in this tab.";

    /// <summary>The node whose prompt and result show below the tree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedAgent))]
    public partial AgentNode? SelectedAgent { get; set; }

    public bool HasSelectedAgent => SelectedAgent is not null;

    /// <summary>Asks the view to open the agent map in a window of its own.</summary>
    public event Action? AgentWindowRequested;

    [RelayCommand]
    private void OpenAgentWindow() => AgentWindowRequested?.Invoke();

    /// <summary>
    /// Clicking a node: the permission prompt it's waiting on, else its group in the conversation, expanded along with
    /// the groups it's nested in (DESIGN.md §18, "Interaction").
    /// </summary>
    [RelayCommand]
    private void ShowAgent(AgentNode? node)
    {
        if (node is null)
        {
            return;
        }
        SelectedAgent = node;
        if (node.WaitingPrompt is { } prompt)
        {
            ScrollToRequested?.Invoke(prompt);
            return;
        }
        if (node.Item is not { } group)
        {
            return;
        }
        for (var parent = node.Parent; parent?.Item is { } outer; parent = parent.Parent)
        {
            outer.IsExpanded = true;
        }
        group.IsExpanded = true;
        ScrollToRequested?.Invoke(group);
    }

    /// <summary>
    /// Stops one subagent, after confirming. Claude Code's <c>stop_task</c> stops a subagent in the foreground or the
    /// background, and the ones it started; the rest of the turn carries on. Without a task id to stop it by, the
    /// fallback is the whole-turn Stop.
    /// </summary>
    [RelayCommand]
    private void StopAgent(AgentNode? node)
    {
        if (node is not { CanStop: true } || _session is not { } session)
        {
            return;
        }
        if (node.TaskId is { } taskId)
        {
            _shell.Confirm(
                $"Stop \"{node.Title}\"?",
                "Claude Code stops this subagent, and any subagents it started, and tells Claude it was stopped. The rest of the turn carries on.",
                "Stop subagent",
                async () =>
                {
                    try
                    {
                        await session.StopTaskAsync(taskId);
                    }
                    catch (Exception ex)
                    {
                        _conversation.AddNote($"Couldn't stop the subagent: {ex.Message}", NoteKind.Error);
                    }
                });
        }
        else if (IsWorking)
        {
            _shell.Confirm(
                "Stop the whole turn?",
                "Claude Code hasn't given this subagent an id to stop it by, so Claudette can only stop the whole turn, like the Stop button.",
                "Stop turn",
                StopAsync);
        }
    }

    [RelayCommand]
    private Task CopyAgentPromptAsync(AgentNode? node) =>
        node?.Prompt is { Length: > 0 } prompt ? _services.Platform.SetClipboardTextAsync(prompt) : Task.CompletedTask;

    [RelayCommand]
    private Task CopyAgentResultAsync(AgentNode? node) =>
        node?.ResultText is { Length: > 0 } result ? _services.Platform.SetClipboardTextAsync(result) : Task.CompletedTask;

    private void OnAgentsChanged()
    {
        OnPropertyChanged(nameof(InfoRows));
        OnPropertyChanged(nameof(AgentsHeader));
        OnPropertyChanged(nameof(HasAgents));
        OnPropertyChanged(nameof(HasActiveAgents));
        if (SelectedAgent is { } selected && !Agents.Root.DescendantsAndSelf().Contains(selected))
        {
            // Gone after /clear.
            SelectedAgent = null;
        }
        UpdateAgentTicker();
    }

    /// <summary>Running times tick every second while an agent runs, from the injected clock.</summary>
    private void UpdateAgentTicker()
    {
        if (Agents.IsTicking)
        {
            _agentTicker ??= _services.Time.CreateTimer(_ => _services.Dispatcher.Post(Agents.Tick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        else
        {
            StopAgentTicker();
        }
    }

    private void StopAgentTicker()
    {
        _agentTicker?.Dispose();
        _agentTicker = null;
    }
}
