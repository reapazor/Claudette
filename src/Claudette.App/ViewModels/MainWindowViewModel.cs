using Claudette.App.Services;
using Claudette.Core.Auth;
using Claudette.Core.Development;
using Claudette.Core.Installation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>
/// Runs the startup checks (Claude Code installed, then signed in) and shows the matching page (DESIGN.md §2, §11).
/// </summary>
public sealed partial class MainWindowViewModel(AppServices services, string? initialFolder = null) : ViewModelBase, IAsyncDisposable, IRestartHost
{
    private ShellViewModel? _shell;

    [ObservableProperty]
    public partial ViewModelBase CurrentPage { get; set; } = new BusyViewModel("Starting…");

    /// <summary>The account menu, the sign-in banner and its dialog, and signing out (DESIGN.md §11).</summary>
    public AccountViewModel Account { get; } = new(services);

    /// <summary>The signed-in email and plan, or null when signed out.</summary>
    public string? AccountText => Account.IsSignedIn ? Account.Summary : null;

    /// <summary>Why the usage header is missing, when usage tracking couldn't start.</summary>
    [ObservableProperty]
    public partial string? UsageNote { get; set; }

    /// <summary>The usage header (DESIGN.md §6), once signed in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsage))]
    public partial UsageViewModel? Usage { get; set; }

    /// <summary>True once plan usage has arrived. API-key accounts have no plan limits, so the header stays plain.</summary>
    public bool HasUsage => Usage is { HasData: true };

    /// <summary>The sidebar's "Claude Code … is ready" badge and Settings' update section (DESIGN.md §12).</summary>
    [ObservableProperty]
    public partial ClaudeUpdateViewModel? Updates { get; set; }

    private ClaudeUpdateResult? _launchUpdate;

    /// <summary>The main UI, once Claude Code is installed and signed in.</summary>
    public ShellViewModel? Shell => _shell;

    public AppServices Services => services;

    public Task StartAsync()
    {
        services.Notifications.Activated += OnNotificationActivated;
        Account.CheckSignIn = CheckSignInAsync;
        // Signed out from the account menu or Settings: the tabs wait for the next sign-in (DESIGN.md §11), and the
        // utility session starts again when next needed, without the old account.
        Account.SignedOut += () =>
        {
            _shell?.OnSignedOut();
            _ = services.StopUtilitySessionAsync();
        };
        Account.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountViewModel.Status))
            {
                OnPropertyChanged(nameof(AccountText));
            }
        };
        return CheckInstallAsync(services.Settings.ClaudeCode.ClaudePath);
    }

    private async Task CheckInstallAsync(string? path)
    {
        CurrentPage = new BusyViewModel("Looking for Claude Code…");
        var result = await services.Locator.LocateAsync(path);
        if (result.Install is { } found && services.State.UpdateClaudeOnNextLaunch)
        {
            // Update on next launch (DESIGN.md §12): before any tab or the utility session starts a claude process.
            services.State.UpdateClaudeOnNextLaunch = false;
            services.SaveState();
            CurrentPage = new BusyViewModel("Updating Claude Code…");
            _launchUpdate = await UpdateClaudeAsync(found.Path, null);
            result = await services.Locator.LocateAsync(path);
        }
        if (!result.IsUsable)
        {
            // Too old: the setup screen offers the same Update now as the header (DESIGN.md §12, "Minimum version").
            var update = result is { Problem: Core.Installation.ClaudeInstallProblem.TooOld, Install: { } old }
                ? output => UpdateClaudeAsync(old.Path, output)
                : (Func<Action<string>, Task<ClaudeUpdateResult>>?)null;
            CurrentPage = new SetupViewModel(result, services.Platform, CheckInstallAsync, update, () => CheckInstallAsync(path));
            return;
        }
        if (path is not null && path != services.Settings.ClaudeCode.ClaudePath)
        {
            // The user pointed Claudette at claude from the setup screen: remember it.
            services.Settings.ClaudeCode.ClaudePath = path;
            services.SaveSettings();
        }
        services.UseInstall(result.Install!);
        if (_launchUpdate is { } launchUpdate)
        {
            services.ClaudeUpdates!.RecordLaunchUpdate(launchUpdate);
            _launchUpdate = null;
        }
        await CheckSignInAsync();
    }

    /// <summary>Checks how <paramref name="claudePath"/> was installed, then runs its update command.</summary>
    private async Task<ClaudeUpdateResult> UpdateClaudeAsync(string claudePath, Action<string>? onOutput)
    {
        try
        {
            var updater = services.CreateUpdater(claudePath);
            var check = await Task.Run(() => updater.CheckAsync());
            if (!check.Plan.CanRun)
            {
                return new ClaudeUpdateResult(false, check.InstalledVersion,
                    check.Plan.ManualCommand is { } manual ? $"Run this to update Claude Code: {manual}" : check.Plan.Note ?? "Claudette can't update this installation.");
            }
            return await Task.Run(() => updater.UpdateAsync(check.Plan, onOutput is null ? null : line => services.Dispatcher.Post(() => onOutput(line))));
        }
        catch (Exception ex)
        {
            return new ClaudeUpdateResult(false, null, $"The update failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs <c>claude auth status</c> (DESIGN.md §11, "Detecting"). At launch, signed out shows the sign-in screen
    /// instead of the tabs; later, it keeps the banner and the dialog that's open.
    /// </summary>
    private async Task CheckSignInAsync()
    {
        var midSession = _shell is not null;
        if (!midSession && CurrentPage is not SignInViewModel)
        {
            CurrentPage = new BusyViewModel("Checking your sign-in…");
        }
        AuthStatus status;
        try
        {
            status = await services.Auth!.GetStatusAsync();
        }
        catch (Exception ex)
        {
            var message = $"Couldn't check your sign-in: {ex.Message}";
            if (midSession)
            {
                Account.SignIn?.ShowCheckFailed(message);
            }
            else if (CurrentPage is SignInViewModel page)
            {
                page.ShowCheckFailed(message);
            }
            else
            {
                CurrentPage = new SignInViewModel(services, CheckSignInAsync) { Error = message };
            }
            return;
        }

        Account.Status = status;
        if (!status.LoggedIn)
        {
            if (midSession)
            {
                Account.SignIn?.ShowStillSignedOut();
            }
            else if (CurrentPage is SignInViewModel page)
            {
                page.ShowStillSignedOut();
            }
            else
            {
                CurrentPage = new SignInViewModel(services, CheckSignInAsync);
            }
            return;
        }

        Account.OnSignedIn(status);
        services.ProjectsDirectory = status.ProjectsDirectory
            ?? (status.ConfigDirectory is { } config ? Path.Combine(config, "projects") : null);
        services.ClaudeConfigDirectory = status.ConfigDirectory
            ?? (status.ProjectsDirectory is { } projects ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(projects)) : null);
        if (_shell is null)
        {
            _shell = new ShellViewModel(services, ShowSignIn);
            _shell.TabStatusChanged += () => _tabsChanged?.Invoke();
            OnPropertyChanged(nameof(Shell));
            CurrentPage = _shell;
            var restored = _restore;
            _restore = null;
            if (restored is not null)
            {
                // Restarted into this build or release: the tabs the last one had (DESIGN.md §9, "Working on
                // Claudette"). The window deletes the snapshot once this build is up.
                _shell.Restore(null, restored);
            }
            else
            {
                _shell.Restore(initialFolder);
            }
            StartUsage();
            StartUpdateChecks(_shell);
            StartRestarts(_shell);
            StartAppUpdates(_shell, restored?.Update);
        }
        else
        {
            CurrentPage = _shell;
            // Like the tabs, the utility session (plan usage) starts again, with the new sign-in.
            await services.StopUtilitySessionAsync();
            await _shell.OnSignedInAgainAsync();
        }
    }

    /// <summary>Starts plan usage tracking and the header. A failure here never stops the tabs from working.</summary>
    private void StartUsage()
    {
        try
        {
            var tracker = services.StartUsageTracking();
            var usage = new UsageViewModel(services, tracker);
            usage.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UsageViewModel.HasData))
                {
                    OnPropertyChanged(nameof(HasUsage));
                }
            };
            services.SettingsChanged += (_, _) => tracker.OnSettingsChanged();
            tracker.TurnRecorded += () => _shell?.OnTurnRecorded();
            _shell?.OnTurnRecorded();
            Usage = usage;
        }
        catch (Exception ex)
        {
            UsageNote = $"Usage unavailable ({ex.Message})";
        }
    }

    /// <summary>Checks for Claude Code updates at launch and every few hours, and shows the sidebar's badge (DESIGN.md §12).</summary>
    private void StartUpdateChecks(ShellViewModel shell)
    {
        if (services.ClaudeUpdates is not { } updates)
        {
            return;
        }
        var viewModel = new ClaudeUpdateViewModel(services, updates, () => shell.RunningVersions);
        shell.RunningVersionsChanged += viewModel.Refresh;
        Updates = viewModel;
        shell.Updates = viewModel;
        updates.Start();
    }

    /// <summary>
    /// A tab reported that Claude Code needs a sign-in (DESIGN.md §11): a banner across all tabs, which stay open, and
    /// an OS notification if Claudette isn't in front. The account menu catches up with <c>claude auth status</c>.
    /// </summary>
    private void ShowSignIn()
    {
        if (!Account.NeedsSignIn)
        {
            Account.RequireSignIn();
            _ = Account.RefreshAsync();
        }
    }

    /// <summary>Asks the window to come to the front, for a clicked notification or a second launch.</summary>
    public event Action? BringToFrontRequested;

    /// <summary>
    /// Claudette was launched again, for example from the jump list or Open Recent (DESIGN.md §4, "Other ways in"):
    /// come to the front, and open a tab in the folder it names.
    /// </summary>
    public void OnLaunchedAgain(IReadOnlyList<string> args)
    {
        BringToFrontRequested?.Invoke();
        if (LaunchArguments.Folder(args) is { } folder && _shell is not null && CurrentPage == _shell)
        {
            _ = _shell.OpenFolderAsync(folder);
        }
    }

    /// <summary>
    /// A notification was clicked (DESIGN.md §10): bring Claudette to the front and go to the tab or screen it was
    /// about.
    /// </summary>
    internal void OnNotificationActivated(NotificationTarget target)
    {
        BringToFrontRequested?.Invoke();
        switch (target.Kind)
        {
            case NotificationKind.UsageAlert:
                Usage?.OpenPanelCommand.Execute(null);
                break;
            case NotificationKind.UpdateReady:
                Updates?.RequestOpen();
                break;
            case NotificationKind.SignIn:
                // At launch the sign-in screen is already showing; later, the banner's dialog, ready to start.
                if (Account.NeedsSignIn)
                {
                    Account.ShowSignIn();
                }
                break;
            default:
                if (target.TabId is { } tabId && CurrentPage == _shell)
                {
                    _shell?.SelectTab(tabId);
                }
                break;
        }
    }

    // ---- Restarting into a new build (DESIGN.md §9, "Working on Claudette") -----------------------------------

    private DevelopmentBuild? _development;
    private Action? _stopListening;
    private Action? _resumeListening;
    private RestartSnapshot? _restore;
    private Action? _tabsChanged;

    /// <summary>Watches for new builds and restarts into them, when running from a source build.</summary>
    public RestartService? Restarts { get; private set; }

    /// <summary>Set by the window: where it is and how big, for the build this one restarts into.</summary>
    public Func<WindowPlacement?>? GetWindowPlacement { get; set; }

    /// <summary>Raised when the new build is up and this one should close.</summary>
    public event Action? ExitRequested;

    /// <summary>This is a source build: offer its new builds once the tabs are up.</summary>
    /// <param name="stopListening">Stops taking later launches, so the new build can.</param>
    /// <param name="resumeListening">Takes them again if the new build doesn't start.</param>
    public void UseDevelopmentBuild(DevelopmentBuild build, Action? stopListening = null, Action? resumeListening = null)
    {
        _development = build;
        UseSingleInstance(stopListening, resumeListening);
    }

    /// <summary>How a restart hands later launches to the new build or version, and takes them back if it fails.</summary>
    public void UseSingleInstance(Action? stopListening, Action? resumeListening)
    {
        _stopListening = stopListening;
        _resumeListening = resumeListening;
    }

    // ---- Claudette's own updates (DESIGN.md §2, "Updating Claudette") --------------------------------------------

    /// <summary>Release checks, downloads and installs.</summary>
    public AppUpdateService? AppUpdates { get; private set; }

    /// <summary>The sidebar's update badge and Settings' Claudette updates section.</summary>
    [ObservableProperty]
    public partial AppUpdateViewModel? AppUpdate { get; private set; }

    private void StartAppUpdates(ShellViewModel shell, AppUpdateHandover? handover)
    {
        var updates = AppUpdates = new AppUpdateService(services, this, _development is not null, _stopListening, _resumeListening);
        AppUpdate = shell.AppUpdate = new AppUpdateViewModel(updates);
        services.SettingsChanged += (_, _) => updates.OnSettingsChanged();
        if (handover is not null)
        {
            updates.OnRestoredAfterUpdate(handover);
        }
        updates.Start();
    }

    /// <summary>The tabs to open instead of the saved ones: this build was restarted into from an earlier one.</summary>
    public void RestoreOnStart(RestartSnapshot snapshot) => _restore = snapshot;

    private void StartRestarts(ShellViewModel shell)
    {
        if (_development is not { } build)
        {
            return;
        }
        Restarts = new RestartService(services, build, this, _stopListening, _resumeListening);
        shell.NewBuild = new NewBuildViewModel(Restarts);
        _ = Task.Run(() => new BuildCopies(services.Paths.BuildCopiesDirectory).CleanUp(build.RunningDirectory));
        Restarts.Start();
    }

    bool IRestartHost.AnyTabWorking => _shell?.AnyTabWorking == true;

    event Action? IRestartHost.TabsChanged
    {
        add => _tabsChanged += value;
        remove => _tabsChanged -= value;
    }

    RestartSnapshot IRestartHost.Capture()
    {
        var snapshot = _shell?.CaptureForRestart() ?? new RestartSnapshot();
        snapshot.Window = GetWindowPlacement?.Invoke();
        return snapshot;
    }

    async Task IRestartHost.CloseTabsAsync(string message)
    {
        CurrentPage = new BusyViewModel(message);
        if (_shell is not null)
        {
            await _shell.CloseTabsForRestartAsync();
        }
    }

    void IRestartHost.Recover(RestartSnapshot snapshot)
    {
        if (_shell is not null)
        {
            _shell.Restore(null, snapshot);
            CurrentPage = _shell;
        }
    }

    void IRestartHost.Exit() => ExitRequested?.Invoke();

    public async ValueTask DisposeAsync()
    {
        services.Notifications.Activated -= OnNotificationActivated;
        Usage?.Dispose();
        Updates?.Dispose();
        _shell?.NewBuild?.Dispose();
        Restarts?.Dispose();
        AppUpdate?.Dispose();
        AppUpdates?.Dispose();
        if (_shell is not null)
        {
            await _shell.DisposeAsync();
        }
    }
}
