using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Claudette.App.ViewModels;

namespace Claudette.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private UsageWindow? _usageWindow;

    public MainWindow()
    {
        InitializeComponent();
        UseMicaOnWindows11();
        // Notifications are skipped for what's already in front of the user (DESIGN.md §10).
        Activated += (_, _) => _viewModel?.Services.Notifications.SetAppActive(true);
        Deactivated += (_, _) => _viewModel?.Services.Notifications.SetAppActive(false);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.BringToFrontRequested -= BringToFront;
        }
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.BringToFrontRequested += BringToFront;
            _viewModel.Services.Notifications.SetAppActive(IsActive);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.Usage) && _viewModel?.Usage is { } usage)
        {
            usage.ShowUsagePanel = ShowUsagePanelAsync;
        }
    }

    /// <summary>
    /// The Mica backdrop on Windows 11 (DESIGN.md §2). Only once Windows actually grants it does the window's background
    /// go transparent: the header, sidebar and title bar then show Mica, and the page keeps an opaque background.
    /// </summary>
    private void UseMicaOnWindows11()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }
        TransparencyLevelHint = [WindowTransparencyLevel.Mica];
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty || e.Property == ActualThemeVariantProperty)
            {
                ApplyBackdrop();
            }
        };
    }

    private void ApplyBackdrop()
    {
        if (ActualTransparencyLevel == WindowTransparencyLevel.Mica)
        {
            Background = Brushes.Transparent;
            Resources["PageBackgroundBrush"] = this.FindResource(ActualThemeVariant, "MicaPageBrush");
            Resources["SidebarBrush"] = this.FindResource(ActualThemeVariant, "MicaSidebarBrush");
        }
        else
        {
            ClearValue(BackgroundProperty);
            Resources.Remove("PageBackgroundBrush");
            Resources.Remove("SidebarBrush");
        }
    }

    /// <summary>For a clicked notification: restore the window if minimized and bring it to the front.</summary>
    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        // Toggling Topmost gets past Windows' foreground lock, which otherwise only flashes the taskbar button.
        Topmost = true;
        Topmost = false;
        Activate();
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
