using Claudette.Core.Installation;
using Microsoft.Extensions.Logging;

namespace Claudette.App.Services;

/// <summary>
/// Claude Code updates (DESIGN.md §12): checks at launch and every few hours with <c>claude --version</c>,
/// <c>claude doctor</c> and the package manager, runs <b>Update now</b>, and keeps <b>Update on next launch</b>.
/// Open tabs are never restarted; they keep the version they started with.
/// </summary>
public sealed class ClaudeUpdateService : IAsyncDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    /// <summary>The changelog the update dialog links to (DESIGN.md §16, "What's watched").</summary>
    public const string ChangelogUrl = "https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md";

    private readonly AppServices _services;
    private readonly IClaudeUpdater _updater;
    private readonly ILogger _logger;
    private readonly Lock _checkLock = new();
    private ITimer? _timer;
    private Task? _checking;

    public ClaudeUpdateService(AppServices services, IClaudeUpdater updater, Version installedVersion)
    {
        _services = services;
        _updater = updater;
        _logger = services.Loggers.CreateLogger<ClaudeUpdateService>();
        InstalledVersion = installedVersion;
    }

    /// <summary>The version new tabs start with: from launch, then from each check. Only read on the UI thread.</summary>
    public Version InstalledVersion { get; private set; }

    /// <summary>The latest check, or null before the first one finishes.</summary>
    public ClaudeUpdateCheck? LatestCheck { get; private set; }

    public bool IsChecking { get; private set; }

    public bool IsUpdating { get; private set; }

    /// <summary>How the last <b>Update now</b> (or the update at launch) went.</summary>
    public ClaudeUpdateResult? LastResult { get; private set; }

    /// <summary><b>Update on next launch</b> is waiting for the next start.</summary>
    public bool IsUpdateScheduled => _services.State.UpdateClaudeOnNextLaunch;

    /// <summary>Raised on the UI thread whenever anything above changes.</summary>
    public event Action? Changed;

    /// <summary>Starts checking at launch and every few hours, if Settings allows it.</summary>
    public void Start() => OnSettingsChanged();

    public void OnSettingsChanged()
    {
        if (_services.Settings.ClaudeCode.CheckForUpdates)
        {
            _timer ??= _services.Time.CreateTimer(_ => _ = CheckNowAsync(), null, TimeSpan.Zero, CheckInterval);
        }
        else
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Checks now. A check already under way is shared rather than started twice.</summary>
    public Task CheckNowAsync()
    {
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
        ClaudeUpdateCheck? check = null;
        try
        {
            check = await Task.Run(() => _updater.CheckAsync()).ConfigureAwait(false);
            if (check.Error is { } error)
            {
                _logger.LogWarning("Claude Code update check: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't check for Claude Code updates.");
        }
        _services.Dispatcher.Post(() =>
        {
            IsChecking = false;
            if (check is not null)
            {
                LatestCheck = check;
                if (check.InstalledVersion is { } installed)
                {
                    InstalledVersion = installed;
                }
            }
            Changed?.Invoke();
        });
    }

    /// <summary>
    /// <b>Update now</b>: runs the install method's update command and reports the new version. WinGet can't replace a
    /// running <c>claude</c>, so the hidden utility session is stopped first; it restarts when next needed. The caller
    /// offers <b>Update on next launch</b> instead while a tab is running.
    /// </summary>
    /// <param name="onOutput">Each line of the command's output, on the UI thread.</param>
    public async Task<ClaudeUpdateResult> UpdateNowAsync(Action<string>? onOutput = null)
    {
        if (LatestCheck is null)
        {
            await CheckNowAsync();
        }
        if (LatestCheck?.Plan is not { CanRun: true } plan)
        {
            return new ClaudeUpdateResult(false, null, LatestCheck?.Plan.Note ?? "Claudette can't update this installation.");
        }
        IsUpdating = true;
        Changed?.Invoke();
        ClaudeUpdateResult result;
        try
        {
            if (plan.NeedsClaudeStopped)
            {
                await _services.StopUtilitySessionAsync();
            }
            result = await Task.Run(() => _updater.UpdateAsync(plan, onOutput is null ? null : line => _services.Dispatcher.Post(() => onOutput(line))));
        }
        catch (Exception ex)
        {
            result = new ClaudeUpdateResult(false, null, $"The update failed: {ex.Message}");
        }
        IsUpdating = false;
        LastResult = result;
        if (result.Version is { } version)
        {
            InstalledVersion = version;
        }
        Changed?.Invoke();
        await CheckNowAsync();
        return result;
    }

    /// <summary><b>Update on next launch</b>: saved with this machine's state, and run before any tab starts.</summary>
    public void ScheduleUpdateOnNextLaunch(bool scheduled = true)
    {
        _services.State.UpdateClaudeOnNextLaunch = scheduled;
        _services.SaveState();
        Changed?.Invoke();
    }

    /// <summary>The update that ran at launch, so the dialog and Settings can report it.</summary>
    public void RecordLaunchUpdate(ClaudeUpdateResult result)
    {
        LastResult = result;
        if (result.Version is { } version)
        {
            InstalledVersion = version;
        }
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_timer is not null)
        {
            await _timer.DisposeAsync();
            _timer = null;
        }
    }
}
