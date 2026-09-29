using System.ComponentModel;
using Avalonia.Controls;
using Claudette.App.ViewModels;

namespace Claudette.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private UsageWindow? _usageWindow;

    public MainWindow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.Usage) && _viewModel?.Usage is { } usage)
        {
            usage.ShowUsagePanel = ShowUsagePanelAsync;
        }
    }

    /// <summary>The Usage panel (DESIGN.md §6). One at a time; clicking the header again brings it forward.</summary>
    private Task ShowUsagePanelAsync()
    {
        if (_viewModel is not { Usage: { } header, Services.Usage: { } tracker } main)
        {
            return Task.CompletedTask;
        }
        if (_usageWindow is not null)
        {
            (_usageWindow.DataContext as UsagePanelViewModel)?.Refresh();
            _usageWindow.Activate();
            return Task.CompletedTask;
        }
        var panel = new UsagePanelViewModel(main.Services, tracker, header, id => main.Shell?.AllTabs.FirstOrDefault(t => t.Id == id)?.DisplayName);
        tracker.TurnRecorded += panel.Refresh;
        _usageWindow = new UsageWindow { DataContext = panel };
        _usageWindow.Closed += (_, _) =>
        {
            tracker.TurnRecorded -= panel.Refresh;
            _usageWindow = null;
        };
        _usageWindow.Show(this);
        return Task.CompletedTask;
    }
}
