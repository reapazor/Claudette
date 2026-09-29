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

    [ObservableProperty]
    public partial string? AccountText { get; set; }

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

    private async Task CheckSignInAsync()
    {
        if (CurrentPage is not SignInViewModel)
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
            CurrentPage = new SignInViewModel(services, CheckSignInAsync) { Error = $"Couldn't check your sign-in: {ex.Message}" };
            return;
        }

        if (!status.LoggedIn)
        {
            AccountText = null;
            if (CurrentPage is not SignInViewModel)
            {
                CurrentPage = new SignInViewModel(services, CheckSignInAsync);
            }
            return;
        }

        services.Notifications.Clear(NotificationKind.SignIn);
        services.ProjectsDirectory = status.ProjectsDirectory
            ?? (status.ConfigDirectory is { } config ? Path.Combine(config, "projects") : null);
        AccountText = string.Join(" · ", new[] { status.Email, PlanName(status.SubscriptionType) }.Where(s => !string.IsNullOrEmpty(s)));
        if (_shell is null)
        {
            _shell = new ShellViewModel(services, ShowSignIn);
            _shell.TabStatusChanged += () => _tabsChanged?.Invoke();
            OnPropertyChanged(nameof(Shell));
            CurrentPage = _shell;
            if (_restore is { } snapshot)
            {
                // Restarted into this build: the tabs the last one had (DESIGN.md §9, "Working on Claudette").
                _restore = null;
                _shell.Restore(null, snapshot);
                RestartSnapshot.Delete(services.Paths.RestartFile);
            }
            else
            {
                _shell.Restore(initialFolder);
            }
            StartUsage();
            StartUpdateChecks(_shell);
            StartRestarts(_shell);
        }
        else
        {
            CurrentPage = _shell;
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
            AccountText = $"{AccountText} · usage unavailable ({ex.Message})";
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

    /// <summary>A tab reported that Claude Code needs a sign-in.</summary>
    private void ShowSignIn()
    {
        if (CurrentPage is not SignInViewModel)
        {
            CurrentPage = new SignInViewModel(services, CheckSignInAsync);
            // DESIGN.md §11: if Claudette isn't in front, an OS notification too.
            services.Notifications.Notify(NotificationKind.SignIn, "Claude Code needs you to sign in", "Your tabs are paused until you sign in again.");
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
                // The sign-in screen is already showing.
                break;
            default:
                if (target.TabId is { } tabId && CurrentPage == _shell)
                {
                    _shell?.SelectTab(tabId);
                }
                break;
        }
    }

    private static string? PlanName(string? subscriptionType) => subscriptionType switch
    {
        null or "" => null,
        "max" => "Max",
        "pro" => "Pro",
        _ => subscriptionType,
    };

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
        _stopListening = stopListening;
        _resumeListening = resumeListening;
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

    async Task IRestartHost.CloseTabsAsync()
    {
        CurrentPage = new BusyViewModel("Restarting into the new build…");
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
        if (_shell is not null)
        {
            await _shell.DisposeAsync();
        }
    }
}
