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
        Activated += (_, _) =>
        {
            _viewModel?.Services.Notifications.SetAppActive(true);
            // Back from the OS's settings, perhaps with Reduce motion changed (DESIGN.md §3, "Accessibility").
            _ = _viewModel?.Services.ReadMotionPreferencesAsync(onlyIfStale: true);
            // Back from an editor, perhaps with claudette.json changed: the actions and links follow (DESIGN.md §18).
            if (_viewModel?.Shell?.SelectedTab is { } tab)
            {
                _ = tab.ProjectTools.RefreshFileAsync();
            }
            // Back from a terminal, perhaps, with a branch switched there: the tab rows follow (DESIGN.md §4, "Sidebar").
            foreach (var each in _viewModel?.Shell?.AllTabs ?? [])
            {
                each.RefreshBranch();
            }
            // Back from another machine, perhaps, with a scratch pad changed there (DESIGN.md §18, "Scratch pad").
            _viewModel?.Services.ScratchPads.OnAppActivated();
        };
        Deactivated += (_, _) => _viewModel?.Services.Notifications.SetAppActive(false);
        // A narrow window leaves the busiest tabs, then the weekly chart, out of the detailed header (DESIGN.md §6).
        SizeChanged += (_, e) => _viewModel?.Usage?.SetDetailsWidth(e.NewSize.Width / _zoom);
    }

    private int _zoomPercent = 100;

    private double _zoom = 1;

    /// <summary>
    /// Scales the window's content (DESIGN.md §3, "Accessibility"): <paramref name="percent"/> of its size, 100 for none.
    /// Popups and menus keep their size.
    /// </summary>
    public void UseZoom(int percent)
    {
        if (percent == _zoomPercent)
        {
            return;
        }
        _zoomPercent = percent;
        _zoom = percent / 100.0;
        Zoomed.LayoutTransform = percent == 100 ? null : new ScaleTransform(_zoom, _zoom);
        if (_viewModel?.Usage is { } usage && Bounds.Width > 0)
        {
            usage.SetDetailsWidth(Bounds.Width / _zoom);
        }
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
            if (_viewModel.Usage is { } usage)
            {
                UseUsage(usage);
            }
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.Usage) && _viewModel?.Usage is { } usage)
        {
            UseUsage(usage);
        }
    }

    private void UseUsage(UsageViewModel usage)
    {
        usage.ShowUsagePanel = ShowUsagePanelAsync;
        if (Bounds.Width > 0)
        {
            usage.SetDetailsWidth(Bounds.Width / _zoom);
        }
    }

    /// <summary>
    /// Asks for the Mica backdrop on Windows 11, or stops asking. Claude's colors (DESIGN.md §3, "Visual style") are
    /// solid, so the warm sidebar and header aren't replaced by the desktop showing through.
    /// </summary>
    public void UseMica(bool use)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            TransparencyLevelHint = use ? [WindowTransparencyLevel.Mica] : [];
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
        UseMica(true);
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
        void OnTurnRecorded(string tabId) => panel.Refresh();
        tracker.TurnRecorded += OnTurnRecorded;
        tracker.SamplesImported += panel.Refresh;
        _usageWindow = new UsageWindow { DataContext = panel };
        _usageWindow.Closed += (_, _) =>
        {
            tracker.TurnRecorded -= OnTurnRecorded;
            tracker.SamplesImported -= panel.Refresh;
            _usageWindow = null;
        };
        _usageWindow.Show(this);
        return Task.CompletedTask;
    }
}
