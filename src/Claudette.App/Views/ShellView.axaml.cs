using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Views;

public partial class ShellView : UserControl
{
    private TopLevel? _topLevel;
    private ShellViewModel? _shell;
    private ClaudeUpdateViewModel? _updates;

    private const double DragThreshold = 6;

    /// <summary>The tab or group being dragged, or about to be once the pointer moves far enough.</summary>
    private object? _dragItem;
    private Point _dragStart;
    private bool _dragging;

    /// <summary>Where a drag of the sidebar's edge started, and the width then; null when not resizing.</summary>
    private double? _resizeFrom;
    private double _resizeStartWidth;

    public ShellView()
    {
        InitializeComponent();
        // Handled events too: the tab and label buttons handle presses themselves.
        GroupList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        GroupList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        GroupList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        // A folder dragged from Finder or Explorer onto the sidebar opens a tab there (DESIGN.md §4, "Other ways in").
        DragDrop.SetAllowDrop(Sidebar, true);
        Sidebar.AddHandler(DragDrop.DragOverEvent, OnFolderDragOver);
        Sidebar.AddHandler(DragDrop.DropEvent, OnFolderDrop);
        SidebarEdge.PointerPressed += OnEdgePressed;
        SidebarEdge.PointerMoved += OnEdgeMoved;
        SidebarEdge.PointerReleased += (_, e) => EndResize(e.Pointer);
        SidebarEdge.PointerCaptureLost += (_, _) => EndResize(null);
        SidebarEdge.DoubleTapped += (_, _) =>
        {
            ViewModel?.ResizeSidebar(ShellViewModel.DefaultSidebarWidth);
            ViewModel?.SaveSidebarWidth();
        };
        // A narrow window shows the sidebar's rail (DESIGN.md §4, "Sidebar").
        SizeChanged += (_, e) => ViewModel?.SetAvailableWidth(e.NewSize.Width);
    }

    // ---- Resizing the sidebar -------------------------------------------------------------------------------------

    private void OnEdgePressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } shell || !e.GetCurrentPoint(SidebarEdge).Properties.IsLeftButtonPressed || e.ClickCount > 1)
        {
            return;
        }
        // Measured against the whole view, which doesn't move as the edge does.
        _resizeFrom = e.GetPosition(this).X;
        _resizeStartWidth = shell.SidebarWidth;
        e.Pointer.Capture(SidebarEdge);
        e.Handled = true;
    }

    private void OnEdgeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeFrom is { } from)
        {
            ViewModel?.ResizeSidebar(_resizeStartWidth + e.GetPosition(this).X - from);
        }
    }

    private void EndResize(IPointer? pointer)
    {
        if (_resizeFrom is null)
        {
            return;
        }
        _resizeFrom = null;
        pointer?.Capture(null);
        ViewModel?.SaveSidebarWidth();
    }

    private void OnFolderDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = DroppedFolders(e).Any() ? DragDropEffects.Link : DragDropEffects.None;

    private void OnFolderDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } shell)
        {
            return;
        }
        foreach (var folder in DroppedFolders(e))
        {
            _ = shell.OpenFolderAsync(folder);
        }
        e.Handled = true;
    }

    private static IEnumerable<string> DroppedFolders(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? []).Select(item => item.TryGetLocalPath()).OfType<string>().Where(Directory.Exists);

    // ---- Dragging tabs and groups (DESIGN.md §4) ------------------------------------------------------------------

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragItem = null;
        _dragging = false;
        if (!e.GetCurrentPoint(GroupList).Properties.IsLeftButtonPressed)
        {
            return;
        }
        // The innermost button: a tab's close button or the group's + don't start a drag.
        var button = (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true);
        _dragItem = button switch
        {
            { DataContext: TabViewModel { IsRenaming: false } tab } when button.Classes.Contains("tab") => tab,
            { DataContext: TabGroupViewModel group } when button.Classes.Contains("grouplabel") => group,
            _ => null,
        };
        _dragStart = e.GetPosition(GroupList);
    }

    /// <summary>The dragged item moves as soon as the pointer passes the middle of a neighbor, so the list shows the result live.</summary>
    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null || ViewModel is not { } shell)
        {
            return;
        }
        var y = e.GetPosition(GroupList).Y;
        if (!_dragging)
        {
            if (Math.Abs(y - _dragStart.Y) < DragThreshold)
            {
                return;
            }
            _dragging = true;
            if (_dragItem is TabViewModel dragged)
            {
                shell.SelectCommand.Execute(dragged);
            }
        }
        switch (_dragItem)
        {
            case TabViewModel tab when shell.Groups.FirstOrDefault(g => g.Tabs.Contains(tab)) is { } group:
                {
                    var buttons = GroupList.GetVisualDescendants().OfType<Button>()
                        .Where(b => b.Classes.Contains("tab") && b.DataContext is TabViewModel t && group.Tabs.Contains(t));
                    if (TargetIndex(buttons, y, b => group.Tabs.IndexOf((TabViewModel)b.DataContext!), group.Tabs.IndexOf(tab)) is { } index)
                    {
                        shell.MoveTabTo(tab, index);
                    }
                    break;
                }
            case TabGroupViewModel group:
                {
                    var containers = Enumerable.Range(0, shell.Groups.Count).Select(i => GroupList.ContainerFromIndex(i)).OfType<Control>();
                    if (TargetIndex(containers, y, c => shell.Groups.IndexOf((TabGroupViewModel)c.DataContext!), shell.Groups.IndexOf(group)) is { } index)
                    {
                        shell.MoveGroupTo(group, index);
                    }
                    break;
                }
        }
    }

    /// <summary>
    /// The index of the item the pointer has moved past the middle of, in the direction of travel, or null to stay put.
    /// </summary>
    private int? TargetIndex(IEnumerable<Control> items, double y, Func<Control, int> indexOf, int from)
    {
        foreach (var item in items)
        {
            if (item.TranslatePoint(default, GroupList) is not { } origin)
            {
                continue;
            }
            var index = indexOf(item);
            var middle = origin.Y + item.Bounds.Height / 2;
            if (index > from && y > middle && y < origin.Y + item.Bounds.Height || index < from && y < middle && y > origin.Y)
            {
                return index;
            }
        }
        return null;
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            // Releasing the capture un-presses the button without a click, so dropping a group doesn't also collapse it.
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        _dragItem = null;
        _dragging = false;
    }

    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    /// <summary>
    /// <b>Change color…</b> in a group's menu opens its color picker beside the group's label, in the sidebar or the rail
    /// (DESIGN.md §4, "Grouped by folder"). Posted, so the menu has closed before the picker opens.
    /// </summary>
    private void OnChangeGroupColor(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } shell || (sender as MenuItem)?.DataContext is not TabGroupViewModel group)
        {
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var label = this.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains("grouplabel") && b.DataContext == group && b.IsEffectivelyVisible);
            if (label is null)
            {
                return;
            }
            var picker = shell.PickGroupColor(group);
            var flyout = new Flyout { Placement = PlacementMode.RightEdgeAlignedTop, Content = new GroupColorPicker { DataContext = picker } };
            picker.Done += flyout.Hide;
            FlyoutBase.SetAttachedFlyout(label, flyout);
            flyout.ShowAt(label);
        });
    }

    /// <summary>
    /// The project's menu opened: look at the project's files again, so what's enabled is current (a solution generated
    /// from a terminal, say). The menu updates in place when that's done (DESIGN.md §18).
    /// </summary>
    private void OnProjectMenuOpened(object? sender, EventArgs e) => ViewModel?.SelectedTab?.ProjectTools.RefreshCommand.Execute(null);

    /// <summary>An entry of the project's menu was picked: the menu closes, as a menu does.</summary>
    /// <summary>Closes the menu once the item has run its command, which a button does after raising Click.</summary>
    private void OnProjectMenuItemPicked(object? sender, RoutedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ProjectButton.Flyout?.Hide());

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellChanged;
        }
        _shell = ViewModel;
        if (_shell is not null)
        {
            _shell.ShowSettingsWindow = ShowSettingsAsync;
            _shell.PropertyChanged += OnShellChanged;
            if (Bounds.Width > 0)
            {
                _shell.SetAvailableWidth(Bounds.Width);
            }
        }
        WatchUpdates();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Updates))
        {
            WatchUpdates();
        }
    }

    /// <summary>A clicked "update ready" notification opens the badge's dialog (DESIGN.md §12).</summary>
    private void WatchUpdates()
    {
        if (_updates is not null)
        {
            _updates.OpenRequested -= OpenUpdateDialog;
        }
        _updates = _shell?.Updates;
        if (_updates is not null)
        {
            _updates.OpenRequested += OpenUpdateDialog;
        }
    }

    private void OpenUpdateDialog()
    {
        if (UpdateBadge.IsVisible)
        {
            UpdateBadge.Flyout?.ShowAt(UpdateBadge);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        // Tunnel, so shortcuts work wherever the focus is (DESIGN.md §4, "Keyboard").
        _topLevel?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// The window's shortcuts, as set in Settings → Keyboard (DESIGN.md §4, §9, §14), and each quick suffix's own
    /// shortcut (§5).
    /// </summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } shell)
        {
            return;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && shell.IsPickerOpen)
        {
            shell.ClosePicker();
            e.Handled = true;
            return;
        }
        var keyboard = shell.Keyboard;
        bool Is(string id) => Shortcuts.Matches(keyboard, id, e.Key, e.KeyModifiers);

        if (Is(KeyboardShortcuts.NextTab))
        {
            shell.SelectNextCommand.Execute(null);
        }
        else if (Is(KeyboardShortcuts.PreviousTab))
        {
            shell.SelectPreviousCommand.Execute(null);
        }
        else if (Is(KeyboardShortcuts.NewTab))
        {
            shell.NewTabCommand.Execute(null);
        }
        else if (Is(KeyboardShortcuts.CloseTab))
        {
            shell.CloseSelectedTabCommand.Execute(null);
        }
        else if (Is(KeyboardShortcuts.History))
        {
            shell.OpenHistoryCommand.Execute(null);
        }
        else if (Is(KeyboardShortcuts.Settings))
        {
            shell.OpenSettingsCommand.Execute(null);
        }
        else if (Is(KeyboardShortcuts.ToggleSidebar))
        {
            shell.ToggleSidebarCommand.Execute(null);
        }
        else if (Is(KeyboardShortcuts.Find) && shell.SelectedTab is { } findTab)
        {
            findTab.OpenFindCommand.Execute(null);
        }
        else if (Is(KeyboardShortcuts.RunProjectAction) && shell.SelectedTab is { } projectTab)
        {
            // Launch the editor, for Unreal (DESIGN.md §18, "Project tools").
            projectTab.ProjectTools.RunMainActionCommand.Execute(null);
        }
        else if (Is(KeyboardShortcuts.GoToTab) && Shortcuts.Digit(e.Key) is { } number)
        {
            shell.SelectNumber(number);
        }
        else if (shell.SelectedTab is { } tab && shell.QuickSuffixes.FirstOrDefault(s => KeyChord.TryParse(s.Shortcut, out var chord) && Shortcuts.Matches(chord, e.Key, e.KeyModifiers)) is { } suffix)
        {
            tab.AddSuffixCommand.Execute(suffix);
        }
        else
        {
            return;
        }
        e.Handled = true;
    }

    private void OnTabDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TabViewModel tab)
        {
            tab.StartRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.PropertyChanged += (_, args) =>
            {
                if (args.Property == IsVisibleProperty && box.IsVisible)
                {
                    box.Focus();
                    box.SelectAll();
                }
            };
        }
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TabViewModel tab)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            tab.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            tab.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TabViewModel { IsRenaming: true } tab)
        {
            tab.CommitRenameCommand.Execute(null);
        }
    }

    /// <summary>Clicking the dimmed area around the picker closes it.</summary>
    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender))
        {
            ViewModel?.ClosePicker();
        }
    }

    /// <summary>The Settings window, modal, where <paramref name="opening"/> says, with the selected tab's project pages.</summary>
    private async Task ShowSettingsAsync(SettingsOpening opening)
    {
        if (this.FindAncestorOfType<Window>() is not { } owner || owner.DataContext is not MainWindowViewModel main)
        {
            opening.Project?.Dispose();
            return;
        }
        var settings = new SettingsViewModel(main.Services, main.AccountText, main.Updates, opening) { Account = main.Account, AppUpdates = main.AppUpdate };
        var window = new SettingsWindow { DataContext = settings };
        await window.ShowDialog(owner);
    }
}
