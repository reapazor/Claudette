using Claudette.Core.Status;
using Microsoft.Extensions.Logging;

namespace Claudette.App.Services;

/// <summary>
/// Claude's service status (DESIGN.md §18, "Service status"): reads status.claude.com at launch and every 5 minutes,
/// and straight away (at most once a minute) when a tab's request to the Claude API fails on Anthropic's side. A
/// failed read backs off, 5 minutes, then 10, up to 30, and the status is unknown meanwhile. Runs only while Settings →
/// General → Show Claude's service status is on. The header's dot and the banner are <c>ServiceStatusViewModel</c>.
/// </summary>
public sealed class ServiceStatusService : IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>The longest wait between checks while the page can't be read.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    /// <summary>A tab's API errors bring the next check forward, but no nearer than this to the last one.</summary>
    public static readonly TimeSpan TroubleInterval = TimeSpan.FromMinutes(1);

    private readonly AppServices _services;
    private readonly StatusFeed _feed;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private ITimer? _timer;
    private Task? _checking;
    private DateTimeOffset? _lastStarted;
    private DateTimeOffset? _nextDue;
    private int _failures;

    /// <summary>Counts each time checking is turned on or off, so an answer that arrives afterwards is dropped.</summary>
    private int _generation;

    private bool _started;
    private bool _disposed;

    public ServiceStatusService(AppServices services, StatusFeed? feed = null)
    {
        _services = services;
        _feed = feed ?? new StatusFeed(services.Http, services.UserAgent);
        _logger = services.Loggers.CreateLogger<ServiceStatusService>();
    }

    /// <summary>Started, and the setting is on: the dot shows.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The last check's answer, null before the first. Its level is <see cref="ServiceLevel.Unknown"/> when it failed.</summary>
    public ServiceStatusReport? Report { get; private set; }

    /// <summary>Why the last check failed, or null.</summary>
    public string? Error { get; private set; }

    /// <summary>Raised on the UI thread when anything above changes, or the banner is dismissed.</summary>
    public event Action? Changed;

    /// <summary>The banner shows: something watched isn't well, and the user hasn't dismissed it.</summary>
    public bool ShowsBanner => IsActive && Report is { ShowsBanner: true } report && _services.State.DismissedServiceStatus?.Covers(report) != true;

    /// <summary>At launch: checks now and every 5 minutes after, if Settings allows.</summary>
    public void Start()
    {
        _started = true;
        OnSettingsChanged();
    }

    /// <summary>Turning the setting off stops the checks and hides the dot and banner; on checks straight away.</summary>
    public void OnSettingsChanged()
    {
        var on = _started && !_disposed && _services.Settings.General.ShowServiceStatus;
        if (on == IsActive)
        {
            return;
        }
        lock (_lock)
        {
            _generation++;
            _timer?.Dispose();
            _timer = null;
            _nextDue = null;
            _lastStarted = null;
            _failures = 0;
            if (on)
            {
                _timer = _services.Time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
        IsActive = on;
        Report = null;
        Error = null;
        Changed?.Invoke();
        if (on)
        {
            _ = CheckNowAsync();
        }
    }

    /// <summary>
    /// A tab's request failed on Anthropic's side (<see cref="ApiTrouble"/>): check now, unless a check is under way or
    /// the last one started less than a minute ago, when the next is brought forward to a minute after it.
    /// </summary>
    public void OnApiTrouble()
    {
        DateTimeOffset now;
        lock (_lock)
        {
            if (_timer is null || _checking is { IsCompleted: false })
            {
                return;
            }
            now = _services.Time.GetUtcNow();
            if (_lastStarted is { } last && now - last < TroubleInterval)
            {
                var due = last + TroubleInterval;
                if (_nextDue is null || due < _nextDue)
                {
                    ScheduleLocked(due, now);
                }
                return;
            }
        }
        _ = CheckNowAsync();
    }

    /// <summary>For tests: the check under way or last run, to wait for it to finish.</summary>
    internal Task? LastCheck
    {
        get
        {
            lock (_lock)
            {
                return _checking;
            }
        }
    }

    /// <summary>When the next check is due, for tests.</summary>
    internal DateTimeOffset? NextCheck
    {
        get
        {
            lock (_lock)
            {
                return _nextDue;
            }
        }
    }

    /// <summary>Checks now. A check already under way is shared rather than started twice.</summary>
    public Task CheckNowAsync()
    {
        lock (_lock)
        {
            if (_timer is null)
            {
                return Task.CompletedTask;
            }
            if (_checking is { IsCompleted: false } running)
            {
                return running;
            }
            _lastStarted = _services.Time.GetUtcNow();
            _nextDue = null;
            return _checking = RunCheckAsync(_generation);
        }
    }

    /// <summary>
    /// <b>Dismiss</b> on the banner: hidden until a different incident arrives or things get worse. Kept with this
    /// machine's state.
    /// </summary>
    public void Dismiss()
    {
        if (Report is { ShowsBanner: true } report)
        {
            _services.State.DismissedServiceStatus = ServiceStatusDismissal.Of(report);
            _services.SaveState();
            Changed?.Invoke();
        }
    }

    private void OnTimer() => _ = CheckNowAsync();

    private async Task RunCheckAsync(int generation)
    {
        ServiceStatusReport? report = null;
        string? error = null;
        try
        {
            // Off the caller's thread: a timer tick, a tab's event on the UI thread, or the setting.
            var summary = await Task.Run(() => _feed.GetSummaryAsync()).ConfigureAwait(false);
            report = ServiceStatusReport.From(summary, _services.Time.GetUtcNow());
        }
        catch (StatusFeedException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            // Whatever went wrong, the status is only unknown.
            error = $"Couldn't read status.claude.com: {ex.Message}";
        }
        if (error is not null)
        {
            _logger.LogInformation("Service status check: {Error}", error);
        }
        lock (_lock)
        {
            if (generation != _generation)
            {
                return;
            }
            _failures = report is null ? _failures + 1 : 0;
            var now = _services.Time.GetUtcNow();
            var wait = report is null ? Backoff(_failures) : CheckInterval;
            // A tab's API error may already have asked for a sooner check.
            if (_nextDue is not { } sooner || sooner > now + wait)
            {
                ScheduleLocked(now + wait, now);
            }
        }
        var asOf = _services.Time.GetUtcNow();
        _services.Dispatcher.Post(() =>
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            Report = report ?? ServiceStatusReport.Unknown(asOf);
            Error = error;
            if (report is { IsAllClear: true } && _services.State.DismissedServiceStatus is not null)
            {
                // Back to normal: the next incident shows, whatever it is.
                _services.State.DismissedServiceStatus = null;
                _services.SaveState();
            }
            Changed?.Invoke();
        });
    }

    /// <summary>After <paramref name="failures"/> failed checks in a row: 5 minutes, then 10, 20, and 30 from then on.</summary>
    public static TimeSpan Backoff(int failures) =>
        TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, CheckInterval.Ticks << Math.Clamp(failures - 1, 0, 16)));

    private void ScheduleLocked(DateTimeOffset due, DateTimeOffset now)
    {
        _nextDue = due;
        var wait = due - now;
        _timer?.Change(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock)
        {
            _generation++;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
