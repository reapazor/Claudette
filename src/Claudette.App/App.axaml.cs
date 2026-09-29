using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Claudette.App.Services;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core;
using Claudette.Core.Development;
using Claudette.Core.Processes;
using Claudette.Core.Settings;
using Claudette.Platform.Notifications;
using Claudette.Platform.Processes;
using Claudette.Platform.Shell;
using Claudette.Platform.Shell.Windows;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.App;

public partial class App : Application
{
    private AppServices? _services;
    private MainWindowViewModel? _mainViewModel;
    private PlatformChrome? _chrome;
    private WindowPlacementTracker? _placement;
    private bool _shutdownComplete;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            _placement = new WindowPlacementTracker(window);
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
            var args = desktop.Args ?? [];
            _mainViewModel = new MainWindowViewModel(_services, LaunchArguments.Folder(args));
            var restoreNonce = UseDevelopmentBuild(window, args);
            if (restoreNonce is null && _services.State.Window is { } saved)
            {
                // Where the window was last time on this machine (DESIGN.md §14).
                _placement.Apply(saved);
            }
            window.DataContext = _mainViewModel;
            var main = _mainViewModel;
            if (Program.Instance is { } instance)
            {
                instance.ArgumentsReceived += args => Dispatcher.UIThread.Post(() => main.OnLaunchedAgain(args));
            }
            window.Closing += OnMainWindowClosing;
            desktop.MainWindow = window;
            _chrome = new PlatformChrome(this, window, _mainViewModel, _services, CreateJumpList());
            var opened = new TaskCompletionSource();
            window.Opened += (_, _) => opened.TrySetResult();
            var started = _mainViewModel.StartAsync();
            if (restoreNonce is not null)
            {
                _ = SignalRestartedAsync(started, opened.Task, _services.Paths.RestartReadyFile, restoreNonce);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ---- Source builds (DESIGN.md §9, "Working on Claudette") ---------------------------------------------------

    /// <summary>
    /// For a source build: offer its new builds, and when this build was restarted into, take over the earlier build's
    /// tabs and window placement. Returns the restart's nonce then, or null.
    /// </summary>
    private string? UseDevelopmentBuild(MainWindow window, IReadOnlyList<string> args)
    {
        if (_services is null || _mainViewModel is null || DevelopmentLaunch.Current(args) is not { } build)
        {
            return null;
        }
        var main = _mainViewModel;
        var instance = Program.Instance;
        main.UseDevelopmentBuild(build, instance is null ? null : instance.StopListening, instance is null ? null : instance.Listen);
        var placement = _placement!;
        main.GetWindowPlacement = placement.Current;
        main.ExitRequested += window.Close;
        if (LaunchArguments.RestoreNonce(args) is not { } nonce)
        {
            return null;
        }
        if (RestartSnapshot.Load(_services.Paths.RestartFile, nonce, _services.Time.GetUtcNow()) is { } snapshot)
        {
            main.RestoreOnStart(snapshot);
            if (snapshot.Window is { } where)
            {
                placement.Apply(where);
            }
        }
        return nonce;
    }

    /// <summary>
    /// Tells the build that restarted into this one that it's up, so it closes: once the window is open, the startup
    /// checks are done and the page they led to has rendered. Until then that build can still take its tabs back.
    /// </summary>
    private static async Task SignalRestartedAsync(Task started, Task opened, string readyFile, string nonce)
    {
        await Task.WhenAll(started, opened);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        RestartHandshake.SignalReady(readyFile, nonce);
    }

    /// <summary>Recent folders in the taskbar jump list, on Windows (DESIGN.md §4).</summary>
    private static IJumpList? CreateJumpList() =>
        OperatingSystem.IsWindows() && Environment.ProcessPath is { } exe ? new WindowsJumpList(exe, NullLogger.Instance) : null;

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
            _chrome?.Dispose();
            if (_services is not null && _placement is not null)
            {
                // Saved with the state; after a restart handover, saving is suspended and the new build has it.
                _services.State.Window = _placement.Current();
                _services.SaveState();
            }
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
