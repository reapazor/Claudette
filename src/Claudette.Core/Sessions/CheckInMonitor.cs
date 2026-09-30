using Claudette.Core.Settings;

namespace Claudette.Core.Sessions;

/// <summary>A check-in that's due, and when it's sent unless the user skips it or sends it sooner.</summary>
public sealed record CheckInCountdown(DateTimeOffset SendsAt, string Message);

/// <summary>
/// Decides when to send an "Everything OK?" check-in during a long turn (DESIGN.md §5, "Check-ins on long turns").
/// <list type="bullet">
/// <item>Run time: the turn has run longer than <see cref="CheckInSettings.RunTimeMinutes"/>.</item>
/// <item>Quiet time: no output for <see cref="CheckInSettings.QuietTimeMinutes"/>.</item>
/// <item>A check-in that's due waits <see cref="Countdown"/> first, so the user can skip it or send it now. Once sent,
/// Claude Code can't give it back. It's withdrawn if the turn no longer calls for it by then: Claude said something
/// after a quiet spell, say.</item>
/// <item>Paused while a permission prompt waits on the user.</item>
/// <item>Output after a check-in counts as the reply and restarts the timers. After two check-ins in a row without a
/// reply, it stops and reports the turn as possibly stuck.</item>
/// </list>
/// </summary>
public sealed class CheckInMonitor : IDisposable
{
    public const int MaxUnanswered = 2;
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long a check-in that's due waits before it's sent.</summary>
    public static readonly TimeSpan Countdown = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly Func<CheckInSettings> _settings;
    private readonly Action<string> _sendCheckIn;
    private readonly Action<bool> _possiblyStuckChanged;
    private readonly Action<CheckInCountdown?> _countdownChanged;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();
    private CheckInCountdown? _countdown;
    private ITimer? _countdownTimer;
    private bool _turnActive;
    private bool _waitingOnUser;
    private bool _possiblyStuck;
    private DateTimeOffset _runBase;
    private DateTimeOffset _lastOutput;
    private DateTimeOffset _waitStart;
    private DateTimeOffset? _lastCheckIn;
    private bool _checkInOutstanding;
    private int _unanswered;

    /// <param name="sendCheckIn">Called (on a timer thread, or the caller's) with the message to send.</param>
    /// <param name="possiblyStuckChanged">Called when the "possibly stuck" state changes.</param>
    /// <param name="countdownChanged">Called when a check-in starts counting down, or stops: sent, skipped or withdrawn.</param>
    public CheckInMonitor(TimeProvider time, Func<CheckInSettings> settings, Action<string> sendCheckIn, Action<bool> possiblyStuckChanged,
        Action<CheckInCountdown?> countdownChanged)
    {
        _time = time;
        _settings = settings;
        _sendCheckIn = sendCheckIn;
        _possiblyStuckChanged = possiblyStuckChanged;
        _countdownChanged = countdownChanged;
        _timer = time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
    }

    /// <summary>The check-in counting down to being sent, or null.</summary>
    public CheckInCountdown? PendingCheckIn
    {
        get
        {
            lock (_lock)
            {
                return _countdown;
            }
        }
    }

    public bool IsPossiblyStuck
    {
        get
        {
            lock (_lock)
            {
                return _possiblyStuck;
            }
        }
    }

    public void TurnStarted()
    {
        bool withdrawn;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _turnActive = true;
            _runBase = now;
            _lastOutput = now;
            _lastCheckIn = null;
            _checkInOutstanding = false;
            _unanswered = 0;
            withdrawn = StopCountdown();
        }
        CountdownStopped(withdrawn);
        SetStuck(false);
    }

    /// <summary>Anything Claude produced: text, a tool call, a tool result.</summary>
    public void OutputSeen()
    {
        bool withdrawn;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _lastOutput = now;
            if (_checkInOutstanding)
            {
                // That's the reply: restart the timers.
                _checkInOutstanding = false;
                _unanswered = 0;
                _runBase = now;
            }
            // Claude isn't quiet any more: a check-in that quiet time called for isn't needed.
            withdrawn = _countdown is not null && !IsDue(now, _settings()) && StopCountdown();
        }
        CountdownStopped(withdrawn);
        SetStuck(false);
    }

    public void TurnEnded()
    {
        bool withdrawn;
        lock (_lock)
        {
            _turnActive = false;
            _checkInOutstanding = false;
            withdrawn = StopCountdown();
        }
        CountdownStopped(withdrawn);
        SetStuck(false);
    }

    /// <summary>
    /// A permission prompt is waiting on the user (or no longer is). Check-ins pause meanwhile, and one counting down
    /// is withdrawn: the turn is waiting on the user, not on Claude.
    /// </summary>
    public void SetWaitingOnUser(bool waiting)
    {
        var withdrawn = false;
        lock (_lock)
        {
            if (waiting == _waitingOnUser)
            {
                return;
            }
            _waitingOnUser = waiting;
            var now = _time.GetUtcNow();
            if (waiting)
            {
                _waitStart = now;
                withdrawn = StopCountdown();
            }
            else
            {
                // Time spent waiting on the user counts neither as Claude being quiet nor as run time.
                _lastOutput = now;
                _runBase += now - _waitStart;
            }
        }
        CountdownStopped(withdrawn);
    }

    /// <summary><b>Send now</b>: the check-in counting down goes at once.</summary>
    public void SendNow()
    {
        string? message;
        lock (_lock)
        {
            message = _countdown?.Message;
            if (message is null || !StopCountdown())
            {
                return;
            }
            MarkSent(_time.GetUtcNow());
        }
        CountdownStopped(true);
        _sendCheckIn(message);
    }

    /// <summary>
    /// ✕ on the countdown: this check-in isn't sent, and the next one waits a whole interval again, as if Claude had
    /// just said something.
    /// </summary>
    public void Skip()
    {
        lock (_lock)
        {
            if (!StopCountdown())
            {
                return;
            }
            var now = _time.GetUtcNow();
            _runBase = now;
            _lastOutput = now;
            if (_checkInOutstanding)
            {
                _lastCheckIn = now;
            }
        }
        CountdownStopped(true);
    }

    /// <summary>Evaluates the triggers. Runs on the timer; public so tests can call it directly.</summary>
    public void Tick()
    {
        CheckInCountdown? started = null;
        var becameStuck = false;
        lock (_lock)
        {
            var settings = _settings();
            if (!settings.Enabled || !_turnActive || _waitingOnUser || _possiblyStuck || _countdown is not null)
            {
                return;
            }
            var now = _time.GetUtcNow();
            if (_checkInOutstanding && _unanswered >= MaxUnanswered && IsDue(now, settings))
            {
                _possiblyStuck = true;
                becameStuck = true;
            }
            else if (IsDue(now, settings))
            {
                started = _countdown = new CheckInCountdown(now + Countdown, settings.Message);
                _countdownTimer = _time.CreateTimer(_ => CountdownEnded(), null, Countdown, Timeout.InfiniteTimeSpan);
            }
        }

        if (becameStuck)
        {
            _possiblyStuckChanged(true);
        }
        if (started is not null)
        {
            _countdownChanged(started);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_lock)
        {
            _countdownTimer?.Dispose();
        }
    }

    /// <summary>The countdown ran out: the check-in is sent, if the turn still calls for it.</summary>
    private void CountdownEnded()
    {
        string? message = null;
        lock (_lock)
        {
            if (_countdown is not { } countdown || !StopCountdown())
            {
                return;
            }
            var settings = _settings();
            var now = _time.GetUtcNow();
            if (settings.Enabled && _turnActive && !_waitingOnUser && IsDue(now, settings))
            {
                message = countdown.Message;
                MarkSent(now);
            }
        }
        CountdownStopped(true);
        if (message is not null)
        {
            _sendCheckIn(message);
        }
    }

    /// <summary>
    /// Whether the turn calls for a check-in now: the wait after an unanswered one is over, or else it has run long or
    /// been quiet long enough.
    /// </summary>
    private bool IsDue(DateTimeOffset now, CheckInSettings settings)
    {
        var run = Minutes(settings.RunTimeMinutes);
        var quiet = Minutes(settings.QuietTimeMinutes);
        if (_checkInOutstanding)
        {
            return (quiet ?? run) is { } wait && now - _lastCheckIn >= wait;
        }
        return (run is { } r && now - _runBase >= r) || (quiet is { } q && now - _lastOutput >= q);
    }

    private void MarkSent(DateTimeOffset now)
    {
        _checkInOutstanding = true;
        _lastCheckIn = now;
        _unanswered++;
    }

    /// <summary>Ends the countdown under the lock. Returns whether there was one.</summary>
    private bool StopCountdown()
    {
        if (_countdown is null)
        {
            return false;
        }
        _countdown = null;
        _countdownTimer?.Dispose();
        _countdownTimer = null;
        return true;
    }

    /// <summary>Reports a countdown that stopped, outside the lock.</summary>
    private void CountdownStopped(bool stopped)
    {
        if (stopped)
        {
            _countdownChanged(null);
        }
    }

    private void SetStuck(bool stuck)
    {
        bool changed;
        lock (_lock)
        {
            changed = _possiblyStuck != stuck;
            _possiblyStuck = stuck;
        }
        if (changed)
        {
            _possiblyStuckChanged(stuck);
        }
    }

    private static TimeSpan? Minutes(int minutes) => minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
}
