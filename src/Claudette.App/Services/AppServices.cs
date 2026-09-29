using Claudette.Core;
using Claudette.Core.Auth;
using Claudette.Core.Git;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Claudette.Platform.Processes;
using Claudette.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.App.Services;

/// <summary>
/// The app's composition root. Holds the shared services, settings and state, and the services that exist once
/// Claude Code has been found.
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private readonly IProcessLauncher _launcher;
    private readonly JsonFileStore<AppSettings> _settingsStore;
    private readonly JsonFileStore<AppState> _stateStore;
    private readonly Lock _saveLock = new();
    private CancellationTokenSource? _pendingSettingsSave;
    private CancellationTokenSource? _pendingStateSave;
    private readonly SemaphoreSlim _utilityLock = new(1, 1);
    private UtilitySession? _utility;

    /// <param name="processTrees">
    /// Tracks each <c>claude</c> process and everything it starts (DESIGN.md §4, "Process monitor"); the launcher must
    /// be the matching <see cref="Claudette.Platform.Processes.TrackingProcessLauncher"/>. Null in tests.
    /// </param>
    public AppServices(
        AppPaths paths,
        IProcessLauncher launcher,
        TimeProvider timeProvider,
        IPlatformServices platform,
        IUiDispatcher dispatcher,
        ILoggerFactory? loggerFactory = null,
        IProcessTreeTracker? processTrees = null)
    {
        Paths = paths;
        _launcher = launcher;
        Time = timeProvider;
        Platform = platform;
        Dispatcher = dispatcher;
        Loggers = loggerFactory ?? NullLoggerFactory.Instance;
        ProcessTrees = processTrees;
        Locator = new ClaudeLocator(launcher, timeProvider);
        _settingsStore = new JsonFileStore<AppSettings>(paths.SettingsFile, Loggers.CreateLogger("Settings"));
        _stateStore = new JsonFileStore<AppState>(paths.StateFile, Loggers.CreateLogger("State"));
        Settings = _settingsStore.Load();
        State = _stateStore.Load();
        Git = new GitWorkingTree(launcher, timeProvider);
        Library = new LibraryService(this);
        UpdaterFactory = path => new ClaudeUpdater(path, Paths.UtilityDirectory, _launcher, Time);
        SettingsChanged += (_, _) =>
        {
            Library.OnSettingsChanged();
            ClaudeUpdates?.OnSettingsChanged();
        };
    }

    /// <summary>Makes the updater for a <c>claude</c> path (DESIGN.md §12). Tests replace it.</summary>
    internal Func<string, IClaudeUpdater> UpdaterFactory { get; set; }

    public IClaudeUpdater CreateUpdater(string claudePath) => UpdaterFactory(claudePath);

    /// <summary>Claude Code update checks and <b>Update now</b>, once Claude Code has been found.</summary>
    public ClaudeUpdateService? ClaudeUpdates { get; private set; }

    /// <summary>The Claude Code version new tabs start with.</summary>
    public Version? InstalledClaudeVersion => ClaudeUpdates?.InstalledVersion ?? Install?.Version;

    /// <summary>Runs git for "working tree vs HEAD" and the library's uncommitted-changes check (DESIGN.md §8, §9).</summary>
    public GitWorkingTree Git { get; }

    /// <summary>The process trees of the tabs' <c>claude</c> processes. Null when unavailable (tests, other OSes).</summary>
    public IProcessTreeTracker? ProcessTrees { get; }

    /// <summary>The session library, leases and settings sync (DESIGN.md §9, §14).</summary>
    public LibraryService Library { get; }

    public AppPaths Paths { get; }

    public TimeProvider Time { get; }

    /// <summary>Starts processes (DESIGN.md §15): <c>claude</c>, git, diff tools.</summary>
    public IProcessLauncher Launcher => _launcher;

    public IPlatformServices Platform { get; }

    public IUiDispatcher Dispatcher { get; }

    public ILoggerFactory Loggers { get; }

    public ClaudeLocator Locator { get; }

    /// <summary>Claudette's settings (DESIGN.md §14). Change them on the UI thread, then call <see cref="SaveSettings"/>.</summary>
    public AppSettings Settings { get; }

    /// <summary>Saved tabs, recent and favorite folders. Change on the UI thread, then call <see cref="SaveState"/>.</summary>
    public AppState State { get; }

    /// <summary>Raised on the UI thread after settings change.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>
    /// Raised on the UI thread after usage history was cleared. The argument says whether each tab's saved token totals
    /// should be reset too (DESIGN.md §6, "Clear usage history").
    /// </summary>
    public event EventHandler<bool>? UsageHistoryCleared;

    /// <summary>Deletes stored usage samples and per-turn records, and optionally the tabs' token totals.</summary>
    public async Task ClearUsageHistoryAsync(bool alsoResetTabTotals)
    {
        if (UsageHistory is { } history)
        {
            await Task.Run(history.Clear);
        }
        UsageHistoryCleared?.Invoke(this, alsoResetTabTotals);
    }

    public ClaudeInstall? Install { get; private set; }

    public IClaudeSessionFactory? Sessions { get; private set; }

    public ClaudeAuth? Auth { get; private set; }

    /// <summary>Where Claude Code keeps transcripts, from <c>claude auth status</c> (DESIGN.md §11).</summary>
    public string? ProjectsDirectory { get; set; }

    /// <summary>The local usage history (DESIGN.md §6), once usage tracking has started.</summary>
    public UsageStore? UsageHistory { get; private set; }

    /// <summary>App-wide plan usage, once signed in. Null in tests that don't need it.</summary>
    public UsageTracker? Usage { get; private set; }

    /// <summary>Opens the usage history and starts polling plan usage. Called once, after sign-in.</summary>
    public UsageTracker StartUsageTracking()
    {
        if (Usage is null)
        {
            UsageHistory = new UsageStore(Paths.UsageDatabase, Time);
            Usage = new UsageTracker(this, UsageHistory);
            Usage.Start();
        }
        return Usage;
    }

    public void UseInstall(ClaudeInstall install)
    {
        Install = install;
        Sessions = new ClaudeSessionFactory(install.Path, _launcher, Time, Loggers);
        Auth = new ClaudeAuth(install.Path, _launcher, Time);
        ClaudeUpdates = new ClaudeUpdateService(this, CreateUpdater(install.Path), install.Version);
    }

    /// <summary>For tests: sessions come from <paramref name="factory"/> instead of a real <c>claude</c>.</summary>
    internal void UseSessionFactory(IClaudeSessionFactory factory) => Sessions = factory;

    /// <summary>Saves settings shortly, so a burst of changes (typing in a field) writes once.</summary>
    public void SaveSettings()
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        Debounce(ref _pendingSettingsSave, () => JsonFileStore<AppSettings>.Serialize(Settings), _settingsStore);
    }

    public void SaveState() => Debounce(ref _pendingStateSave, () => JsonFileStore<AppState>.Serialize(State), _stateStore);

    /// <summary>Writes anything still pending, for shutdown.</summary>
    public async Task FlushAsync()
    {
        _pendingSettingsSave?.Cancel();
        _pendingStateSave?.Cancel();
        await _settingsStore.SaveAsync(Settings);
        await _stateStore.SaveAsync(State);
    }

    /// <summary>
    /// The hidden utility session, started on first use and restarted if it has exited. Safe to call from any thread:
    /// the usage poller calls it from a timer.
    /// </summary>
    public async Task<UtilitySession> GetUtilitySessionAsync(CancellationToken cancellationToken = default)
    {
        await _utilityLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_utility is { Completion.IsCompleted: false })
            {
                return _utility;
            }
            if (_utility is not null)
            {
                await _utility.DisposeAsync().ConfigureAwait(false);
            }
            var factory = Sessions ?? throw new InvalidOperationException("Claude Code hasn't been found yet.");
            _utility = await UtilitySession.StartAsync(factory, Paths.UtilityDirectory, cancellationToken).ConfigureAwait(false);
            return _utility;
        }
        finally
        {
            _utilityLock.Release();
        }
    }

    /// <summary>
    /// Stops the hidden utility session, for an update that can't replace a running <c>claude</c> (WinGet). It starts
    /// again the next time it's needed.
    /// </summary>
    public async Task StopUtilitySessionAsync()
    {
        await _utilityLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_utility is not null)
            {
                await _utility.DisposeAsync().ConfigureAwait(false);
                _utility = null;
            }
        }
        finally
        {
            _utilityLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Library.Dispose();
        if (ClaudeUpdates is not null)
        {
            await ClaudeUpdates.DisposeAsync();
            ClaudeUpdates = null;
        }
        if (Usage is not null)
        {
            await Usage.DisposeAsync();
            Usage = null;
        }
        UsageHistory?.Dispose();
        UsageHistory = null;
        if (_utility is not null)
        {
            await _utility.DisposeAsync();
            _utility = null;
        }
    }

    /// <summary>
    /// Waits briefly, then serializes on the UI thread (which owns the objects) and writes the file in the background.
    /// </summary>
    private void Debounce<T>(ref CancellationTokenSource? pending, Func<string> serialize, JsonFileStore<T> store) where T : class, new()
    {
        CancellationTokenSource cts;
        lock (_saveLock)
        {
            pending?.Cancel();
            cts = new CancellationTokenSource();
            pending = cts;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveDelay, Time, cts.Token);
                Dispatcher.Post(() =>
                {
                    if (cts.IsCancellationRequested)
                    {
                        return;
                    }
                    var json = serialize();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await store.WriteAsync(json);
                        }
                        catch (IOException ex)
                        {
                            Loggers.CreateLogger("Save").LogWarning(ex, "Couldn't save {Path}.", store.Path);
                        }
                    });
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer save.
            }
        });
    }
}
