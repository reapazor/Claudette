using System.Text.Json;
using Claudette.Core;
using Claudette.Core.Accessibility;
using Claudette.Core.Auth;
using Claudette.Core.Claude;
using Claudette.Core.Composer;
using Claudette.Core.Credentials;
using Claudette.Core.Diffs;
using Claudette.Core.Git;
using Claudette.Core.Installation;
using Claudette.Core.LoginItems;
using Claudette.Core.Processes;
using Claudette.Core.ProjectTools;
using Claudette.Core.ProjectTools.Unreal;
using Claudette.Core.Protocol;
using Claudette.Core.RemoteControl;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Claudette.Core.Updates;
using Claudette.Platform.Notifications;
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
    private readonly ISystemMotion _systemMotion;
    private bool _systemReducesMotion;
    private bool _claudeCodeReducesMotion;
    private int _readingMotion;
    /// <summary>Asked to read while a read was under way: it reads again once that ends, as it may know more now.</summary>
    private int _readMotionAgain;
    private DateTimeOffset? _motionReadAt;

    /// <param name="processTrees">
    /// Tracks each <c>claude</c> process and everything it starts (DESIGN.md §4, "Process monitor"); the launcher must
    /// be the matching <see cref="Claudette.Platform.Processes.TrackingProcessLauncher"/>. Null in tests.
    /// </param>
    /// <param name="notifier">Shows OS notifications (DESIGN.md §10). Null shows none.</param>
    /// <param name="credentials">The OS credential store, for stored Perforce passwords (DESIGN.md §18). Null has none.</param>
    /// <param name="appInstaller">Installs Claudette's own updates (DESIGN.md §2, "Updating Claudette"). Null can't.</param>
    /// <param name="httpHandler">
    /// Sends Claudette's own web requests: the update check and download, and Claude's service status. Tests pass a fake.
    /// </param>
    /// <param name="appVersion">This Claudette's version; by default, the one it was built with.</param>
    /// <param name="loginShell">
    /// Reads the login shell's environment for the user's processes (DESIGN.md §13, "Login shell environment"). Null
    /// never reads one: tests.
    /// </param>
    /// <param name="systemProcesses">Every running process by name, for project tools (DESIGN.md §18). Null can't tell.</param>
    /// <param name="unrealRegistry">Unreal's Windows registry entries. Null has none.</param>
    /// <param name="projectToolPaths">Where other programs keep their files; by default the current user's folders.</param>
    /// <param name="sleepBlocker">
    /// Keeps the computer awake while tabs are connected to the Claude app (DESIGN.md §18, "Remote Control"). Null
    /// keeps nothing awake: tests.
    /// </param>
    /// <param name="loginItems">The OS's login entry (DESIGN.md §9, "Starting at login"). Null has none: tests.</param>
    /// <param name="systemMotion">Whether the OS asks for less motion (DESIGN.md §3, "Accessibility"). Null never does: tests.</param>
    public AppServices(
        AppPaths paths,
        IProcessLauncher launcher,
        TimeProvider timeProvider,
        IPlatformServices platform,
        IUiDispatcher dispatcher,
        ILoggerFactory? loggerFactory = null,
        IProcessTreeTracker? processTrees = null,
        INotifier? notifier = null,
        ICredentialStore? credentials = null,
        IAppInstaller? appInstaller = null,
        HttpMessageHandler? httpHandler = null,
        AppVersion? appVersion = null,
        ILoginShell? loginShell = null,
        ISystemProcesses? systemProcesses = null,
        IUnrealEngineRegistry? unrealRegistry = null,
        ProjectToolPaths? projectToolPaths = null,
        ISleepBlocker? sleepBlocker = null,
        ILoginItems? loginItems = null,
        ISystemMotion? systemMotion = null)
    {
        AppInstaller = appInstaller ?? new NoAppInstaller();
        AppVersion = appVersion ?? BuiltVersion();
        LoginItems = loginItems ?? new NoLoginItems("Claudette can't start at login here.");
        ThisCopy = new ClaudetteCopy(AppInstallKind.Other, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), AppVersion.ToString());
        BuildCommit = AppBuild.CommitOf(InformationalVersion());
#pragma warning disable RS0030 // This is AppServices.Http: the one client, whose handler tests replace.
        Http = new HttpClient(httpHandler ?? new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
#pragma warning restore RS0030
        Paths = paths;
        _launcher = launcher;
        Time = timeProvider;
        Platform = platform;
        Dispatcher = dispatcher;
        Loggers = loggerFactory ?? NullLoggerFactory.Instance;
        ProcessTrees = processTrees;
        _settingsStore = new JsonFileStore<AppSettings>(paths.SettingsFile, Loggers.CreateLogger("Settings"));
        _stateStore = new JsonFileStore<AppState>(paths.StateFile, Loggers.CreateLogger("State"));
        Settings = _settingsStore.Load();
        State = _stateStore.Load();
        _systemMotion = systemMotion ?? new NoSystemMotion();
        ReduceMotion = Motion.Reduce(Settings.Appearance.Motion, false, false);
        // The first launch that knows about folder trust trusts the folders already used here (DESIGN.md §7).
        State.TrustFoldersAlreadyUsed();
        UserEnvironment = new UserEnvironment(loginShell, () => Settings.ClaudeCode.UseLoginShellEnvironment, Loggers.CreateLogger("LoginShell"));
        RemoteControl = new RemoteControlService(this, sleepBlocker ?? new NoSleepBlocker());
        Locator = new ClaudeLocator(launcher, timeProvider, UserEnvironment, RemoteControl.ClaudeVariables);
        Git = new GitWorkingTree(launcher, timeProvider, environment: UserEnvironment);
        Library = new LibraryService(this);
        ScratchPads = new ScratchPadService(this);
        Drafts = new DraftService(this);
        Worktrees = new WorktreeCleanupService(this);
        Mascot = new MascotService(this);
        ProtocolLog.DeleteOld(paths.ProtocolLogDirectory, timeProvider.GetUtcNow());
        BeforeContentStore.DeleteOld(paths.BeforeContentDirectory, timeProvider.GetUtcNow());
        Notifications =new NotificationService(this, notifier ?? NullNotifier.Instance);
        Tips = new ShortcutTips(Settings);
        Perforce = new PerforceService(this, credentials ?? new UnavailableCredentialStore());
        ProjectTools = new ProjectToolsService(this, systemProcesses, unrealRegistry ?? NoUnrealEngineRegistry.Instance, projectToolPaths ?? ProjectToolPaths.ForCurrentUser());
        ServiceStatus = new ServiceStatusService(this);
        LiveSessions = new LiveSessionsService(this, systemProcesses);
        UpdaterFactory = path => new ClaudeUpdater(path, Paths.UtilityDirectory, _launcher, Time, UserEnvironment.Probe,
            environmentOverrides: RemoteControl.ClaudeVariables, userEnvironment: UserEnvironment);
        SettingsChanged += (_, _) =>
        {
            // Turning Use my login shell's environment on reads it now, if this run hasn't yet.
            UserEnvironment.Start();
            UpdateMotion();
            Library.OnSettingsChanged();
            ScratchPads.OnSettingsChanged();
            Mascot.OnSettingsChanged();
            ClaudeUpdates?.OnSettingsChanged();
            Notifications.OnSettingsChanged();
            RemoteControl.OnSettingsChanged();
            ServiceStatus.OnSettingsChanged();
            Tips.Refresh();
        };
    }

    /// <summary>Tooltips naming the current keyboard shortcuts (DESIGN.md §14, "Keyboard").</summary>
    public ShortcutTips Tips { get; }

    /// <summary>
    /// Claudette's animations stop (DESIGN.md §3, "Accessibility"): Settings → Appearance → Motion says so, or follows
    /// the OS or Claude Code's <c>prefersReducedMotion</c>, which ask for it.
    /// </summary>
    public bool ReduceMotion { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="ReduceMotion"/> changes.</summary>
    public event EventHandler? MotionChanged;

    /// <summary>How long a read of the OS's motion setting holds when the window comes back to the front.</summary>
    internal static readonly TimeSpan MotionRecheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Reads, in the background, whether the OS and Claude Code's user settings ask for less motion. The app reads them
    /// at launch, once Claude Code's config folder is known, and when the window comes back to the front
    /// (<paramref name="onlyIfStale"/>: unless they were read in the last minute), in case the OS's setting changed.
    /// </summary>
    public async Task ReadMotionPreferencesAsync(bool onlyIfStale = false)
    {
        if (onlyIfStale && _motionReadAt is { } readAt && Time.GetUtcNow() - readAt < MotionRecheckInterval)
        {
            return;
        }
        if (Interlocked.Exchange(ref _readingMotion, 1) == 1)
        {
            Volatile.Write(ref _readMotionAgain, 1);
            return;
        }
        try
        {
            var configDirectory = ClaudeConfigDirectory;
            var claudeCodeReading = Task.Run(() => Motion.ClaudeCodePrefersReduced(configDirectory));
            // On the calling thread: macOS's NSWorkspace is asked from the UI thread; Linux's gsettings runs on its own.
            var system = await _systemMotion.PrefersReducedMotionAsync().ConfigureAwait(false);
            var claudeCode = await claudeCodeReading.ConfigureAwait(false);
            Dispatcher.Post(() =>
            {
                _motionReadAt = Time.GetUtcNow();
                _systemReducesMotion = system;
                _claudeCodeReducesMotion = claudeCode;
                UpdateMotion();
            });
        }
        finally
        {
            Volatile.Write(ref _readingMotion, 0);
        }
        // Claude Code's config folder may have become known meanwhile.
        if (Interlocked.Exchange(ref _readMotionAgain, 0) == 1)
        {
            await ReadMotionPreferencesAsync().ConfigureAwait(false);
        }
    }

    private void UpdateMotion()
    {
        var reduce = Motion.Reduce(Settings.Appearance.Motion, _systemReducesMotion, _claudeCodeReducesMotion);
        if (reduce != ReduceMotion)
        {
            ReduceMotion = reduce;
            MotionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>This Claudette's version, as its releases are tagged.</summary>
    public AppVersion AppVersion { get; }

    /// <summary>Installs a downloaded release over this Claudette, when the way it was installed allows (DESIGN.md §2).</summary>
    public IAppInstaller AppInstaller { get; }

    /// <summary>
    /// For Claudette's own requests: GitHub's releases, and Claude's public status page. Everything else Claude-related
    /// goes through Claude Code.
    /// </summary>
    public HttpClient Http { get; }

    /// <summary>What Claudette's web requests call themselves.</summary>
    public string UserAgent => $"Claudette/{AppVersion}";

    /// <summary>The commit this Claudette was built from, shortened, or null when the build didn't record one.</summary>
    internal string? BuildCommit { get; set; }

    /// <summary>The configuration this Claudette was built in (Debug, Release), as the .NET SDK records it.</summary>
    internal string? BuildConfiguration { get; set; } = typeof(AppServices).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
        .OfType<System.Reflection.AssemblyConfigurationAttribute>()
        .FirstOrDefault()?.Configuration is { Length: > 0 } configuration ? configuration : null;

    /// <summary>
    /// Runs from a source build (DESIGN.md §9, "Working on Claudette"), set at launch: its version is the checkout's,
    /// so the commit tells it from the release.
    /// </summary>
    public bool IsSourceBuild { get; set; }

    /// <summary>This copy of Claudette, for the foot of the Settings sidebar and bug reports (DESIGN.md §14, "Version").</summary>
    public AppBuild Build => new(AppVersion, IsSourceBuild ? AppInstallKind.SourceBuild : AppInstaller.Kind, BuildCommit) { Configuration = BuildConfiguration };

    /// <summary>The OS's login entry (DESIGN.md §9, "Starting at login").</summary>
    public ILoginItems LoginItems { get; }

    /// <summary>This copy of Claudette, which the login entry may start. Set at launch: a source build by its build output.</summary>
    public ClaudetteCopy ThisCopy
    {
        get;
        set
        {
            field = value;
            _startAtLogin = null;
        }
    }

    private StartAtLogin? _startAtLogin;

    /// <summary>Settings → General's <b>Start Claudette when I log in</b>, and tidying the entry up at launch.</summary>
    public StartAtLogin StartAtLogin =>
        _startAtLogin ??= new StartAtLogin(LoginItems, ThisCopy, State.LoginItem, SaveState, Loggers.CreateLogger("LoginItems"));

    /// <summary>The version the running app was built with (<c>-p:Version=…</c> in packaging/), without build metadata.</summary>
    internal static AppVersion BuiltVersion() =>
        AppVersion.TryParse(InformationalVersion())
        ?? AppVersion.TryParse(typeof(AppServices).Assembly.GetName().Version?.ToString(3))
        ?? new AppVersion(0, 0, 0);

    /// <summary>Such as <c>0.1.0+842169b…</c>: the version, and the commit the .NET SDK records from the checkout.</summary>
    private static string? InformationalVersion() =>
        typeof(AppServices).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

    /// <summary>OS notifications and the Dock/taskbar badge (DESIGN.md §10).</summary>
    public NotificationService Notifications { get; }

    /// <summary>Perforce ticket handling and changelists, shared by the tabs (DESIGN.md §18).</summary>
    public PerforceService Perforce { get; }

    /// <summary>Project tools: detecting a tab's project and running its actions (DESIGN.md §18).</summary>
    public ProjectToolsService ProjectTools { get; }

    /// <summary>
    /// Remote Control, the Claude app's connection to the tabs (DESIGN.md §18): whether the account can use it, the
    /// presence file every <c>claude</c> is told about, and keeping the computer awake while tabs are connected.
    /// </summary>
    public RemoteControlService RemoteControl { get; }

    /// <summary>
    /// Claude's service status from status.claude.com (DESIGN.md §18, "Service status"): the header's dot and the banner.
    /// The window starts it at launch; tabs report API errors to it.
    /// </summary>
    public ServiceStatusService ServiceStatus { get; }

    /// <summary>The Claude Code sessions running on this machine, and the names they're reached by (DESIGN.md §13).</summary>
    public LiveSessionsService LiveSessions { get; }

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

    /// <summary>The projects' scratch pads (DESIGN.md §18, "Scratch pad").</summary>
    public ScratchPadService ScratchPads { get; }

    /// <summary>Each tab's unsent message, and the stash (DESIGN.md §5, "Drafts and the stash").</summary>
    public DraftService Drafts { get; }

    /// <summary>Removing worktree tabs' worktrees on their own, as Settings → General says (DESIGN.md §4, "Cleaning up worktrees").</summary>
    public WorktreeCleanupService Worktrees { get; }

    /// <summary>Claudette on the composer, shared by the tabs (DESIGN.md §5).</summary>
    public MascotService Mascot { get; }

    public AppPaths Paths { get; }

    public TimeProvider Time { get; }

    /// <summary>Starts processes (DESIGN.md §15): <c>claude</c>, git, diff tools.</summary>
    public IProcessLauncher Launcher => _launcher;

    /// <summary>
    /// The environment of the user's processes, <c>claude</c>, git, <c>p4</c> and diff tools, with the login shell's
    /// merged in when it's used (DESIGN.md §13, "Login shell environment"). The app starts reading it at launch.
    /// </summary>
    public UserEnvironment UserEnvironment { get; }

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

    /// <summary>Claude Code's config folder, from <c>claude auth status</c>: where its user settings are.</summary>
    public string? ClaudeConfigDirectory { get; set; }

    /// <summary>Where an organization's <c>managed-settings.json</c> for Claude Code is. Null reads none: tests.</summary>
    public string? ClaudeManagedSettingsDirectory { get; set; } = StartingPermissionMode.ManagedDirectory;

    /// <summary>
    /// What Claude Code's settings say about the permission mode a session in <paramref name="folder"/> starts in
    /// (DESIGN.md §7, "Starting mode"); null reads only the user's and managed settings. Reads files.
    /// </summary>
    public StartingPermissionMode ReadStartingPermissionMode(string? folder) =>
        StartingPermissionMode.Read(StartingPermissionMode.SettingsFiles(ClaudeManagedSettingsDirectory, ClaudeConfigDirectory, folder));

    /// <summary>
    /// Claude Code may start in <paramref name="folder"/> without asking about what the folder's own configuration runs
    /// (DESIGN.md §7, "Folder trust"): the user trusted it or a folder it's in, or turned the question off.
    /// </summary>
    public bool IsFolderTrusted(string folder) =>
        !Settings.ClaudeCode.AskBeforeUsingFolderSettings || FolderTrust.IsTrusted(State.TrustedFolders ?? [], folder);

    /// <summary>Remembers that the user trusts <paramref name="folder"/>.</summary>
    public void TrustFolder(string folder)
    {
        var trusted = State.TrustedFolders ??= [];
        if (!FolderTrust.IsTrusted(trusted, folder))
        {
            trusted.Add(folder);
            SaveState();
        }
    }

    private readonly Dictionary<string, WeakReference<ProjectFileIndex>> _fileIndexes = new(StringComparer.Ordinal);

    /// <summary>
    /// The <c>@</c> file index for <paramref name="folder"/>, one for all the tabs in it (DESIGN.md §5, "Composer"). Held
    /// weakly here: the tabs keep it, and it goes once they've all closed. Called on the UI thread.
    /// </summary>
    public ProjectFileIndex FileIndexFor(string folder)
    {
        var key = FolderHistory.Normalize(folder);
        if (_fileIndexes.TryGetValue(key, out var weak) && weak.TryGetTarget(out var index))
        {
            return index;
        }
        foreach (var gone in _fileIndexes.Where(e => !e.Value.TryGetTarget(out _)).Select(e => e.Key).ToArray())
        {
            _fileIndexes.Remove(gone);
        }
        index = new ProjectFileIndex(folder, Git, Time);
        _fileIndexes[key] = new WeakReference<ProjectFileIndex>(index);
        return index;
    }

    /// <summary>For the working line's verbs (DESIGN.md §5). Tests give it a seed.</summary>
    public Random Random { get; set; } = Random.Shared;

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
        Sessions = new ClaudeSessionFactory(install.Path, _launcher, Time, Loggers, Diagnostics, UserEnvironment);
        Auth = new ClaudeAuth(install.Path, _launcher, Time, environment: RemoteControl.ClaudeVariables, userEnvironment: UserEnvironment);
        ClaudeUpdates = new ClaudeUpdateService(this, CreateUpdater(install.Path), install.Version);
    }

    /// <summary>What Claude Code has sent that Claudette doesn't know yet, for Settings → Advanced → Diagnostics (DESIGN.md §16).</summary>
    public ProtocolDiagnostics Diagnostics { get; } = new();

    /// <summary>A new protocol log for a session when Settings → Advanced turns logging on, otherwise null (DESIGN.md §13).</summary>
    public string? ProtocolLogPath(string label) =>
        Settings.Advanced.LogProtocol ? Path.Combine(Paths.ProtocolLogDirectory, ProtocolLog.FileName(Time.GetUtcNow(), label)) : null;

    /// <summary>
    /// Keeps the models a session's <c>initialize</c> reply offered, for the model and effort lists in Settings and Tab
    /// settings (DESIGN.md §14). Claude Code's own "default" entry isn't a model to pick.
    /// </summary>
    public void RememberModels(IReadOnlyList<ModelInfo>? models)
    {
        var offered = models?.Where(m => m.Value != "default").ToList() ?? [];
        if (offered.Count > 0 && !offered.SequenceEqual(State.KnownModels, ModelInfoComparer.Instance))
        {
            State.KnownModels = offered;
            SaveState();
        }
    }

    /// <summary>For tests: sessions come from <paramref name="factory"/> instead of a real <c>claude</c>.</summary>
    internal void UseSessionFactory(IClaudeSessionFactory factory) => Sessions = factory;

    /// <summary>For tests: the tabs record their turns with <paramref name="tracker"/>, which only polls once started.</summary>
    internal void UseUsageTracker(UsageTracker tracker)
    {
        UsageHistory = tracker.Store;
        Usage = tracker;
    }

    /// <summary>Saves settings shortly, so a burst of changes (typing in a field) writes once.</summary>
    /// <summary>
    /// Settings → Project tools, as one string to compare: whether they changed since a tab last looked. Worked out
    /// once per change for every tab, rather than by each.
    /// </summary>
    public string ProjectToolsSettingsKey => _projectToolsSettingsKey ??= JsonSerializer.Serialize(Settings.ProjectTools, JsonFileStore<AppSettings>.Options);

    private string? _projectToolsSettingsKey;

    public void SaveSettings()
    {
        _projectToolsSettingsKey = null;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        if (!SuspendSaving)
        {
            Debounce(ref _pendingSettingsSave, () => JsonFileStore<AppSettings>.Serialize(Settings), _settingsStore);
        }
    }

    public void SaveState()
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (!SuspendSaving)
        {
            Debounce(ref _pendingStateSave, () => JsonFileStore<AppState>.Serialize(State), _stateStore);
        }
    }

    /// <summary>
    /// Set while Claudette hands over to a new build of itself (DESIGN.md §9, "Working on Claudette"): the new build
    /// owns the settings and state files then, so nothing is written from here, even at shutdown. Setting it drops
    /// saves still waiting; <see cref="FlushAsync"/> first.
    /// </summary>
    public bool SuspendSaving
    {
        get;
        set
        {
            field = value;
            if (value)
            {
                _pendingSettingsSave?.Cancel();
                _pendingStateSave?.Cancel();
            }
        }
    }

    /// <summary>Raised on the UI thread when the state changes, for example the recent folders.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Writes anything still pending, for shutdown.</summary>
    public async Task FlushAsync()
    {
        if (SuspendSaving)
        {
            return;
        }
        _pendingSettingsSave?.Cancel();
        _pendingStateSave?.Cancel();
        await _settingsStore.SaveAsync(Settings);
        await _stateStore.SaveAsync(State);
        await ScratchPads.FlushAsync();
        await Drafts.FlushAsync();
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
            _utility = await UtilitySession.StartAsync(factory, Paths.UtilityDirectory, cancellationToken, ProtocolLogPath("utility"), RemoteControl.ClaudeVariables).ConfigureAwait(false);
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
        Worktrees.Dispose();
        Mascot.Dispose();
        ScratchPads.Dispose();
        Library.Dispose();
        Notifications.Dispose();
        RemoteControl.Dispose();
        ServiceStatus.Dispose();
        LiveSessions.Dispose();
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
        Http.Dispose();
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

/// <summary>Compares models by what Settings shows of them: the id, name and effort levels.</summary>
internal sealed class ModelInfoComparer : IEqualityComparer<ModelInfo>
{
    public static readonly ModelInfoComparer Instance = new();

    public bool Equals(ModelInfo? x, ModelInfo? y) =>
        ReferenceEquals(x, y) || x is not null && y is not null && x.Value == y.Value && x.DisplayName == y.DisplayName
            && x.SupportsEffort == y.SupportsEffort && x.SupportedEffortLevels.SequenceEqual(y.SupportedEffortLevels);

    public int GetHashCode(ModelInfo obj) => HashCode.Combine(obj.Value, obj.DisplayName);
}
