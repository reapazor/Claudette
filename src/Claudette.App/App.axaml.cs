using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Claudette.App.Services;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core;
using Claudette.Core.Processes;
using Claudette.Core.Settings;
using Claudette.Platform.Notifications;
using Claudette.Platform.Processes;

namespace Claudette.App;

public partial class App : Application
{
    private AppServices? _services;
    private MainWindowViewModel? _mainViewModel;
    private bool _shutdownComplete;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var launcher = new ProcessLauncher();
            var trees = TryCreateProcessTracker(launcher);
            _services = new AppServices(
                AppPaths.ForCurrentUser(),
                trees is null ? launcher : new TrackingProcessLauncher(launcher, trees),
                TimeProvider.System,
                new AvaloniaPlatformServices(() => TopLevel.GetTopLevel(window)),
                new AvaloniaUiDispatcher(),
                processTrees: trees,
                notifier: Notifier.CreateForCurrentOS(launcher));
            var services = _services;
            // The Dock or taskbar badge needs the window's native handle, so it's set up once the window exists.
            window.Opened += (_, _) => services.Notifications.UseBadge(
                Notifier.CreateBadgeForCurrentOS(() => window.TryGetPlatformHandle()?.Handle ?? 0, BadgeIcon.Render));
            ApplyAppearance();
            _services.SettingsChanged += (_, _) => ApplyAppearance();
            _mainViewModel = new MainWindowViewModel(_services, FolderArgument(desktop.Args ?? []));
            window.DataContext = _mainViewModel;
            window.Closing += OnMainWindowClosing;
            desktop.MainWindow = window;
            _ = _mainViewModel.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Process tracking for the process monitor and tab cleanup (DESIGN.md §4). Optional: tabs work without it.</summary>
    private static IProcessTreeTracker? TryCreateProcessTracker(IProcessLauncher launcher)
    {
        try
        {
            return ProcessTreeTracker.CreateForCurrentOS(launcher, TimeProvider.System);
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Applies Settings → Appearance: theme and font sizes (DESIGN.md §14).</summary>
    private void ApplyAppearance()
    {
        if (_services is null)
        {
            return;
        }
        var appearance = _services.Settings.Appearance;
        RequestedThemeVariant = appearance.Theme switch
        {
            ThemeChoice.Light => ThemeVariant.Light,
            ThemeChoice.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        Resources["ConversationFontSize"] = appearance.ConversationFontSize;
        Resources["CodeFontSize"] = appearance.CodeFontSize;
    }

    /// <summary><c>--folder &lt;path&gt;</c> opens a tab in that folder on startup (DESIGN.md §4, "Other ways in").</summary>
    private static string? FolderArgument(string[] args)
    {
        var index = Array.IndexOf(args, "--folder");
        return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
    }

    /// <summary>Stops every Claude Code process before the window closes (interrupting a working turn first).</summary>
    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownComplete || sender is not Window window)
        {
            return;
        }
        e.Cancel = true;
        window.IsEnabled = false;
        try
        {
            if (_mainViewModel is not null)
            {
                await _mainViewModel.DisposeAsync();
            }
            if (_services is not null)
            {
                await _services.FlushAsync();
                await _services.DisposeAsync();
            }
        }
        finally
        {
            _shutdownComplete = true;
            window.Close();
        }
    }
}
