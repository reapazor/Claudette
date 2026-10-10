using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Claudette.App.Services;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Views;

/// <summary>
/// A tab's composer (DESIGN.md §5): Enter to send, Up and Down through earlier prompts, the suffix menu, the running
/// tasks list and the tokens flyout. The autocomplete and attachments are in <c>ComposerView.Assist.cs</c>.
/// </summary>
public partial class ComposerView : UserControl
{
    private TabViewModel? _tab;

    public ComposerView()
    {
        InitializeComponent();
        // Tunnel, so these are seen before the multi-line TextBox turns Enter into a new line.
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        WireComposerAssist();
    }

    /// <summary>A message was sent from the composer: the conversation follows it to the bottom.</summary>
    public event Action? Sent;

    private TabViewModel? ViewModel => DataContext as TabViewModel;

    /// <summary>The composer's text box has the focus.</summary>
    internal bool HasFocus(object? focused) => ReferenceEquals(focused, Composer);

    /// <summary>The focus goes back to the composer, with the caret at its end when <paramref name="caretToEnd"/>.</summary>
    internal void FocusComposer(bool caretToEnd = false)
    {
        Composer.Focus();
        if (caretToEnd)
        {
            Composer.CaretIndex = Composer.Text?.Length ?? 0;
        }
    }

    /// <summary>Opens the quick suffixes menu (DESIGN.md §5), as its shortcut does.</summary>
    internal void ShowSuffixes() => SuffixButton.Flyout?.ShowAt(SuffixButton);

    /// <summary>
    /// What the conversation can show just above the composer: Claudette on the composer keeps clear of it, or ducks
    /// behind the box while it spans the box (DESIGN.md §5).
    /// </summary>
    internal void KeepMascotClearOf(IEnumerable<Control> controls) => MascotLayer.KeepClearOf(controls);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_tab is not null)
        {
            _tab.ComposerFocusRequested -= OnComposerFocusRequested;
            _tab.PropertyChanged -= OnTabPropertyChanged;
            // The list belonged to that tab.
            TasksChip.Flyout?.Hide();
            _tab.IsTaskListOpen = false;
        }
        _tab = ViewModel;
        if (_tab is not null)
        {
            _tab.ComposerFocusRequested += OnComposerFocusRequested;
            _tab.PropertyChanged += OnTabPropertyChanged;
        }
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (HandleCompletionKey(e))
        {
            return;
        }
        if (HandleRecallKey(e))
        {
            return;
        }
        if (ViewModel is not { } tab)
        {
            return;
        }
        if (e.Key == Key.V && Shortcuts.Matches(PasteAsTextChord, e.Key, e.KeyModifiers))
        {
            // The clipboard's text in the box as it is, however long (DESIGN.md §5, "Attachments").
            e.Handled = true;
            _ = PasteAsTextAsync(tab);
            return;
        }
        if (e.Key == Key.Enter && IsSendKey(tab, e.KeyModifiers))
        {
            e.Handled = true;
            if (tab.SendCommand.CanExecute(null))
            {
                tab.SendCommand.Execute(null);
                Sent?.Invoke();
            }
        }
    }

    private static readonly KeyChord PasteAsTextChord = new(ChordModifiers.Primary | ChordModifiers.Shift, "V");

    private static readonly KeyChord PrimaryEnter = new(ChordModifiers.Primary, "Enter");

    /// <summary>
    /// Settings → Keyboard → <b>Send with</b> (DESIGN.md §14): Enter, with Shift+Enter for a new line, or Ctrl/Cmd+Enter,
    /// with Enter for one. The text box makes the new line itself.
    /// </summary>
    private static bool IsSendKey(TabViewModel tab, KeyModifiers modifiers) => tab.Keyboard.SendKey == SendKey.PrimaryEnter
        ? Shortcuts.Matches(PrimaryEnter, Key.Enter, modifiers)
        : !modifiers.HasFlag(KeyModifiers.Shift);

    private async Task PasteAsTextAsync(TabViewModel tab)
    {
        if (await tab.ClipboardTextAsync() is not { Length: > 0 } text)
        {
            return;
        }
        var current = Composer.Text ?? "";
        var start = Math.Clamp(Math.Min(Composer.SelectionStart, Composer.SelectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(Composer.SelectionStart, Composer.SelectionEnd), 0, current.Length);
        Composer.Text = current[..start] + text + current[end..];
        Composer.CaretIndex = start + text.Length;
        Composer.ClearSelection();
    }

    /// <summary>
    /// Up on the composer's first line goes back through the tab's earlier prompts, as a terminal's history does, and Down
    /// on its last line comes forward again, back to what was typed (DESIGN.md §5, "Composer").
    /// </summary>
    private bool HandleRecallKey(KeyEventArgs e)
    {
        if (ViewModel is not { } tab || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Up or Key.Down))
        {
            return false;
        }
        var text = Composer.Text ?? "";
        var caret = Math.Clamp(Composer.CaretIndex, 0, text.Length);
        var recalled = e.Key == Key.Up
            ? (caret == 0 || text.LastIndexOf('\n', caret - 1) < 0) && tab.RecallOlderPrompt()
            : tab.IsRecallingPrompt && text.IndexOf('\n', caret) < 0 && tab.RecallNewerPrompt();
        if (!recalled)
        {
            return false;
        }
        Composer.CaretIndex = Composer.Text?.Length ?? 0;
        e.Handled = true;
        return true;
    }

    /// <summary>A message is back in the composer to edit (DESIGN.md §5, "Rewind and branch"): the caret goes after it.</summary>
    private void OnComposerFocusRequested() => Dispatcher.UIThread.Post(() => FocusComposer(caretToEnd: true));

    /// <summary>The last running task ended while its list was open: the list goes with the chip.</summary>
    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.HasRunningTasks) && _tab is { HasRunningTasks: false })
        {
            Dispatcher.UIThread.Post(() => TasksChip.Flyout?.Hide());
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

    /// <summary>The tokens flyout's window and chart are read from the usage history only while it's open.</summary>
    private void OnTokenDetailsOpened(object? sender, EventArgs e)
    {
        if (ViewModel is { } tab)
        {
            tab.Context.IsTokenDetailsOpen = true;
        }
    }

    private void OnTokenDetailsClosed(object? sender, EventArgs e)
    {
        if (ViewModel is { } tab)
        {
            tab.Context.IsTokenDetailsOpen = false;
        }
    }

    private void OnShowProcesses(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } tab)
        {
            tab.IsProcessesPage = true;
            tab.IsSidePanelOpen = true;
        }
    }

    /// <summary>Closes the dropdown a picked item lives in.</summary>
    private void OnFlyoutItemPicked(object? sender, RoutedEventArgs e) => CloseFlyout(sender as Visual);

    /// <summary>The stash's entries say how long ago each was stashed: up to date as its menu opens (DESIGN.md §5).</summary>
    private void OnStashMenuOpened(object? sender, EventArgs e) => ViewModel?.RefreshStash();

    private void OnSuffixPicked(object? sender, RoutedEventArgs e) => CloseFlyout(sender as Visual, () => Composer.Focus());

    /// <summary>Focus goes into the menu, so its number keys work straight away.</summary>
    private void OnSuffixMenuOpened(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => SuffixMenu.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus());

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
            Dispatcher.UIThread.Post(() =>
            {
                popup.Close();
                then?.Invoke();
            });
        }
    }
}
