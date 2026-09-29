using Claudette.Core.Development;
using Claudette.Core.Updates;
using Microsoft.Extensions.Logging;

namespace Claudette.App.Services;

/// <summary>
/// Claudette's own updates (DESIGN.md §2, "Updating Claudette"): checks GitHub releases at launch and every few hours,
/// downloads the package for this platform, and installs it with the same handover a source build uses to restart into
/// a new build (§9): every tab, draft and the window's placement come back in the new version. If the install fails
/// before Claudette closes, the tabs come straight back here.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>How long a new version started by Claudette itself has to say it's up.</summary>
    public static readonly TimeSpan StartTimeout = RestartService.StartTimeout;

    private static readonly TimeSpan ReadyCheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly AppServices _services;
    private readonly IRestartHost _host;
    private readonly IAppInstaller _installer;
    private readonly ReleaseFeed _feed;
    private readonly UpdateDownloader _downloader;
    private readonly Action? _stopListening;
    private readonly Action? _resumeListening;
    private readonly System.Runtime.InteropServices.Architecture _architecture;
    private readonly ILogger _logger;
    private readonly Lock _checkLock = new();
    private ITimer? _timer;
    private Task? _checking;
    private CancellationTokenSource? _download;
    private bool _disposed;

    /// <param name="isSourceBuild">A source build gets its new builds from the checkout, so it never checks releases.</param>
    /// <param name="stopListening">Stops taking later launches, so the new version can.</param>
    /// <param name="resumeListening">Takes them again, when the install didn't happen.</param>
    /// <param name="architecture">Which release package fits this machine; by default, this process's architecture.</param>
    public AppUpdateService(AppServices services, IRestartHost host, bool isSourceBuild, Action? stopListening = null, Action? resumeListening = null, ReleaseFeed? feed = null,
        System.Runtime.InteropServices.Architecture? architecture = null)
    {
        _architecture = architecture ?? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        _services = services;
        _host = host;
        IsSourceBuild = isSourceBuild;
        _installer = isSourceBuild ? new NoAppInstaller(AppInstallKind.SourceBuild) : services.AppInstaller;
        _feed = feed ?? new ReleaseFeed(services.Http, services.UserAgent);
        _downloader = new UpdateDownloader(services.Http, services.Paths.UpdatesDirectory, services.UserAgent);
        _stopListening = stopListening;
        _resumeListening = resumeListening;
        _logger = services.Loggers.CreateLogger<AppUpdateService>();
        _host.TabsChanged += OnTabsChanged;
    }

    public AppVersion CurrentVersion => _services.AppVersion;

    public bool IsSourceBuild { get; }

    public AppInstallKind Kind => _installer.Kind;

    /// <summary>This copy can download and install a release itself; otherwise releases are offered as a link.</summary>
    public bool CanInstall => _installer.CanInstall;

    /// <summary>The newest release above this version, from the last check.</summary>
    public AvailableUpdate? Available { get; private set; }

    /// <summary>Whether the user skipped <see cref="Available"/>: it isn't announced, but Settings still shows it.</summary>
    public bool IsSkipped => Available is { } update && _services.State.SkippedAppUpdate == update.Version.ToString();

    public bool IsChecking { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Why the last check failed, or null.</summary>
    public string? CheckError { get; private set; }

    public bool IsDownloading { get; private set; }

    /// <summary>From 0 to 1 while downloading.</summary>
    public double DownloadProgress { get; private set; }

    /// <summary>The verified package for <see cref="Available"/>, once downloaded.</summary>
    public string? DownloadedPath { get; private set; }

    public bool IsInstalling { get; private set; }

    /// <summary>Waiting for every tab to finish before installing.</summary>
    public bool IsWaitingForIdle { get; private set; }

    /// <summary>Why the last download or install didn't happen, or null.</summary>
    public string? LastError { get; private set; }

    /// <summary>The downloaded package can still be installed by opening it: the OS's own installer takes over.</summary>
    public bool CanOpenPackage { get; private set; }

    /// <summary>The version this one was updated from, when it was just installed by an update.</summary>
    public string? UpdatedFrom { get; private set; }

    public bool AnyTabWorking => _host.AnyTabWorking;

    public string ReleasesPage => _feed.ReleasesPage;

    /// <summary>Raised on the UI thread when anything above changes, or a tab's status does.</summary>
    public event Action? Changed;

    /// <summary>Starts checking at launch and every few hours, if Settings allows it; and clears out old downloads.</summary>
    public void Start()
    {
        if (IsSourceBuild)
        {
            return;
        }
        var current = CurrentVersion;
        _ = Task.Run(() => _downloader.CleanUp(v => v > current));
        OnSettingsChanged();
    }

    public void OnSettingsChanged()
    {
        if (!_disposed && !IsSourceBuild && _services.Settings.General.CheckForAppUpdates)
        {
            _timer ??= _services.Time.CreateTimer(_ => _ = CheckNowAsync(), null, TimeSpan.Zero, CheckInterval);
        }
        else
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>For tests: the check under way or last run, to wait for it to finish.</summary>
    internal Task? LastCheck
    {
        get
        {
            lock (_checkLock)
            {
                return _checking;
            }
        }
    }

    /// <summary>Checks now. A check already under way is shared rather than started twice.</summary>
    public Task CheckNowAsync()
    {
        if (IsSourceBuild)
        {
            return Task.CompletedTask;
        }
        lock (_checkLock)
        {
            return _checking is { IsCompleted: false } running ? running : _checking = RunCheckAsync();
        }
    }

    private async Task RunCheckAsync()
    {
        _services.Dispatcher.Post(() =>
        {
            IsChecking = true;
            Changed?.Invoke();
        });
        IReadOnlyList<AppRelease>? releases = null;
        string? error = null;
        try
        {
            releases = await _feed.GetReleasesAsync().ConfigureAwait(false);
        }
        catch (ReleaseFeedException ex)
        {
            error = ex.Message;
            _logger.LogInformation("Claudette update check: {Error}", ex.Message);
        }
        var includePrereleases = _services.Settings.General.IncludePrereleases;
        _services.Dispatcher.Post(() =>
        {
            IsChecking = false;
            LastChecked = _services.Time.GetUtcNow();
            CheckError = error;
            if (releases is not null)
            {
                var found = AvailableUpdate.Find(releases, CurrentVersion, includePrereleases, _installer.Kind, _architecture);
                // The same release again keeps its download, finished or under way.
                if (found?.Version != Available?.Version || found?.Asset != Available?.Asset)
                {
                    CancelDownload();
                    DownloadedPath = null;
                    DownloadProgress = 0;
                    Available = found;
                }
            }
            Changed?.Invoke();
        });
    }

    /// <summary>Downloads the package for <see cref="Available"/> in the background. False if it didn't finish.</summary>
    public async Task<bool> DownloadAsync()
    {
        if (IsDownloading || Available is not { Asset: { } asset } update)
        {
            return false;
        }
        if (DownloadedPath is not null)
        {
            return true;
        }
        IsDownloading = true;
        DownloadProgress = 0;
        LastError = null;
        CanOpenPackage = false;
        Changed?.Invoke();
        var cancel = _download = new CancellationTokenSource();
        var lastPercent = -1;
        var progress = new Progress<double>(value =>
        {
            // Progress<T> reports on the UI thread that created it. Only redraw when the percentage moves.
            var percent = (int)(value * 100);
            if (percent != lastPercent && update == Available)
            {
                lastPercent = percent;
                DownloadProgress = value;
                Changed?.Invoke();
            }
        });
        try
        {
            var path = await Task.Run(() => _downloader.DownloadAsync(asset, update.Version, progress, cancel.Token));
            if (update == Available)
            {
                DownloadedPath = path;
            }
            return update == Available;
        }
        catch (UpdateDownloadException ex)
        {
            LastError = ex.Message;
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (_download == cancel)
            {
                _download = null;
                IsDownloading = false;
            }
            cancel.Dispose();
            Changed?.Invoke();
        }
    }

    public void CancelDownload() => _download?.Cancel();

    /// <summary>
    /// <b>Restart to update</b>: downloads the package if it isn't yet, checks it, closes the tabs and installs it.
    /// Returns false if the update didn't happen, with the reason in <see cref="LastError"/>; the tabs are back then.
    /// </summary>
    public async Task<bool> InstallAsync()
    {
        if (IsInstalling || !CanInstall || Available is not { Asset: not null } update)
        {
            return false;
        }
        IsInstalling = true;
        IsWaitingForIdle = false;
        LastError = null;
        CanOpenPackage = false;
        Changed?.Invoke();
        try
        {
            if (DownloadedPath is null && (!await DownloadAsync() || DownloadedPath is null))
            {
                return false;
            }
            PreparedInstall prepared;
            try
            {
                prepared = await _installer.PrepareAsync(DownloadedPath, update.Version);
            }
            catch (AppInstallException ex)
            {
                return Fail(ex.Message, ex.CanOpenPackage);
            }
            return await HandOverAsync(prepared);
        }
        finally
        {
            IsInstalling = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Installs once no tab is working: straight away if none is.</summary>
    public void InstallWhenIdle()
    {
        IsWaitingForIdle = true;
        Changed?.Invoke();
        InstallIfIdle();
    }

    public void CancelWaiting()
    {
        IsWaitingForIdle = false;
        Changed?.Invoke();
    }

    /// <summary>Stops announcing this release; a newer one is announced again.</summary>
    public void Skip()
    {
        if (Available is { } update)
        {
            _services.State.SkippedAppUpdate = update.Version.ToString();
            _services.SaveState();
            Changed?.Invoke();
        }
    }

    public Task OpenReleasePageAsync() => _services.Platform.OpenUrlAsync(Available?.Release.PageUrl is { Length: > 0 } page ? page : ReleasesPage);

    /// <summary>Opens the downloaded package, for installing it by hand: App Installer on Windows, Finder on macOS.</summary>
    public Task OpenPackageAsync() => DownloadedPath is { } path ? _services.Platform.OpenFileAsync(path) : Task.CompletedTask;

    /// <summary>
    /// This launch took over the tabs closed for an update. If the version didn't change, the update didn't install:
    /// say so, and offer it again.
    /// </summary>
    public void OnRestoredAfterUpdate(AppUpdateHandover handover)
    {
        if (AppVersion.TryParse(handover.To) is { } target && CurrentVersion < target)
        {
            LastError = $"The update to Claudette {target} didn't finish, so this is still {CurrentVersion}. Your tabs are back.";
            CanOpenPackage = false;
        }
        else
        {
            UpdatedFrom = handover.From;
        }
        Changed?.Invoke();
    }

    private void OnTabsChanged()
    {
        Changed?.Invoke();
        InstallIfIdle();
    }

    private void InstallIfIdle()
    {
        if (IsWaitingForIdle && !IsInstalling && !_host.AnyTabWorking)
        {
            _ = InstallAsync();
        }
    }

    private async Task<bool> HandOverAsync(PreparedInstall prepared)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var snapshot = _host.Capture();
        snapshot.Nonce = nonce;
        snapshot.CreatedAt = _services.Time.GetUtcNow();
        snapshot.Update = new AppUpdateHandover(CurrentVersion.ToString(), prepared.Version.ToString());

        // From here the new version owns the settings and state files: flush, then stop writing them.
        await _services.FlushAsync();
        _services.SuspendSaving = true;
        await _host.CloseTabsAsync($"Installing Claudette {prepared.Version}…");
        try
        {
            snapshot.Save(_services.Paths.RestartFile);
            RestartSnapshot.Delete(_services.Paths.RestartReadyFile);
            _stopListening?.Invoke();
            var outcome = await _installer.InstallAsync(prepared, [LaunchArguments.RestoreOption, nonce], _services.Paths.RestartReadyFile, nonce);
            if (outcome == InstallOutcome.ExitNow || await WaitForReadyAsync(nonce))
            {
                _logger.LogInformation("Installing Claudette {Version}.", prepared.Version);
                _host.Exit();
                return true;
            }
            return Recover(snapshot, $"Claudette {prepared.Version} is installed, but didn't start within {StartTimeout.TotalSeconds:0} seconds. Open Claudette again to use it.", false);
        }
        catch (AppInstallException ex)
        {
            return Recover(snapshot, ex.Message, ex.CanOpenPackage);
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the tabs are closed and saving is off: they must come back.
            _logger.LogError(ex, "Installing Claudette {Version} failed.", prepared.Version);
            return Recover(snapshot, $"The update couldn't be started: {ex.Message}", true);
        }
    }

    private async Task<bool> WaitForReadyAsync(string nonce)
    {
        var started = _services.Time.GetUtcNow();
        while (_services.Time.GetUtcNow() - started < StartTimeout)
        {
            if (RestartHandshake.IsReady(_services.Paths.RestartReadyFile, nonce))
            {
                return true;
            }
            await Task.Delay(ReadyCheckInterval, _services.Time);
        }
        return false;
    }

    private bool Recover(RestartSnapshot snapshot, string error, bool canOpenPackage)
    {
        _logger.LogWarning("Claudette update didn't install: {Error}", error);
        RestartSnapshot.Delete(_services.Paths.RestartFile);
        _services.SuspendSaving = false;
        _resumeListening?.Invoke();
        _host.Recover(snapshot);
        return Fail(error, canOpenPackage);
    }

    private bool Fail(string error, bool canOpenPackage)
    {
        LastError = error;
        CanOpenPackage = canOpenPackage && DownloadedPath is not null;
        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        _host.TabsChanged -= OnTabsChanged;
        _timer?.Dispose();
        _download?.Cancel();
    }
}
