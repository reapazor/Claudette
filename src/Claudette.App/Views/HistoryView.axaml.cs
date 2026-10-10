using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Claudette.App.ViewModels;

namespace Claudette.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SearchBox.Focus();
    }

    /// <summary>A project picked from "+N more": the list closes once the pick has gone through.</summary>
    private void OnMoreProjectPicked(object? sender, RoutedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => MoreProjectsButton.Flyout?.Hide());

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && DataContext is HistoryViewModel history)
        {
            history.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.Source == SearchBox && DataContext is HistoryViewModel searching && searching.SearchRepliesCommand.CanExecute(null))
        {
            // Enter in the search box looks through Claude's replies too (DESIGN.md §9, "History").
            searching.SearchRepliesCommand.Execute(null);
            e.Handled = true;
        }
    }
}
