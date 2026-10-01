using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The shell's widths (DESIGN.md §3, §4, "Sidebar"): the sidebar's, collapsed to its rail by the user or by a narrow
/// window, and the side panel's, which every tab shares. What the user chooses is kept in the app state.
/// </summary>
public sealed partial class ShellLayout : ViewModelBase
{
    private readonly AppState _state;
    private readonly Action _saveState;
    private readonly Action _sidePanelWidthChanged;
    private bool _isNarrow;

    /// <param name="saveState">Saves <paramref name="state"/> once a width or the sidebar's collapse changes.</param>
    /// <param name="sidePanelWidthChanged">Tells every tab its side panel's width changed.</param>
    internal ShellLayout(AppState state, Action saveState, Action sidePanelWidthChanged)
    {
        _state = state;
        _saveState = saveState;
        _sidePanelWidthChanged = sidePanelWidthChanged;
        IsSidebarCollapsed = state.SidebarCollapsed;
        SidebarWidth = Math.Clamp(state.SidebarWidth ?? DefaultSidebarWidth, MinSidebarWidth, MaxSidebarWidth);
        SidePanelWidth = Math.Clamp(state.SidePanelWidth ?? DefaultSidePanelWidth, MinSidePanelWidth, MaxSidePanelWidth);
    }

    // ---- Sidebar (DESIGN.md §4, "Sidebar") ------------------------------------------------------------------

    public const double DefaultSidebarWidth = 248;
    public const double MinSidebarWidth = 180;
    public const double MaxSidebarWidth = 420;

    /// <summary>The collapsed sidebar: a rail of status icons.</summary>
    public const double RailWidth = 52;

    /// <summary>Below this width the sidebar collapses to its rail by itself, leaving the user's own choice alone.</summary>
    public const double NarrowWidth = 900;

    /// <summary>Whether the sidebar shows only its rail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarExpanded), nameof(SidebarDisplayWidth))]
    public partial bool IsSidebarCollapsed { get; set; }

    public bool IsSidebarExpanded => !IsSidebarCollapsed;

    /// <summary>The expanded sidebar's width, as the user dragged it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarDisplayWidth))]
    public partial double SidebarWidth { get; set; }

    public double SidebarDisplayWidth => IsSidebarCollapsed ? RailWidth : SidebarWidth;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        // In a narrow window, expanding is only for now: once it's wide again, the user's choice comes back.
        if (!_isNarrow)
        {
            _state.SidebarCollapsed = IsSidebarCollapsed;
            _saveState();
        }
    }

    /// <summary>Called by the view as the window resizes: a narrow window shows the rail.</summary>
    public void SetAvailableWidth(double width)
    {
        var narrow = width < NarrowWidth;
        if (narrow == _isNarrow)
        {
            return;
        }
        _isNarrow = narrow;
        IsSidebarCollapsed = narrow || _state.SidebarCollapsed;
    }

    /// <summary>Dragging the sidebar's edge. <see cref="SaveSidebarWidth"/> keeps the result when the drag ends.</summary>
    public void ResizeSidebar(double width) => SidebarWidth = Math.Clamp(width, MinSidebarWidth, MaxSidebarWidth);

    public void SaveSidebarWidth()
    {
        _state.SidebarWidth = SidebarWidth;
        _saveState();
    }

    // ---- Side panel (DESIGN.md §3): one width for every tab's -----------------------------------------------

    public const double DefaultSidePanelWidth = 340;
    public const double MinSidePanelWidth = 260;
    public const double MaxSidePanelWidth = 900;

    /// <summary>The side panel's width, as the user dragged it. Each tab's view also keeps room for its conversation.</summary>
    [ObservableProperty]
    public partial double SidePanelWidth { get; private set; } = DefaultSidePanelWidth;

    partial void OnSidePanelWidthChanged(double value) => _sidePanelWidthChanged();

    /// <summary>Dragging the side panel's edge. <see cref="SaveSidePanelWidth"/> keeps the result when the drag ends.</summary>
    public void ResizeSidePanel(double width) => SidePanelWidth = Math.Clamp(width, MinSidePanelWidth, MaxSidePanelWidth);

    public void SaveSidePanelWidth()
    {
        _state.SidePanelWidth = SidePanelWidth;
        _saveState();
    }
}
