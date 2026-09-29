using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>Why a tab stopped at a usage limit won't continue by itself (DESIGN.md §6, "Continuing after a limit resets").</summary>
public enum LimitWaitHold
{
    /// <summary>Nothing: it continues once the limit resets.</summary>
    None,

    /// <summary>Settings → Usage, or the tab's own settings, turned automatic continue off.</summary>
    TurnedOff,

    /// <summary>The limit resets more than a day away, as a weekly limit can.</summary>
    TooFar,

    /// <summary>The user chose <b>Don't continue</b>.</summary>
    Cancelled,

    /// <summary>
    /// Continuing ran straight into the limit again: it waits again at most <see cref="AutoContinueMonitor.MaxRepeats"/>
    /// times in a row, as Claude Code does.
    /// </summary>
    Repeated,

    /// <summary>The limit reset long before Claudette noticed: the computer slept, or Claudette wasn't running.</summary>
    Missed,
}

/// <summary>
/// A turn that Claude Code ended at a plan usage limit, and the limit's reset (DESIGN.md §6, "Continuing after a limit
/// resets"). Saved with the tab, so a restart keeps waiting.
/// </summary>
/// <param name="ResetsAt">When the limit resets, from the rejected <c>rate_limit_event</c>.</param>
/// <param name="LimitType">The event's <c>rateLimitType</c>, such as <c>five_hour</c>, or null.</param>
/// <param name="ContinueAt">When to continue: shortly after the reset, so the server has reset too.</param>
/// <param name="Hold">Why the tab won't continue by itself, or <see cref="LimitWaitHold.None"/> when it will.</param>
/// <param name="HasReset">The limit has reset, and the tab didn't continue by itself.</param>
public sealed record LimitWait(DateTimeOffset ResetsAt, string? LimitType, DateTimeOffset ContinueAt, LimitWaitHold Hold, bool HasReset = false)
{
    [JsonIgnore]
    public bool WillContinue => Hold == LimitWaitHold.None && !HasReset;

    /// <summary>"session limit", "weekly limit" or "Opus limit", as Claude Code names them in its error.</summary>
    [JsonIgnore]
    public string LimitName => LimitType switch
    {
        "five_hour" => "session limit",
        "seven_day" => "weekly limit",
        "seven_day_opus" => "Opus limit",
        "seven_day_sonnet" => "Sonnet limit",
        _ => "usage limit",
    };
}

/// <summary>
/// Continues a task that a plan usage limit stopped, once the limit resets (DESIGN.md §6, "Continuing after a limit
/// resets"). Claude Code does this itself only in interactive sessions, not in <c>-p</c> runs like Claudette's.
/// <list type="bullet">
/// <item>A turn stopped at the limit: it ended with an API error, and Claude Code sent a <c>rate_limit_event</c> with
/// <c>status: rejected</c> and a <c>resetsAt</c>, in either order. An <c>errorCode</c> (usage credits needed) or an error
/// other than a 429 isn't a limit that waiting fixes.</item>
/// <item>At the reset, plus <see cref="Grace"/>, it sends <see cref="Message"/>, as Claude Code does.</item>
/// <item>It holds instead when turned off, when the reset is more than <see cref="MaxWait"/> away, after <b>Don't
/// continue</b> (for that reset), after <see cref="MaxRepeats"/> continues in a row that hit the limit again, and when
/// the reset was more than <see cref="MissedAfter"/> before it noticed.</item>
/// <item>Any turn that starts ends the wait: the user sent something, or something else started one.</item>
/// </list>
/// </summary>
public sealed class AutoContinueMonitor : IDisposable
{
    /// <summary>What Claude Code itself sends when it continues after a limit resets.</summary>
    public const string Message = "Continue from where you left off.";

    public const int MaxRepeats = 2;
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(24);
    public static readonly TimeSpan MissedAfter = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly Func<bool> _enabled;
    private readonly Action<string> _sendContinue;
    private readonly Action _changed;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();
    private LimitWait? _wait;
    private (DateTimeOffset ResetsAt, string? LimitType)? _rejection;
    private bool _endedAtLimit;
    private bool _continued;
    private int _repeats;
    private DateTimeOffset? _cancelledReset;

    /// <param name="enabled">Whether the tab continues by itself: its override, else Settings → Usage.</param>
    /// <param name="sendContinue">Called (on a timer thread, or the caller's) with the message to send.</param>
    /// <param name="changed">Called when <see cref="Wait"/> changes.</param>
    public AutoContinueMonitor(TimeProvider time, Func<bool> enabled, Action<string> sendContinue, Action changed)
    {
        _time = time;
        _enabled = enabled;
        _sendContinue = sendContinue;
        _changed = changed;
        _timer = time.CreateTimer(_ => Tick(), null, TickInterval, TickInterval);
    }

    /// <summary>The tab's wait for a limit to reset, or null.</summary>
    public LimitWait? Wait
    {
        get
        {
            lock (_lock)
            {
                return _wait;
            }
        }
    }

    /// <summary>A turn started, whoever started it. A wait ends: the tab is doing something else now.</summary>
    public void TurnStarted() => Update(() =>
    {
        _rejection = null;
        _endedAtLimit = false;
        return SetWait(null);
    });

    /// <summary>The user sent a message: continues so far no longer count as repeats.</summary>
    public void UserSent() => Update(() =>
    {
        _continued = false;
        _repeats = 0;
        return false;
    });

    /// <summary>The <c>rate_limit_info</c> of a <c>rate_limit_event</c>.</summary>
    public void RateLimit(JsonObject info) => Update(() =>
    {
        if (Rejection(info) is not { } rejection)
        {
            return false;
        }
        _rejection = rejection;
        return _endedAtLimit && Arm();
    });

    /// <summary>A turn ended. Stopping it (<c>terminal_reason</c> <c>aborted_streaming</c> or <c>aborted_tools</c>) isn't the limit.</summary>
    public void TurnEnded(ResultMessage result) => Update(() =>
    {
        if (!EndedAtLimit(result))
        {
            _rejection = null;
            _endedAtLimit = false;
            if (!result.IsError)
            {
                // The task got going again.
                _continued = false;
                _repeats = 0;
            }
            return false;
        }
        _endedAtLimit = true;
        return _rejection is not null && Arm();
    });

    /// <summary>
    /// <b>Continue when it resets</b>: whatever held the wait, continue at the reset; or now, when it already has.
    /// </summary>
    public void ContinueAtReset()
    {
        var send = false;
        Update(() =>
        {
            if (_wait is not { } wait)
            {
                return false;
            }
            _cancelledReset = null;
            _repeats = 0;
            if (wait.HasReset)
            {
                send = true;
                return Continue();
            }
            return SetWait(wait with { Hold = LimitWaitHold.None });
        });
        if (send)
        {
            _sendContinue(Message);
        }
    }

    /// <summary><b>Don't continue</b>: this reset won't continue the task by itself, even if the limit is hit again.</summary>
    public void DontContinue() => Update(() =>
    {
        if (_wait is not { WillContinue: true } wait)
        {
            return false;
        }
        _cancelledReset = wait.ResetsAt;
        return SetWait(wait with { Hold = LimitWaitHold.Cancelled });
    });

    /// <summary>Closes a wait that won't continue by itself.</summary>
    public void Dismiss() => Update(() => _wait is { WillContinue: false } && SetWait(null));

    /// <summary>The setting may have changed: a wait that follows it (on, or turned off) takes the new value.</summary>
    public void SettingsChanged() => Update(() =>
        _wait is { HasReset: false, Hold: LimitWaitHold.None or LimitWaitHold.TurnedOff } wait
        && SetWait(wait with { Hold = _enabled() ? LimitWaitHold.None : LimitWaitHold.TurnedOff }));

    /// <summary>A wait saved with the tab, after a restart. It may have reset meanwhile.</summary>
    public void Restore(LimitWait wait)
    {
        Update(() => SetWait(wait));
        Tick();
    }

    /// <summary>Continues at the reset. Runs on the timer; public so tests can call it directly.</summary>
    public void Tick()
    {
        var send = false;
        Update(() =>
        {
            var now = _time.GetUtcNow();
            if (_wait is not { HasReset: false } wait || now < (wait.Hold == LimitWaitHold.None ? wait.ContinueAt : wait.ResetsAt))
            {
                return false;
            }
            if (wait.Hold != LimitWaitHold.None)
            {
                return SetWait(wait with { HasReset = true });
            }
            if (now - wait.ContinueAt > MissedAfter)
            {
                // The computer slept through it, or Claudette wasn't running: the user may not expect the task to go on.
                return SetWait(wait with { Hold = LimitWaitHold.Missed, HasReset = true });
            }
            send = true;
            return Continue();
        });
        if (send)
        {
            _sendContinue(Message);
        }
    }

    public void Dispose() => _timer.Dispose();

    /// <summary>
    /// A rejected <c>rate_limit_event</c> that waiting fixes: its reset time and limit. Null for one that asks for usage
    /// credits (<c>errorCode</c>), or without a reset time.
    /// </summary>
    public static (DateTimeOffset ResetsAt, string? LimitType)? Rejection(JsonObject info)
    {
        if (info.GetString("status") != "rejected" || info["errorCode"] is not null)
        {
            return null;
        }
        DateTimeOffset? resetsAt = info.GetDouble("resetsAt") is { } seconds
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))
            : DateTimeOffset.TryParse(info.GetString("resetsAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        return resetsAt is { } reset ? (reset, info.GetString("rateLimitType")) : null;
    }

    /// <summary>
    /// The turn ended on an API error that may be the limit: <c>api_error_status</c> 429, or none with a
    /// <c>terminal_reason</c> of <c>api_error</c> (or none). Not one that ran out of turns or budget, or was stopped. The
    /// rejected event says which limit.
    /// </summary>
    public static bool EndedAtLimit(ResultMessage result) =>
        result.IsError && result.Raw.GetDouble("api_error_status") switch
        {
            429 => true,
            null => result.TerminalReason is null or "api_error",
            _ => false,
        };

    private bool Arm()
    {
        var (resetsAt, limitType) = _rejection!.Value;
        _rejection = null;
        _endedAtLimit = false;
        _repeats = _continued ? _repeats + 1 : 0;
        _continued = false;
        var now = _time.GetUtcNow();
        var hold = !_enabled() ? LimitWaitHold.TurnedOff
            : _cancelledReset == resetsAt ? LimitWaitHold.Cancelled
            : _repeats > MaxRepeats ? LimitWaitHold.Repeated
            : resetsAt - now > MaxWait ? LimitWaitHold.TooFar
            : LimitWaitHold.None;
        // A reset already past (a clock that runs ahead, say) continues shortly rather than straight away.
        var continueAt = (resetsAt > now ? resetsAt : now) + Grace;
        return SetWait(new LimitWait(resetsAt, limitType, continueAt, hold));
    }

    private bool Continue()
    {
        _continued = true;
        return SetWait(null);
    }

    private bool SetWait(LimitWait? wait)
    {
        if (wait == _wait)
        {
            return false;
        }
        _wait = wait;
        return true;
    }

    /// <summary>Runs <paramref name="change"/> under the lock, and reports a changed wait outside it.</summary>
    private void Update(Func<bool> change)
    {
        bool changed;
        lock (_lock)
        {
            changed = change();
        }
        if (changed)
        {
            _changed();
        }
    }
}
