using Avalonia.Controls;
using Claudette.App.Diffs;

namespace Claudette.App.Views;

public partial class DiffWindow : Window
{
    private DiffWindowViewModel? _viewModel;

    public DiffWindow() => InitializeComponent();

    /// <summary><b>Reviewed</b> closes the window (DESIGN.md §8, "Reviewed").</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= Close;
        }
        _viewModel = DataContext as DiffWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += Close;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= Close;
        }
    }
}
