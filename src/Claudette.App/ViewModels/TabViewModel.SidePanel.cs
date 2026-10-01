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
}

/// <summary>
/// The side panel (DESIGN.md §3): Changed files, Agents, Tasks, Project when the tab has project tools, MCP when the
/// session has MCP servers, and Processes when the monitor is on. Which page shows, and how wide it is.
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>
    /// The side panel: Changed files, Agents, Project when the tab has project tools, and Processes when the monitor is
    /// on (DESIGN.md §3).
    /// </summary>
    [ObservableProperty]
    public partial bool IsSidePanelOpen { get; set; }

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
        }
        ProjectTools.UpdateShownRun();
        ProcessMonitor.OnSidePanelOpenChanged();
    }

    /// <summary>Which page of the side panel shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilesPage), nameof(IsAgentsPage), nameof(IsProjectPage), nameof(IsProcessesPage), nameof(IsTasksPage), nameof(IsMcpPage))]
    public partial SidePanelPage Page { get; set; }

    partial void OnPageChanged(SidePanelPage value)
    {
        ProcessMonitor.IsPanelVisible = value == SidePanelPage.Processes && IsSidePanelOpen;
        ProjectTools.UpdateShownRun();
        if (value == SidePanelPage.Mcp && IsSidePanelOpen)
        {
            _ = McpServers.RefreshAsync();
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
    public double SidePanelWidth => _shell.SidePanelWidth;

    /// <summary>Dragging the side panel's edge. <see cref="SaveSidePanelWidth"/> keeps the result when the drag ends.</summary>
    public void ResizeSidePanel(double width) => _shell.ResizeSidePanel(width);

    public void SaveSidePanelWidth() => _shell.SaveSidePanelWidth();

    /// <summary>Double-clicking the edge.</summary>
    public void ResetSidePanelWidth()
    {
        _shell.ResizeSidePanel(ShellViewModel.DefaultSidePanelWidth);
        _shell.SaveSidePanelWidth();
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

    /// <summary>The composer bar's way to the Tasks page: opens the side panel on it.</summary>
    [RelayCommand]
    private void OpenTasksPage()
    {
        IsSidePanelOpen = true;
        Page = SidePanelPage.Tasks;
    }

    /// <summary>Opens the side panel on the Project page: <b>Show output…</b>, a run's entry, or its notification.</summary>
    private void OpenProjectPage()
    {
        IsSidePanelOpen = true;
        IsProjectPage = true;
    }
}
