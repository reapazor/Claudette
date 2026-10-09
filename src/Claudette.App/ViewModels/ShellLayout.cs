using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The shell's widths (DESIGN.md §3, §4, "Sidebar"): the sidebar's, collapsed to its rail by the user or by a narrow
/// window, and the side panel's, which every tab shares. What the user chooses is kept in the app state. Neither has a
/// most; between them the conversation keeps <see cref="MinConversationWidth"/>, and when the window is too narrow for
/// both, the side panel gives way first.
/// </summary>
public sealed partial class ShellLayout : ViewModelBase
{
    private readonly AppState _state;
    private readonly Action _saveState;
    private readonly Action _sidePanelWidthChanged;
    private bool _isNarrow;

    /// <summary>The shell's width, which the sidebar leaves room in; unbounded until the view reports it.</summary>
    private double _availableWidth = double.PositiveInfinity;

    /// <param name="saveState">Saves <paramref name="state"/> once a width or the sidebar's collapse changes.</param>
    /// <param name="sidePanelWidthChanged">Tells every tab its side panel's width changed.</param>
    internal ShellLayout(AppState state, Action saveState, Action sidePanelWidthChanged)
    {
        _state = state;
        _saveState = saveState;
        _sidePanelWidthChanged = sidePanelWidthChanged;
        IsSidebarCollapsed = state.SidebarCollapsed;
        SidebarWidth = Math.Max(state.SidebarWidth ?? DefaultSidebarWidth, MinSidebarWidth);
        SidePanelWidth = Math.Max(state.SidePanelWidth ?? DefaultSidePanelWidth, MinSidePanelWidth);
    }

    /// <summary>The conversation keeps at least this much room between the sidebar and the side panel (DESIGN.md §3).</summary>
    public const double MinConversationWidth = 360;

    // ---- Sidebar (DESIGN.md §4, "Sidebar") ------------------------------------------------------------------

    public const double DefaultSidebarWidth = 248;
    public const double MinSidebarWidth = 180;

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

    /// <summary>Whether the selected tab shows its side panel, which the sidebar leaves the least width for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarDisplayWidth))]
    public partial bool IsSidePanelShown { get; set; }

    /// <summary>The sidebar as shown: the rail, or the width the user dragged as far as there's room for it.</summary>
    public double SidebarDisplayWidth => IsSidebarCollapsed ? RailWidth : Math.Min(SidebarWidth, SidebarRoom);

    /// <summary>
    /// The most the expanded sidebar shows: the shell's width less the conversation's least and, while the selected tab
    /// shows its side panel, the side panel's least. The side panel gives way first, down to its least (each tab's view
    /// keeps it within what the sidebar leaves), and then the sidebar does, down to its own.
    /// </summary>
    private double SidebarRoom =>
        Math.Max(MinSidebarWidth, _availableWidth - MinConversationWidth - (IsSidePanelShown ? MinSidePanelWidth : 0));

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

    /// <summary>Called by the view as the window resizes: a narrow window shows the rail, and the sidebar fits.</summary>
    public void SetAvailableWidth(double width)
    {
        _availableWidth = width;
        OnPropertyChanged(nameof(SidebarDisplayWidth));
        var narrow = width < NarrowWidth;
        if (narrow == _isNarrow)
        {
            return;
        }
        _isNarrow = narrow;
        IsSidebarCollapsed = narrow || _state.SidebarCollapsed;
    }

    /// <summary>
    /// Dragging the sidebar's edge, as far as there's room for it. <see cref="SaveSidebarWidth"/> keeps the result when
    /// the drag ends.
    /// </summary>
    public void ResizeSidebar(double width) => SidebarWidth = Math.Clamp(width, MinSidebarWidth, SidebarRoom);

    public void SaveSidebarWidth()
    {
        _state.SidebarWidth = SidebarWidth;
        _saveState();
    }

    // ---- Side panel (DESIGN.md §3): one width for every tab's -----------------------------------------------

    public const double DefaultSidePanelWidth = 340;
    public const double MinSidePanelWidth = 260;

    /// <summary>
    /// The side panel's width, as the user dragged it. Each tab's view shows it as far as there's room, leaving its
    /// conversation <see cref="MinConversationWidth"/>.
    /// </summary>
    [ObservableProperty]
    public partial double SidePanelWidth { get; private set; } = DefaultSidePanelWidth;

    partial void OnSidePanelWidthChanged(double value) => _sidePanelWidthChanged();

    /// <summary>
    /// Dragging the side panel's edge; the tab's view stops it where its conversation would get too narrow.
    /// <see cref="SaveSidePanelWidth"/> keeps the result when the drag ends.
    /// </summary>
    public void ResizeSidePanel(double width) => SidePanelWidth = Math.Max(width, MinSidePanelWidth);

    public void SaveSidePanelWidth()
    {
        _state.SidePanelWidth = SidePanelWidth;
        _saveState();
    }
}
