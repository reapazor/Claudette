using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Claudette.App.Conversation;
using Claudette.App.Diffs;
using Claudette.App.Services;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Views;

public partial class TabView : UserControl
{
    private const double StickToBottomThreshold = 40;
    private bool _stickToBottom = true;

    /// <summary>The conversation keeps at least this much room beside the side panel, however wide it was dragged.</summary>
    private const double MinConversationWidth = 360;

    /// <summary>Where a drag of the side panel's edge started, and the width then; null when not resizing.</summary>
    private double? _resizeFrom;
    private double _resizeStartWidth;

    public TabView()
    {
        InitializeComponent();
        // Tunnel, so these are seen before the multi-line TextBox turns Enter into a new line.
        AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        // Tunnel too: the list's items take Enter for themselves.
        ChangedFilesList.AddHandler(KeyDownEvent, OnChangedFileKeyDown, RoutingStrategies.Tunnel);
        ConversationScroll.ScrollChanged += OnConversationScrollChanged;
        // Copy on a code block goes through the tab and says "Copied" (DESIGN.md §5, "Copy and times").
        CodeBlockCopy.Attach(this);
        WireComposerAssist();
        SidePanelEdge.PointerPressed += OnSidePanelEdgePressed;
        SidePanelEdge.PointerMoved += OnSidePanelEdgeMoved;
        SidePanelEdge.PointerReleased += (_, e) => EndSidePanelResize(e.Pointer);
        SidePanelEdge.PointerCaptureLost += (_, _) => EndSidePanelResize(null);
        SidePanelEdge.DoubleTapped += (_, _) => ViewModel?.ResetSidePanelWidth();
        SizeChanged += (_, e) => SidePanel.MaxWidth = Math.Max(ShellViewModel.MinSidePanelWidth, e.NewSize.Width - MinConversationWidth);
    }

    private TabViewModel? ViewModel => DataContext as TabViewModel;

    // ---- Resizing the side panel (DESIGN.md §3) -------------------------------------------------------------------

    private void OnSidePanelEdgePressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is null || !e.GetCurrentPoint(SidePanelEdge).Properties.IsLeftButtonPressed || e.ClickCount > 1)
        {
            return;
        }
        // Measured against the whole view, which doesn't move as the edge does, from the width it shows.
        _resizeFrom = e.GetPosition(this).X;
        _resizeStartWidth = SidePanel.Bounds.Width;
        e.Pointer.Capture(SidePanelEdge);
        e.Handled = true;
    }

    /// <summary>The panel is on the right, so dragging its edge left widens it.</summary>
    private void OnSidePanelEdgeMoved(object? sender, PointerEventArgs e)
    {
        if (_resizeFrom is { } from)
        {
            ViewModel?.ResizeSidePanel(Math.Min(_resizeStartWidth + from - e.GetPosition(this).X, SidePanel.MaxWidth));
        }
    }

    private void EndSidePanelResize(IPointer? pointer)
    {
        if (_resizeFrom is null)
        {
            return;
        }
        _resizeFrom = null;
        pointer?.Capture(null);
        ViewModel?.SaveSidePanelWidth();
    }

    /// <summary>The Perforce password prompt opened: type straight into it.</summary>
    private void OnPerforcePasswordAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => box.Focus());
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (ViewModel is not { } tab)
        {
            return;
        }
        if (Shortcuts.Matches(tab.Keyboard, KeyboardShortcuts.Stop, e.Key, e.KeyModifiers) && tab.StopCommand.CanExecute(null))
        {
            tab.StopCommand.Execute(null);
            e.Handled = true;
        }
        else if (Shortcuts.Matches(tab.Keyboard, KeyboardShortcuts.Suffixes, e.Key, e.KeyModifiers))
        {
            SuffixButton.Flyout?.ShowAt(SuffixButton);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Ctrl/Cmd+Enter answers "yes" to the waiting prompt and Ctrl/Cmd+Backspace "no" (DESIGN.md §7), or whatever
    /// Settings → Keyboard says. Not while typing in one of a prompt's own fields, and Backspace keeps deleting words in
    /// a field with text.
    /// </summary>
    private void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { WaitingPrompt: not null } tab)
        {
            return;
        }
        var allow = Shortcuts.Matches(tab.Keyboard, KeyboardShortcuts.AllowPrompt, e.Key, e.KeyModifiers);
        if (!allow && !Shortcuts.Matches(tab.Keyboard, KeyboardShortcuts.DenyPrompt, e.Key, e.KeyModifiers))
        {
            return;
        }
        var focusedBox = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as TextBox;
        if (focusedBox is not null && !ReferenceEquals(focusedBox, Composer)
            || e.Key == Key.Back && !string.IsNullOrEmpty(focusedBox?.Text))
        {
            return;
        }
        e.Handled = allow ? tab.AcceptWaitingPrompt() : tab.DeclineWaitingPrompt();
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (HandleCompletionKey(e))
        {
            return;
        }
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && ViewModel is { } tab)
        {
            e.Handled = true;
            if (tab.SendCommand.CanExecute(null))
            {
                tab.SendCommand.Execute(null);
                _stickToBottom = true;
            }
        }
    }

    /// <summary>Follows new output unless the user has scrolled up; then offers "Jump to latest" (DESIGN.md §5).</summary>
    private void OnConversationScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scroll = ConversationScroll;
        var distanceFromBottom = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
        if (e.ExtentDelta.Y != 0)
        {
            if (_stickToBottom)
            {
                ScrollToEnd();
            }
        }
        else if (e.OffsetDelta.Y != 0)
        {
            _stickToBottom = distanceFromBottom <= StickToBottomThreshold;
        }
        JumpToLatest.IsVisible = !_stickToBottom && distanceFromBottom > StickToBottomThreshold;
    }

    private void OnJumpToLatest(object? sender, RoutedEventArgs e)
    {
        _stickToBottom = true;
        ScrollToEnd();
        JumpToLatest.IsVisible = false;
    }

    private void ScrollToEnd()
    {
        var scroll = ConversationScroll;
        scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_tab is not null)
        {
            _tab.ChangedFiles.DiffRequested -= OnDiffRequested;
            _tab.ScrollToRequested -= OnScrollToRequested;
            _tab.AgentWindowRequested -= OnAgentWindowRequested;
            _tab.ComposerFocusRequested -= OnComposerFocusRequested;
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab.ProjectTools.PropertyChanged -= OnProjectToolsPropertyChanged;
            // The list belonged to that tab.
            TasksChip.Flyout?.Hide();
            _tab.IsTaskListOpen = false;
        }
        _tab = ViewModel;
        if (_tab is not null)
        {
            _tab.ChangedFiles.DiffRequested += OnDiffRequested;
            _tab.ScrollToRequested += OnScrollToRequested;
            _tab.AgentWindowRequested += OnAgentWindowRequested;
            _tab.ComposerFocusRequested += OnComposerFocusRequested;
            _tab.PropertyChanged += OnTabPropertyChanged;
            _tab.ProjectTools.PropertyChanged += OnProjectToolsPropertyChanged;
        }
        WatchProjectOutput();
    }

    private TabViewModel? _tab;

    /// <summary>A message is back in the composer to edit (DESIGN.md §5, "Rewind and branch"): the caret goes after it.</summary>
    private void OnComposerFocusRequested() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Composer.Focus();
        Composer.CaretIndex = Composer.Text?.Length ?? 0;
    });

    /// <summary>The output of the run the Project page shows, which it follows.</summary>
    private System.Collections.ObjectModel.ObservableCollection<string>? _projectOutput;

    /// <summary>The last running task ended while its list was open: the list goes with the chip.</summary>
    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.HasRunningTasks) && _tab is { HasRunningTasks: false })
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => TasksChip.Flyout?.Hide());
        }
    }

    /// <summary>The Project page shows another run.</summary>
    private void OnProjectToolsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectToolsViewModel.SelectedRun))
        {
            WatchProjectOutput();
        }
    }

    /// <summary>The running tasks list is open, so its running times tick (DESIGN.md §5, "Running tasks").</summary>
    private void OnTaskListOpened(object? sender, EventArgs e)
    {
        if (ViewModel is { } tab)
        {
            tab.IsTaskListOpen = true;
        }
    }

    private void OnTaskListClosed(object? sender, EventArgs e)
    {
        if (ViewModel is { } tab)
        {
            tab.IsTaskListOpen = false;
        }
    }

    /// <summary>Another run's log is showing: follow its lines instead, from its newest.</summary>
    private void WatchProjectOutput()
    {
        if (_projectOutput is not null)
        {
            _projectOutput.CollectionChanged -= OnProjectOutputChanged;
        }
        _projectOutput = _tab?.ProjectTools.SelectedRun?.Output;
        if (_projectOutput is not null)
        {
            _projectOutput.CollectionChanged += OnProjectOutputChanged;
            ScrollProjectOutputToEnd();
        }
    }

    /// <summary>The built-in diff view, in its own window so it can stay open beside the conversation (DESIGN.md §8).</summary>
    private void OnDiffRequested(DiffSource source)
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var window = new DiffWindow { DataContext = new DiffWindowViewModel(source, dark) };
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    /// Scrolls to a card, for example the Bash call that started a process, or a subagent's group from the agent map.
    /// A group nested in another only has a container once the groups around it have expanded and been laid out.
    /// </summary>
    private void OnScrollToRequested(ConversationItem item)
    {
        _stickToBottom = false;
        if (item is ToolUseItem tool)
        {
            tool.IsExpanded = true;
        }
        if (ConversationItems.ContainerFromItem(item) is { } container)
        {
            BringTopIntoView(container);
            return;
        }
        // The conversation is virtualized: bring the top-level item that holds it into view first, so it has controls.
        if (ViewModel?.TopLevelItemOf(item) is { } outer)
        {
            ConversationItems.ScrollIntoView(outer);
            if (ReferenceEquals(outer, item) && ConversationItems.ContainerFromItem(item) is { } realized)
            {
                BringTopIntoView(realized);
                return;
            }
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ConversationItems.ContainerFromItem(item) is { } top)
            {
                BringTopIntoView(top);
            }
            else if (ConversationItems.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault(p => ReferenceEquals(p.Content, item)) is { } nested)
            {
                BringTopIntoView(nested);
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The top of a card, so a long expanded group starts at its header.</summary>
    private static void BringTopIntoView(Control container) =>
        container.BringIntoView(new Rect(0, 0, container.Bounds.Width, Math.Min(container.Bounds.Height, 80)));

    private AgentMapWindow? _agentWindow;

    /// <summary>The agent map in a window of its own, beside the conversation (DESIGN.md §18); one per tab.</summary>
    private void OnAgentWindowRequested()
    {
        if (_agentWindow is not null)
        {
            _agentWindow.Activate();
            return;
        }
        var window = _agentWindow = new AgentMapWindow { DataContext = DataContext };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_agentWindow, window))
            {
                _agentWindow = null;
            }
        };
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>The tab closed: its agent map window goes with it.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _agentWindow?.Close();
    }

    /// <summary>
    /// A click opens the built-in diff view (DESIGN.md §8). Not on selection, so the arrow keys move through the list
    /// without opening a window for each file; Enter opens the selected one.
    /// </summary>
    private void OnChangedFileTapped(object? sender, TappedEventArgs e)
    {
        if (ChangedFileAt(e) is { } row && ViewModel?.ChangedFiles is { HasDiffTool: false } files)
        {
            files.OpenFileDiffCommand.Execute(row);
        }
    }

    /// <summary>
    /// A double click opens the diff tool when one is set. Without one, the second click of it opens the built-in view
    /// again, as a single click would (a double click raises no second tap).
    /// </summary>
    private void OnChangedFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ChangedFileAt(e) is not { } row || ViewModel?.ChangedFiles is not { } files)
        {
            return;
        }
        if (files.HasDiffTool)
        {
            files.OpenFileInDiffToolCommand.Execute(row);
        }
        else
        {
            files.OpenFileDiffCommand.Execute(row);
        }
    }

    /// <summary>Enter opens the selected file as a click (or, with a diff tool, a double click) would.</summary>
    private void OnChangedFileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ChangedFilesList.SelectedItem is ChangedFileRow row && ViewModel is { } tab)
        {
            if (tab.ChangedFiles.HasDiffTool)
            {
                tab.ChangedFiles.OpenFileInDiffToolCommand.Execute(row);
            }
            else
            {
                tab.ChangedFiles.OpenFileDiffCommand.Execute(row);
            }
            e.Handled = true;
        }
    }

    /// <summary>The row under a tap, unless it was on one of the row's own controls (the Reviewed box, a button).</summary>
    private static ChangedFileRow? ChangedFileAt(TappedEventArgs e) =>
        e.Source is Control source && source.FindAncestorOfType<Avalonia.Controls.Primitives.ToggleButton>(includeSelf: true) is null
            && source.FindAncestorOfType<Button>(includeSelf: true) is null
            ? source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ChangedFileRow
            : null;

    private void OnShowProcesses(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } tab)
        {
            tab.IsProcessesPage = true;
            tab.IsSidePanelOpen = true;
        }
    }

    /// <summary>A project job's output follows its newest line, as a terminal does.</summary>
    private void OnProjectOutputChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // A batch of lines is one Add, or a Reset when the start was trimmed to keep to the limit.
        if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Add or System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            ScrollProjectOutputToEnd();
        }
    }

    private void ScrollProjectOutputToEnd()
    {
        if (!ProjectOutputList.IsEffectivelyVisible)
        {
            return;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ProjectOutputList.ItemCount > 0)
            {
                ProjectOutputList.ScrollIntoView(ProjectOutputList.ItemCount - 1);
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Closes the dropdown a picked item lives in.</summary>
    private void OnFlyoutItemPicked(object? sender, RoutedEventArgs e) => CloseFlyout(sender as Visual);

    private void OnSuffixPicked(object? sender, RoutedEventArgs e) => CloseFlyout(sender as Visual, () => Composer.Focus());

    /// <summary>Focus goes into the menu, so its number keys work straight away.</summary>
    private void OnSuffixMenuOpened(object? sender, EventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SuffixMenu.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus());

    /// <summary>1–9 pick one of the first nine suffixes (DESIGN.md §5).</summary>
    private void OnSuffixMenuKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None && Shortcuts.Digit(e.Key) is { } number && ViewModel is { } tab && tab.PickSuffix(number))
        {
            SuffixButton.Flyout?.Hide();
            Composer.Focus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Closes the dropdown once the picked item has run its command. A button raises Click before it runs its command,
    /// and closing the dropdown takes the item out of the view, so a command bound through <c>$parent[UserControl]</c>
    /// would be gone by then.
    /// </summary>
    private static void CloseFlyout(Visual? item, Action? then = null)
    {
        if (item?.FindAncestorOfType<FlyoutPresenter>()?.Parent is Popup popup)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                popup.Close();
                then?.Invoke();
            });
        }
    }
}
