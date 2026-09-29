using Avalonia;
using Avalonia.Controls;
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

    private const double DragThreshold = 6;

    /// <summary>The tab or group being dragged, or about to be once the pointer moves far enough.</summary>
    private object? _dragItem;
    private Point _dragStart;
    private bool _dragging;

    public ShellView()
    {
        InitializeComponent();
        // Handled events too: the tab and label buttons handle presses themselves.
        GroupStrip.AddHandler(PointerPressedEvent, OnStripPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        GroupStrip.AddHandler(PointerMovedEvent, OnStripPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        GroupStrip.AddHandler(PointerReleasedEvent, OnStripPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        // A folder dragged from Finder or Explorer onto the tab strip opens a tab there (DESIGN.md §4, "Other ways in").
        DragDrop.SetAllowDrop(TabStrip, true);
        TabStrip.AddHandler(DragDrop.DragOverEvent, OnFolderDragOver);
        TabStrip.AddHandler(DragDrop.DropEvent, OnFolderDrop);
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

    private void OnStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragItem = null;
        _dragging = false;
        if (!e.GetCurrentPoint(GroupStrip).Properties.IsLeftButtonPressed)
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
        _dragStart = e.GetPosition(GroupStrip);
    }

    /// <summary>The dragged item moves as soon as the pointer passes the middle of a neighbor, so the strip shows the result live.</summary>
    private void OnStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null || ViewModel is not { } shell)
        {
            return;
        }
        var x = e.GetPosition(GroupStrip).X;
        if (!_dragging)
        {
            if (Math.Abs(x - _dragStart.X) < DragThreshold)
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
                    var buttons = GroupStrip.GetVisualDescendants().OfType<Button>()
                        .Where(b => b.Classes.Contains("tab") && b.DataContext is TabViewModel t && group.Tabs.Contains(t));
                    if (TargetIndex(buttons, x, b => group.Tabs.IndexOf((TabViewModel)b.DataContext!), group.Tabs.IndexOf(tab)) is { } index)
                    {
                        shell.MoveTabTo(tab, index);
                    }
                    break;
                }
            case TabGroupViewModel group:
                {
                    var containers = Enumerable.Range(0, shell.Groups.Count).Select(i => GroupStrip.ContainerFromIndex(i)).OfType<Control>();
                    if (TargetIndex(containers, x, c => shell.Groups.IndexOf((TabGroupViewModel)c.DataContext!), shell.Groups.IndexOf(group)) is { } index)
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
    private int? TargetIndex(IEnumerable<Control> items, double x, Func<Control, int> indexOf, int from)
    {
        foreach (var item in items)
        {
            if (item.TranslatePoint(default, GroupStrip) is not { } origin)
            {
                continue;
            }
            var index = indexOf(item);
            var middle = origin.X + item.Bounds.Width / 2;
            if (index > from && x > middle && x < origin.X + item.Bounds.Width || index < from && x < middle && x > origin.X)
            {
                return index;
            }
        }
        return null;
    }

    private void OnStripPointerReleased(object? sender, PointerReleasedEventArgs e)
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

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } shell)
        {
            shell.ShowSettingsWindow = ShowSettingsAsync;
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

    private async Task ShowSettingsAsync(string? category)
    {
        if (this.FindAncestorOfType<Window>() is not { } owner || owner.DataContext is not MainWindowViewModel main)
        {
            return;
        }
        var settings = new SettingsViewModel(main.Services, main.AccountText, main.Updates);
        if (category is not null && SettingsViewModel.AllCategories.Contains(category))
        {
            settings.SelectedCategory = category;
        }
        var window = new SettingsWindow { DataContext = settings };
        await window.ShowDialog(owner);
    }
}
