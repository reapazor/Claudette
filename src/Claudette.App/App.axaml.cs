using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Claudette.App.Services;
using Claudette.App.Themes;
using Claudette.App.ViewModels;
using Claudette.App.Views;
using Claudette.Core;
using Claudette.Core.Development;
using Claudette.Core.Logging;
using Claudette.Core.Processes;
using Claudette.Core.Settings;
using Claudette.Platform.Credentials;
using Claudette.Platform.LoginShell;
using Claudette.Platform.Notifications;
using Claudette.Platform.Power;
using Claudette.Platform.Processes;
using Claudette.Platform.ProjectTools;
using Claudette.Platform.Shell;
using Claudette.Platform.Shell.Windows;
using Claudette.Platform.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.App;

public partial class App : Application
{
    private AppServices? _services;
    private MainWindowViewModel? _mainViewModel;
    private PlatformChrome? _chrome;
    private WindowPlacementTracker? _placement;
    private bool _shutdownComplete;
    private MainWindow? _mainWindow;

    /// <summary>Settings → Appearance → Style's colors (DESIGN.md §3, "Visual style").</summary>
    internal AppColors Colors { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Colors = new AppColors(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = _mainWindow = new MainWindow();
            _placement = new WindowPlacementTracker(window);
            var launcher = new ProcessLauncher();
            var trees = TryCreateProcessTracker(launcher);
            var paths = AppPaths.ForCurrentUser();
            var loggers = new FileLoggerFactory(paths.LogDirectory, TimeProvider.System);
            CatchUnhandledExceptions(loggers.CreateLogger("Unhandled"));
            _services = new AppServices(
                paths,
                trees is null ? launcher : new TrackingProcessLauncher(launcher, trees),
                TimeProvider.System,
                new AvaloniaPlatformServices(() => TopLevel.GetTopLevel(window)),
                new AvaloniaUiDispatcher(),
                processTrees: trees,
                notifier: Notifier.CreateForCurrentOS(launcher),
                credentials: CredentialStores.CreateForCurrentOS(launcher, TimeProvider.System),
                appInstaller: AppInstallers.CreateForCurrentOS(launcher, TimeProvider.System, paths.UpdatesDirectory, NullLogger.Instance),
                loginShell: LoginShellReader.CreateForCurrentOS(launcher, TimeProvider.System),
                systemProcesses: new SystemProcesses(launcher, TimeProvider.System),
                unrealRegistry: UnrealEngineRegistries.CreateForCurrentOS(),
                sleepBlocker: SleepBlockers.CreateForCurrentOS(launcher),
                loginItems: LoginLaunch.CreateItems(launcher, TimeProvider.System),
                loggerFactory: loggers);
            // In the background, so the window isn't held up; the first claude start waits for it (DESIGN.md §13).
            _services.UserEnvironment.Start();
            var services = _services;
            // The Dock or taskbar badge needs the window's native handle, so it's set up once the window exists.
            window.Opened += (_, _) => services.Notifications.UseBadge(
                Notifier.CreateBadgeForCurrentOS(() => window.TryGetPlatformHandle()?.Handle ?? 0, BadgeIcon.Render));
            ApplyAppearance();
            _services.SettingsChanged += (_, _) => ApplyAppearance();
            var args = desktop.Args ?? [];
            _mainViewModel = new MainWindowViewModel(_services, LaunchArguments.Folder(args));
            UseRestarts(window, args);
            _services.ThisCopy = LoginLaunch.CurrentCopy(args, _services.AppVersion);
            var restored = RestoreAfterRestart(args);
            if (restored is null && _services.State.Window is { } saved)
            {
                // Where the window was last time on this machine (DESIGN.md §14).
                _placement.Apply(saved);
            }
            if (restored is null && LaunchArguments.IsLogin(args))
            {
                // Started at login: out of the way until it's wanted (DESIGN.md §9, "Starting at login").
                _placement.StartMinimized();
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
            if (restored is not null)
            {
                _ = SignalRestartedAsync(started, opened.Task, _services.Paths, restored.Nonce);
            }
            // An installed Claudette notes itself for source builds, and takes over an entry one left for it.
            _ = _services.StartAtLogin.RefreshAtLaunchAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Logs what nothing else caught, to <c>claudette.log</c> (DESIGN.md §13, "Diagnostics"). An exception on the UI
    /// thread is logged and handled rather than ending the app, which would leave every tab's <c>claude</c> running
    /// (the Job Objects deliberately don't end them, §4). A faulted task nobody awaited is logged and observed.
    /// </summary>
    private static void CatchUnhandledExceptions(ILogger logger)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unhandled exception on the UI thread; carrying on.");
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "A background task failed and nothing was waiting for it.");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception; Claudette is ending.");
    }

    // ---- Restarts: source builds (DESIGN.md §9, "Working on Claudette") and updates (§2, "Updating Claudette") -----

    /// <summary>
    /// Sets up handing over to a new build or version: the window's placement, closing once it's up, and passing on
    /// later launches. A source build also watches for its new builds.
    /// </summary>
    private void UseRestarts(MainWindow window, IReadOnlyList<string> args)
    {
        if (_mainViewModel is not { } main)
        {
            return;
        }
        var instance = Program.Instance;
        Action? stopListening = instance is null ? null : instance.StopListening;
        Action? resumeListening = instance is null ? null : instance.Listen;
        if (DevelopmentLaunch.Current(args) is { } build)
        {
            main.UseDevelopmentBuild(build, stopListening, resumeListening);
        }
        else
        {
            main.UseSingleInstance(stopListening, resumeListening);
        }
        main.GetWindowPlacement = _placement!.Current;
        main.ExitRequested += window.Close;
    }

    /// <summary>
    /// When an earlier build or version restarted into this one, takes over its tabs and window placement: the snapshot
    /// named by <c>--restore</c>, or, when Claudette was opened by hand after an update, the update's snapshot.
    /// </summary>
    private RestartSnapshot? RestoreAfterRestart(IReadOnlyList<string> args)
    {
        if (_services is null || _mainViewModel is null
            || RestartSnapshot.Load(_services.Paths.RestartFile, LaunchArguments.RestoreNonce(args), _services.Time.GetUtcNow()) is not { } snapshot)
        {
            return null;
        }
        _mainViewModel.RestoreOnStart(snapshot);
        if (snapshot.Window is { } where)
        {
            _placement!.Apply(where);
        }
        return snapshot;
    }

    /// <summary>
    /// Tells the build or version that restarted into this one that it's up, so it closes: once the window is open, the
    /// startup checks are done and the page they led to has rendered. Until then that build can still take its tabs
    /// back, so the snapshot is only deleted now.
    /// </summary>
    private static async Task SignalRestartedAsync(Task started, Task opened, AppPaths paths, string nonce)
    {
        await Task.WhenAll(started, opened);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        RestartHandshake.SignalReady(paths.RestartReadyFile, nonce);
        RestartSnapshot.Delete(paths.RestartFile);
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

    private const string DefaultMonoFonts = "Cascadia Mono, Consolas, Menlo, monospace";

    /// <summary>The appearance settings last applied, so unchanged ones aren't applied again.</summary>
    private object? _appliedAppearance;

    /// <summary>Applies Settings → Appearance: theme, style, fonts and font sizes (DESIGN.md §14).</summary>
    private void ApplyAppearance()
    {
        if (_services is null)
        {
            return;
        }
        var appearance = _services.Settings.Appearance;
        // Settings change often (every switch in Settings saves), and setting an application resource walks every
        // window's whole tree, every tab's conversation included: only when something here changed.
        var applied = (appearance.Theme, appearance.Style, appearance.ConversationFontSize, appearance.CodeFontSize, appearance.ConversationFont, appearance.CodeFont);
        if (applied.Equals(_appliedAppearance))
        {
            return;
        }
        _appliedAppearance = applied;
        RequestedThemeVariant = appearance.Theme switch
        {
            ThemeChoice.Light => ThemeVariant.Light,
            ThemeChoice.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        Colors.Apply(appearance.Style);
        _mainWindow?.UseMica(appearance.Style == AppStyle.Standard);
        Resources["ConversationFontSize"] = appearance.ConversationFontSize;
        Resources["CodeFontSize"] = appearance.CodeFontSize;
        // A font that isn't installed falls back to the next name in the list.
        Resources["ConversationFont"] = appearance.ConversationFont is { } conversation ? new FontFamily($"{conversation}, {FontFamily.DefaultFontFamilyName}") : FontFamily.Default;
        Resources["ReplyFont"] = AppColors.ReplyFont(appearance.Style, appearance.ConversationFont);
        Resources["MonoFont"] = new FontFamily(appearance.CodeFont is { } code ? $"{code}, {DefaultMonoFonts}" : DefaultMonoFonts);
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
