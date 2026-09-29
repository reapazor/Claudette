using System.Runtime.InteropServices;
using Claudette.Core.Development;
using Claudette.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Claudette.App.Services;

/// <summary>The window's side of a restart into a new build or a new release: its tabs, and closing.</summary>
public interface IRestartHost
{
    /// <summary>Whether a tab is starting, in a turn, or waiting on the user.</summary>
    bool AnyTabWorking { get; }

    /// <summary>Raised when a tab's status changes.</summary>
    event Action? TabsChanged;

    /// <summary>The open tabs, drafts and window placement, for the new build.</summary>
    RestartSnapshot Capture();

    /// <summary>Shows <paramref name="message"/> in place of the tabs and stops every tab, as closing Claudette does.</summary>
    Task CloseTabsAsync(string message);

    /// <summary>The new build didn't start: brings the tabs back here.</summary>
    void Recover(RestartSnapshot snapshot);

    /// <summary>The new build is up: closes this one.</summary>
    void Exit();
}

/// <summary>
/// For a source build of Claudette (DESIGN.md §9, "Working on Claudette"): watches the build output for a new build,
/// and restarts into it, handing over every tab. The old build waits for the new one to say it's up, and takes its
/// tabs back if it doesn't.
/// </summary>
public sealed class RestartService : IDisposable
{
    /// <summary>How long the new build has to start before this one takes its tabs back.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ReadyCheckInterval = TimeSpan.FromMilliseconds(250);

    private const int ErrorLines = 12;

    private readonly AppServices _services;
    private readonly IRestartHost _host;
    private readonly Action? _stopListening;
    private readonly Action? _resumeListening;
    private readonly ILogger _logger;
    private BuildWatcher? _watcher;
    private DateTime? _failedStamp;

    /// <param name="stopListening">Stops taking later launches, so the new build can.</param>
    /// <param name="resumeListening">Takes them again, when the new build didn't start.</param>
    public RestartService(AppServices services, DevelopmentBuild build, IRestartHost host, Action? stopListening = null, Action? resumeListening = null)
    {
        _services = services;
        Build = build;
        _host = host;
        _stopListening = stopListening;
        _resumeListening = resumeListening;
        _logger = services.Loggers.CreateLogger<RestartService>();
        _host.TabsChanged += OnTabsChanged;
    }

    public DevelopmentBuild Build { get; }

    /// <summary>The newer build that's ready, if there is one.</summary>
    public DateTime? ReadyStamp { get; private set; }

    /// <summary>Waiting for every tab to finish before restarting.</summary>
    public bool IsWaitingForIdle { get; private set; }

    public bool IsRestarting { get; private set; }

    /// <summary>Why the last restart didn't happen, or null.</summary>
    public string? LastError { get; private set; }

    public bool AnyTabWorking => _host.AnyTabWorking;

    /// <summary>Restart into new builds without asking, once no tab is working. Remembered on this machine.</summary>
    public bool RestartAutomatically
    {
        get => _services.State.RestartOnNewBuild;
        set
        {
            _services.State.RestartOnNewBuild = value;
            _services.SaveState();
            if (value && ReadyStamp is not null && ReadyStamp != _failedStamp)
            {
                RestartWhenIdle();
            }
        }
    }

    /// <summary>Raised on the UI thread when anything above changes, or a tab's status does.</summary>
    public event Action? Changed;

    /// <summary>Starts watching the build output.</summary>
    public void Start()
    {
        _watcher ??= new BuildWatcher(Build.SourceOutput, DevelopmentLaunch.AssemblyName, Build.RunningStamp, _services.Time);
        _watcher.BuildReady += stamp => _services.Dispatcher.Post(() => OnBuildReady(stamp));
    }

    /// <summary>For tests: checks the build output now instead of on the timer.</summary>
    internal void CheckNow() => _watcher?.Poll();

    private void OnBuildReady(DateTime stamp)
    {
        ReadyStamp = stamp;
        LastError = null;
        if (RestartAutomatically)
        {
            IsWaitingForIdle = true;
        }
        Changed?.Invoke();
        RestartIfIdle();
    }

    /// <summary>Restarts once no tab is working: straight away if none is.</summary>
    public void RestartWhenIdle()
    {
        IsWaitingForIdle = true;
        Changed?.Invoke();
        RestartIfIdle();
    }

    public void CancelWaiting()
    {
        IsWaitingForIdle = false;
        Changed?.Invoke();
    }

    private void OnTabsChanged()
    {
        Changed?.Invoke();
        RestartIfIdle();
    }

    private void RestartIfIdle()
    {
        if (IsWaitingForIdle && !IsRestarting && ReadyStamp is not null && !_host.AnyTabWorking)
        {
            _ = RestartAsync();
        }
    }

    /// <summary>
    /// Restarts into the newest build: copies it, hands the tabs to it and closes once it's up. Returns false if it
    /// didn't happen, with the reason in <see cref="LastError"/>; the tabs are back then.
    /// </summary>
    public async Task<bool> RestartAsync()
    {
        if (IsRestarting)
        {
            return false;
        }
        IsRestarting = true;
        IsWaitingForIdle = false;
        LastError = null;
        Changed?.Invoke();
        var stamp = ReadyStamp;
        try
        {
            BuildCopy? copy;
            try
            {
                copy = await Task.Run(() => new BuildCopies(_services.Paths.BuildCopiesDirectory).Create(Build.SourceOutput, RuntimeInformation.RuntimeIdentifier));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail($"The new build couldn't be copied: {ex.Message}", stamp);
            }
            if (copy is null)
            {
                return Fail("The build changed while it was being copied. It's offered again once it's finished.", null);
            }
            return await HandOverAsync(copy, stamp);
        }
        finally
        {
            IsRestarting = false;
            Changed?.Invoke();
        }
    }

    private async Task<bool> HandOverAsync(BuildCopy copy, DateTime? stamp)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var snapshot = _host.Capture();
        snapshot.Nonce = nonce;
        snapshot.CreatedAt = _services.Time.GetUtcNow();

        // From here the new build owns the settings and state files: flush, then stop writing them.
        await _services.FlushAsync();
        _services.SuspendSaving = true;
        await _host.CloseTabsAsync("Restarting into the new build…");
        IRunningProcess? process = null;
        var errors = new Queue<string>();
        try
        {
            snapshot.Save(_services.Paths.RestartFile);
            RestartSnapshot.Delete(_services.Paths.RestartReadyFile);
            _stopListening?.Invoke();
            process = _services.Launcher.Start(DevelopmentLaunch.Command(copy.Directory,
                [LaunchArguments.SourceBuildOption, Build.SourceOutput, LaunchArguments.RestoreOption, nonce]));
            var collecting = CollectErrorsAsync(process, errors);
            var outcome = await WaitForReadyAsync(process, nonce);
            if (outcome is null)
            {
                _logger.LogInformation("Restarted into the build from {Stamp:u}.", copy.Stamp);
                _host.Exit();
                return true;
            }
            process.Kill();
            try
            {
                // Its last words, for the error.
                await collecting.WaitAsync(TimeSpan.FromSeconds(2), _services.Time);
            }
            catch (TimeoutException)
            {
            }
            return Recover(snapshot, outcome + Tail(errors), stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            process?.Kill();
            return Recover(snapshot, $"The new build couldn't be started: {ex.Message}", stamp);
        }
    }

    /// <summary>Null once the new build is up, or why it isn't.</summary>
    private async Task<string?> WaitForReadyAsync(IRunningProcess process, string nonce)
    {
        var started = _services.Time.GetUtcNow();
        while (true)
        {
            if (RestartHandshake.IsReady(_services.Paths.RestartReadyFile, nonce))
            {
                return null;
            }
            if (process.Exited.IsCompleted)
            {
                // It may have signalled just before exiting; that still isn't a Claudette that's running.
                return $"The new build stopped as it started (exit code {await process.Exited}).";
            }
            if (_services.Time.GetUtcNow() - started >= StartTimeout)
            {
                return $"The new build didn't start within {StartTimeout.TotalSeconds:0} seconds, so it was stopped.";
            }
            await Task.WhenAny(process.Exited, Task.Delay(ReadyCheckInterval, _services.Time));
        }
    }

    private bool Recover(RestartSnapshot snapshot, string error, DateTime? stamp)
    {
        _logger.LogWarning("Restart into the new build failed: {Error}", error);
        RestartSnapshot.Delete(_services.Paths.RestartFile);
        _services.SuspendSaving = false;
        _resumeListening?.Invoke();
        _host.Recover(snapshot);
        return Fail(error + " Still running the earlier build.", stamp);
    }

    /// <summary>Records why the restart didn't happen. A build that failed isn't restarted into automatically again.</summary>
    private bool Fail(string error, DateTime? stamp)
    {
        LastError = error;
        if (stamp is not null)
        {
            _failedStamp = stamp;
        }
        else
        {
            ReadyStamp = null;
        }
        return false;
    }

    private static async Task CollectErrorsAsync(IRunningProcess process, Queue<string> errors)
    {
        await foreach (var line in process.StandardError.ReadAllAsync())
        {
            lock (errors)
            {
                errors.Enqueue(line);
                while (errors.Count > ErrorLines)
                {
                    errors.Dequeue();
                }
            }
        }
    }

    private static string Tail(Queue<string> errors)
    {
        lock (errors)
        {
            return errors.Count == 0 ? "" : "\n\n" + string.Join('\n', errors);
        }
    }

    public void Dispose()
    {
        _host.TabsChanged -= OnTabsChanged;
        _watcher?.Dispose();
    }
}
