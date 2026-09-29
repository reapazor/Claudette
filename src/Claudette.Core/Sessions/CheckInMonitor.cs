using Claudette.Core.Settings;

namespace Claudette.Core.Sessions;

/// <summary>
/// Decides when to send an "Everything OK?" check-in during a long turn (DESIGN.md §5, "Check-ins on long turns").
/// <list type="bullet">
/// <item>Run time: the turn has run longer than <see cref="CheckInSettings.RunTimeMinutes"/>.</item>
/// <item>Quiet time: no output for <see cref="CheckInSettings.QuietTimeMinutes"/>.</item>
/// <item>Paused while a permission prompt waits on the user.</item>
/// <item>Output after a check-in counts as the reply and restarts the timers. After two check-ins in a row without a
/// reply, it stops and reports the turn as possibly stuck.</item>
/// </list>
/// </summary>
public sealed class CheckInMonitor : IDisposable
{
    public const int MaxUnanswered = 2;
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Func<CheckInSettings> _settings;
    private readonly Action<string> _sendCheckIn;
    private readonly Action<bool> _possiblyStuckChanged;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();
    private bool _turnActive;
    private bool _waitingOnUser;
    private bool _possiblyStuck;
    private DateTimeOffset _runBase;
    private DateTimeOffset _lastOutput;
    private DateTimeOffset _waitStart;
    private DateTimeOffset? _lastCheckIn;
    private bool _checkInOutstanding;
    private int _unanswered;

    /// <param name="sendCheckIn">Called (on a timer thread) with the message to send.</param>
    /// <param name="possiblyStuckChanged">Called when the "possibly stuck" state changes.</param>
    public CheckInMonitor(TimeProvider time, Func<CheckInSettings> settings, Action<string> sendCheckIn, Action<bool> possiblyStuckChanged)
    {
        _time = time;
        _settings = settings;
        _sendCheckIn = sendCheckIn;
        _possiblyStuckChanged = possiblyStuckChanged;
        _timer = time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
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
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            _turnActive = true;
            _runBase = now;
            _lastOutput = now;
            _lastCheckIn = null;
            _checkInOutstanding = false;
            _unanswered = 0;
        }
        SetStuck(false);
    }

    /// <summary>Anything Claude produced: text, a tool call, a tool result.</summary>
    public void OutputSeen()
    {
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
        }
        SetStuck(false);
    }

    public void TurnEnded()
    {
        lock (_lock)
        {
            _turnActive = false;
            _checkInOutstanding = false;
        }
        SetStuck(false);
    }

    /// <summary>A permission prompt is waiting on the user (or no longer is). Check-ins pause meanwhile.</summary>
    public void SetWaitingOnUser(bool waiting)
    {
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
            }
            else
            {
                // Time spent waiting on the user counts neither as Claude being quiet nor as run time.
                _lastOutput = now;
                _runBase += now - _waitStart;
            }
        }
    }

    /// <summary>Evaluates the triggers. Runs on the timer; public so tests can call it directly.</summary>
    public void Tick()
    {
        string? message = null;
        var becameStuck = false;
        lock (_lock)
        {
            var settings = _settings();
            if (!settings.Enabled || !_turnActive || _waitingOnUser || _possiblyStuck)
            {
                return;
            }
            var now = _time.GetUtcNow();
            var run = Minutes(settings.RunTimeMinutes);
            var quiet = Minutes(settings.QuietTimeMinutes);

            if (_checkInOutstanding)
            {
                var wait = quiet ?? run;
                if (wait is null || now - _lastCheckIn < wait)
                {
                    return;
                }
                if (_unanswered >= MaxUnanswered)
                {
                    _possiblyStuck = true;
                    becameStuck = true;
                }
                else
                {
                    message = settings.Message;
                }
            }
            else if ((run is { } r && now - _runBase >= r) || (quiet is { } q && now - _lastOutput >= q))
            {
                message = settings.Message;
            }

            if (message is not null)
            {
                _checkInOutstanding = true;
                _lastCheckIn = now;
                _unanswered++;
            }
        }

        if (becameStuck)
        {
            _possiblyStuckChanged(true);
        }
        if (message is not null)
        {
            _sendCheckIn(message);
        }
    }

    public void Dispose() => _timer.Dispose();

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
