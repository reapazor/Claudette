using System.ComponentModel;
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

    public TabView()
    {
        InitializeComponent();
        // Tunnel, so these are seen before the multi-line TextBox turns Enter into a new line.
        AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        ConversationScroll.ScrollChanged += OnConversationScrollChanged;
        // Copy on a code block goes through the tab and says "Copied" (DESIGN.md §5, "Copy and times").
        CodeBlockCopy.Attach(this);
        WireComposerAssist();
    }

    private TabViewModel? ViewModel => DataContext as TabViewModel;

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
            _tab.DiffRequested -= OnDiffRequested;
            _tab.ScrollToRequested -= OnScrollToRequested;
            _tab.AgentWindowRequested -= OnAgentWindowRequested;
            _tab.ProjectOutput.CollectionChanged -= OnProjectOutputChanged;
            _tab.PropertyChanged -= OnTabPropertyChanged;
            // The list belonged to that tab.
            TasksChip.Flyout?.Hide();
            _tab.IsTaskListOpen = false;
        }
        _tab = ViewModel;
        if (_tab is not null)
        {
            _tab.DiffRequested += OnDiffRequested;
            _tab.ScrollToRequested += OnScrollToRequested;
            _tab.AgentWindowRequested += OnAgentWindowRequested;
            _tab.ProjectOutput.CollectionChanged += OnProjectOutputChanged;
            _tab.PropertyChanged += OnTabPropertyChanged;
        }
    }

    private TabViewModel? _tab;

    /// <summary>The last running task ended while its list was open: the list goes with the chip.</summary>
    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.HasRunningTasks) && _tab is { HasRunningTasks: false })
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => TasksChip.Flyout?.Hide());
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
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ConversationItems.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault(p => ReferenceEquals(p.Content, item)) is { } nested)
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

    /// <summary>Without a diff tool, selecting a file opens the built-in diff view; with one, double-click opens the tool (DESIGN.md §8).</summary>
    private void OnChangedFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (ChangedFilesList.SelectedItem is ChangedFileRow row && ViewModel is { HasDiffTool: false } tab)
        {
            tab.OpenFileDiffCommand.Execute(row);
            // Clear it, so clicking the same file again opens it again.
            ChangedFilesList.SelectedItem = null;
        }
    }

    private void OnChangedFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ChangedFileAt(e) is { } row && ViewModel is { HasDiffTool: true } tab)
        {
            tab.OpenFileInDiffToolCommand.Execute(row);
        }
    }

    private void OnChangedFileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ChangedFilesList.SelectedItem is ChangedFileRow row && ViewModel is { } tab)
        {
            tab.OpenFileCommand.Execute(row);
            e.Handled = true;
        }
    }

    private static ChangedFileRow? ChangedFileAt(TappedEventArgs e) =>
        (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ChangedFileRow;

    private void OnShowProcesses(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } tab)
        {
            tab.IsProcessesPage = true;
            tab.IsSidePanelOpen = true;
        }
    }

    /// <summary>
    /// The project chip's menu opened: look at the project's files again, so what's enabled is current (a solution
    /// generated from a terminal, say). The menu updates in place when that's done.
    /// </summary>
    private void OnProjectMenuOpened(object? sender, EventArgs e) => ViewModel?.RefreshProjectCommand.Execute(null);

    /// <summary>A project job's output follows its newest line, as a terminal does.</summary>
    private void OnProjectOutputChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && ProjectOutputList.IsEffectivelyVisible)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ProjectOutputList.ItemCount > 0)
                {
                    ProjectOutputList.ScrollIntoView(ProjectOutputList.ItemCount - 1);
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Closes the dropdown a picked item lives in.</summary>
    private void OnFlyoutItemPicked(object? sender, RoutedEventArgs e) => CloseFlyout(sender as Visual);

    private void OnSuffixPicked(object? sender, RoutedEventArgs e)
    {
        CloseFlyout(sender as Visual);
        Composer.Focus();
    }

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

    private static void CloseFlyout(Visual? item)
    {
        if (item?.FindAncestorOfType<FlyoutPresenter>()?.Parent is Popup popup)
        {
            popup.Close();
        }
    }
}
