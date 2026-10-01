using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The side panel (DESIGN.md §3): Changed files, Agents, Project when the tab has project tools, and Processes when the
/// monitor is on. Which page shows, and how wide it is.
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
        }
        ProjectTools.UpdateShownRun();
        ProcessMonitor.OnSidePanelOpenChanged();
    }

    /// <summary>Which page of the side panel shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilesPage))]
    public partial bool IsProcessesPage { get; set; }

    public bool IsFilesPage => !IsProcessesPage && !IsAgentsPage && !IsProjectPage;

    partial void OnIsProcessesPageChanged(bool value)
    {
        ProcessMonitor.IsPanelVisible = value && IsSidePanelOpen;
        if (value)
        {
            IsAgentsPage = false;
            IsProjectPage = false;
        }
    }

    /// <summary>The Project page of the side panel is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilesPage))]
    public partial bool IsProjectPage { get; set; }

    partial void OnIsProjectPageChanged(bool value)
    {
        if (value)
        {
            IsProcessesPage = false;
            IsAgentsPage = false;
        }
        ProjectTools.UpdateShownRun();
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
    private void ShowFilesPage()
    {
        IsProcessesPage = false;
        IsAgentsPage = false;
        IsProjectPage = false;
    }

    [RelayCommand]
    private void ShowProcessesPage() => IsProcessesPage = true;

    [RelayCommand]
    private void ShowProjectPage() => IsProjectPage = true;

    /// <summary>Opens the side panel on the Project page: <b>Show output…</b>, a run's entry, or its notification.</summary>
    private void OpenProjectPage()
    {
        IsSidePanelOpen = true;
        IsProjectPage = true;
    }
}
