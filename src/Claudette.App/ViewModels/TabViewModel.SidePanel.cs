using Claudette.App.Conversation;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>The pages of the side panel (DESIGN.md §3).</summary>
public enum SidePanelPage
{
    Files,
    Agents,
    Project,
    Processes,
    Tasks,
    Mcp,
    ScratchPad,
}

/// <summary>
/// The side panel (DESIGN.md §3): Changed files, Agents, Tasks, Project when the tab has project tools, MCP when the
/// session has MCP servers, Processes when the monitor is on, and the project's scratch pad. Which page shows, and how
/// wide it is.
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>
    /// The side panel: Changed files, Agents, Project when the tab has project tools, and Processes when the monitor is
    /// on (DESIGN.md §3).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidePanelToggleText))]
    public partial bool IsSidePanelOpen { get; set; }

    /// <summary>The header's side panel button: its tip and name.</summary>
    public string SidePanelToggleText => IsSidePanelOpen ? "Hide the side panel" : "Show the side panel";

    partial void OnIsSidePanelOpenChanged(bool value)
    {
        ProcessMonitor.IsPanelVisible = value && IsProcessesPage;
        if (value)
        {
            _ = ChangedFiles.RefreshAsync();
            if (IsMcpPage)
            {
                _ = McpServers.RefreshAsync();
            }
            if (IsScratchPadPage)
            {
                _ = ScratchPad.SyncAsync();
            }
        }
        ProjectTools.Runs.UpdateShownRun();
        ProcessMonitor.OnSidePanelOpenChanged();
    }

    /// <summary>Which page of the side panel shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilesPage), nameof(IsAgentsPage), nameof(IsProjectPage), nameof(IsProcessesPage), nameof(IsTasksPage), nameof(IsMcpPage),
        nameof(IsScratchPadPage))]
    public partial SidePanelPage Page { get; set; }

    partial void OnPageChanged(SidePanelPage value)
    {
        ProcessMonitor.IsPanelVisible = value == SidePanelPage.Processes && IsSidePanelOpen;
        ProjectTools.Runs.UpdateShownRun();
        if (value == SidePanelPage.Mcp && IsSidePanelOpen)
        {
            _ = McpServers.RefreshAsync();
        }
        if (value == SidePanelPage.ScratchPad && IsSidePanelOpen)
        {
            _ = ScratchPad.SyncAsync();
        }
    }

    public bool IsFilesPage => Page == SidePanelPage.Files;

    /// <summary>The Agents page of the side panel is showing. Setting it false goes back to Changed files.</summary>
    public bool IsAgentsPage
    {
        get => Page == SidePanelPage.Agents;
        set => SetPage(SidePanelPage.Agents, value);
    }

    /// <summary>The Project page of the side panel is showing.</summary>
    public bool IsProjectPage
    {
        get => Page == SidePanelPage.Project;
        set => SetPage(SidePanelPage.Project, value);
    }

    public bool IsProcessesPage
    {
        get => Page == SidePanelPage.Processes;
        set => SetPage(SidePanelPage.Processes, value);
    }

    /// <summary>The Tasks page: the plan and the tasks Claude is working through (DESIGN.md §5, "Tasks").</summary>
    public bool IsTasksPage
    {
        get => Page == SidePanelPage.Tasks;
        set => SetPage(SidePanelPage.Tasks, value);
    }

    /// <summary>The MCP page: the session's MCP servers (DESIGN.md §4, "MCP servers").</summary>
    public bool IsMcpPage
    {
        get => Page == SidePanelPage.Mcp;
        set => SetPage(SidePanelPage.Mcp, value);
    }

    /// <summary>The Scratch Pad page: the project's scratch pad (DESIGN.md §18, "Scratch pad").</summary>
    public bool IsScratchPadPage
    {
        get => Page == SidePanelPage.ScratchPad;
        set => SetPage(SidePanelPage.ScratchPad, value);
    }

    private void SetPage(SidePanelPage page, bool showing)
    {
        if (showing)
        {
            Page = page;
        }
        else if (Page == page)
        {
            Page = SidePanelPage.Files;
        }
    }

    [RelayCommand]
    private void ToggleSidePanel() => IsSidePanelOpen = !IsSidePanelOpen;

    /// <summary>The side panel's width: the same for every tab, set by dragging its edge (DESIGN.md §3).</summary>
    public double SidePanelWidth => _shell.Layout.SidePanelWidth;

    /// <summary>Dragging the side panel's edge. <see cref="SaveSidePanelWidth"/> keeps the result when the drag ends.</summary>
    public void ResizeSidePanel(double width) => _shell.Layout.ResizeSidePanel(width);

    public void SaveSidePanelWidth() => _shell.Layout.SaveSidePanelWidth();

    /// <summary>Double-clicking the edge.</summary>
    public void ResetSidePanelWidth()
    {
        _shell.Layout.ResizeSidePanel(ShellLayout.DefaultSidePanelWidth);
        _shell.Layout.SaveSidePanelWidth();
    }

    internal void OnSidePanelWidthChanged() => OnPropertyChanged(nameof(SidePanelWidth));

    [RelayCommand]
    private void ShowFilesPage() => Page = SidePanelPage.Files;

    [RelayCommand]
    private void ShowProcessesPage() => Page = SidePanelPage.Processes;

    [RelayCommand]
    private void ShowProjectPage() => Page = SidePanelPage.Project;

    [RelayCommand]
    private void ShowTasksPage() => Page = SidePanelPage.Tasks;

    [RelayCommand]
    private void ShowMcpPage() => Page = SidePanelPage.Mcp;

    [RelayCommand]
    private void ShowScratchPadPage() => Page = SidePanelPage.ScratchPad;

    /// <summary>Opens the side panel on <paramref name="page"/>: the command palette's way to it.</summary>
    internal void OpenSidePanelPage(SidePanelPage page)
    {
        IsSidePanelOpen = true;
        Page = page;
    }

    /// <summary>The composer bar's way to the Tasks page: opens the side panel on it.</summary>
    [RelayCommand]
    private void OpenTasksPage()
    {
        IsSidePanelOpen = true;
        Page = SidePanelPage.Tasks;
    }

    /// <summary>
    /// A reply's <b>Show as the plan</b> (DESIGN.md §5, "Tasks"): for a plan Claude wrote in a reply rather than through
    /// Plan mode. It heads the Tasks page as an approved plan does, and the side panel opens on it. Kept with the tab.
    /// </summary>
    [RelayCommand]
    private void ShowAsPlan(AssistantTextItem? reply)
    {
        if (reply is null || reply.IsStreaming || string.IsNullOrWhiteSpace(reply.Text))
        {
            return;
        }
        TodoList.ChooseReply(reply.Text, reply.SentAt);
        State.Plan = new ChosenPlan { Text = TodoList.Plan!, WrittenAt = reply.SentAt, ChosenAt = _services.Time.GetUtcNow() };
        _services.SaveState();
        OpenSidePanelPage(SidePanelPage.Tasks);
        _shell.Announce("Shown as the plan on the Tasks page.");
    }

    /// <summary>
    /// A restored tab's chosen plan, once its conversation is read back: shown while its reply is still there (a rewind
    /// can go back past it) and no plan was approved after it was chosen; forgotten otherwise.
    /// </summary>
    private void RestoreChosenPlan()
    {
        if (State.Plan is not { } chosen)
        {
            return;
        }
        var approvedLater = TodoList.Plans.Any(p => p is { State: PlanState.Approved, At: { } approved } && approved > chosen.ChosenAt);
        if (approvedLater || !Replies(Items).Any(r => r.Text.Trim() == chosen.Text))
        {
            State.Plan = null;
            return;
        }
        TodoList.ChooseReply(chosen.Text, chosen.WrittenAt);
    }

    /// <summary>Every reply in the conversation, subagents' groups' too.</summary>
    private static IEnumerable<AssistantTextItem> Replies(IEnumerable<ConversationItem> items) =>
        items.SelectMany<ConversationItem, AssistantTextItem>(item => item switch
        {
            AssistantTextItem reply => [reply],
            SubagentItem group => Replies(group.Items),
            _ => [],
        });

    /// <summary>Opens the side panel on the Project page: <b>Show output…</b>, a run's entry, or its notification.</summary>
    private void OpenProjectPage()
    {
        IsSidePanelOpen = true;
        IsProjectPage = true;
    }
}
