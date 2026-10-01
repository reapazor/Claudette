using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Claudette.App.ViewModels;

namespace Claudette.App.Views;

/// <summary>
/// A tab's side panel (DESIGN.md §3): what its pages need beyond bindings, which is the menu of the pages that don't
/// fit, opening a changed file and following a project job's output.
/// </summary>
public partial class SidePanelView : UserControl
{
    private TabViewModel? _tab;

    /// <summary>The output of the run the Project page shows, which it follows.</summary>
    private ObservableCollection<string>? _projectOutput;

    /// <summary>A scroll to the newest line is on its way: batches until then go with it.</summary>
    private bool _projectScrollPending;

    public SidePanelView()
    {
        InitializeComponent();
        // Tunnel: the list's items take Enter for themselves.
        ChangedFilesList.AddHandler(KeyDownEvent, OnChangedFileKeyDown, RoutingStrategies.Tunnel);
    }

    private TabViewModel? ViewModel => DataContext as TabViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_tab is not null)
        {
            _tab.ProjectTools.Runs.PropertyChanged -= OnProjectRunsPropertyChanged;
        }
        _tab = ViewModel;
        if (_tab is not null)
        {
            _tab.ProjectTools.Runs.PropertyChanged += OnProjectRunsPropertyChanged;
        }
        WatchProjectOutput();
    }

    // ---- The pages that don't fit (DESIGN.md §3, "Side panel") ------------------------------------------------------

    /// <summary>The menu lists the tabs left out of the row, each with its badge and dot.</summary>
    private void OnMorePagesOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu)
        {
            return;
        }
        menu.Items.Clear();
        foreach (var tab in PageTabs.Overflow.OfType<Button>())
        {
            var dot = StatusDot(tab);
            var texts = tab.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && t != dot).Select(t => t.Text);
            menu.Items.Add(new MenuItem { Header = string.Join(" · ", texts), Command = tab.Command, Icon = dot is null ? null : DotLike(dot) });
        }
    }

    private static TextBlock DotLike(TextBlock dot)
    {
        var copy = new TextBlock { Text = dot.Text, FontSize = dot.FontSize, VerticalAlignment = VerticalAlignment.Center };
        copy.Classes.AddRange(StyleClasses(dot));
        return copy;
    }

    /// <summary>The button after the row shows a dot while a tab left out of it shows one.</summary>
    private void OnPageTabsLayoutUpdated(object? sender, EventArgs e)
    {
        var dot = PageTabs.Overflow.Select(StatusDot).FirstOrDefault(d => d is not null);
        MorePagesDot.IsVisible = dot is not null;
        if (dot is not null && !StyleClasses(MorePagesDot).SequenceEqual(StyleClasses(dot)))
        {
            MorePagesDot.Classes.Replace(StyleClasses(dot));
        }
    }

    private static TextBlock? StatusDot(Control tab) =>
        tab.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsVisible && t.Classes.Contains("status"));

    /// <summary>A control's classes without its pseudo-classes, which only the control sets.</summary>
    private static List<string> StyleClasses(Control control) => [.. control.Classes.Where(c => !c.StartsWith(':'))];

    // ---- Changed files (DESIGN.md §8) ------------------------------------------------------------------------------

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
        e.Source is Control source && source.FindAncestorOfType<ToggleButton>(includeSelf: true) is null
            && source.FindAncestorOfType<Button>(includeSelf: true) is null
            ? source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ChangedFileRow
            : null;

    // ---- A project job's output (DESIGN.md §18) --------------------------------------------------------------------

    /// <summary>The Project page shows another run.</summary>
    private void OnProjectRunsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectRunsViewModel.SelectedRun))
        {
            WatchProjectOutput();
        }
    }

    /// <summary>Another run's log is showing: follow its lines instead, from its newest.</summary>
    private void WatchProjectOutput()
    {
        if (_projectOutput is not null)
        {
            _projectOutput.CollectionChanged -= OnProjectOutputChanged;
        }
        _projectOutput = _tab?.ProjectTools.Runs.SelectedRun?.Output;
        if (_projectOutput is not null)
        {
            _projectOutput.CollectionChanged += OnProjectOutputChanged;
            ScrollProjectOutputToEnd();
        }
    }

    /// <summary>A project job's output follows its newest line, as a terminal does.</summary>
    private void OnProjectOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A batch of lines is one Add (after a Remove when the oldest were dropped), or a Reset when it replaced them all.
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            ScrollProjectOutputToEnd();
        }
    }

    private void ScrollProjectOutputToEnd()
    {
        if (!ProjectOutputList.IsEffectivelyVisible || _projectScrollPending)
        {
            return;
        }
        _projectScrollPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _projectScrollPending = false;
            if (ProjectOutputList.ItemCount > 0)
            {
                ProjectOutputList.ScrollIntoView(ProjectOutputList.ItemCount - 1);
            }
        }, DispatcherPriority.Background);
    }
}
