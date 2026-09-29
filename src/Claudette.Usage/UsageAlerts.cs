using System.Globalization;

namespace Claudette.Usage;

public enum UsageAlertKind
{
    ThresholdCrossed,
    ProjectedToHitLimit,
    LimitReset,
}

/// <summary>Something worth an OS notification (DESIGN.md §6, "Alerts"; §10).</summary>
public sealed record UsageAlert(UsageAlertKind Kind, string Title, string Message);

/// <summary>
/// Decides when usage is worth an alert (DESIGN.md §6, "Alerts"). Feed it every snapshot; it remembers what it has
/// already said.
/// <list type="bullet">
/// <item>The session crossing the warning and then the critical threshold, upward, once each per session window.</item>
/// <item>The projection first saying the limit will be hit before the reset, once per session window.</item>
/// <item>Any limit resetting: its reset time moves forward and its percentage drops.</item>
/// </list>
/// A session window is identified by its reset time. The first session reading after a restart seeds the state
/// without alerting, so levels that were already crossed before the restart stay quiet.
/// </summary>
public sealed class UsageAlerts
{
    /// <summary>
    /// Sources report the same reset a little differently (<c>/usage</c> shows minutes only), so reset times this
    /// close are the same window. A real reset moves it by hours.
    /// </summary>
    public static readonly TimeSpan SameWindowTolerance = TimeSpan.FromMinutes(10);

    private readonly Lock _lock = new();
    private readonly Dictionary<(LimitKind, string), LimitReading> _last = [];
    private bool _sessionSeeded;
    private DateTimeOffset? _sessionWindow;
    private bool _warnFired;
    private bool _criticalFired;
    private bool _projectionFired;

    public IReadOnlyList<UsageAlert> Observe(UsageSnapshot snapshot, BurnProjection? sessionProjection, double warn, double critical)
    {
        var alerts = new List<UsageAlert>();
        lock (_lock)
        {
            foreach (var reading in snapshot.Limits)
            {
                var key = (reading.Kind, reading.Label.ToUpperInvariant());
                if (_last.TryGetValue(key, out var previous) && IsNewWindow(previous.ResetsAt, reading.ResetsAt) && reading.Percent < previous.Percent)
                {
                    alerts.Add(ResetAlert(reading));
                }
                _last[key] = reading;
            }

            if (snapshot.Session is not { } session)
            {
                return alerts;
            }
            if (!_sessionSeeded)
            {
                _sessionSeeded = true;
                _sessionWindow = session.ResetsAt;
                _warnFired = session.Percent >= warn;
                _criticalFired = session.Percent >= critical;
            }
            else
            {
                if (IsNewWindow(_sessionWindow, session.ResetsAt))
                {
                    _warnFired = _criticalFired = _projectionFired = false;
                }
                if (_sessionWindow is null || IsNewWindow(_sessionWindow, session.ResetsAt))
                {
                    _sessionWindow = session.ResetsAt;
                }

                if (session.Percent >= critical && !_criticalFired)
                {
                    // Jumping straight past both thresholds is one alert, not two.
                    _criticalFired = _warnFired = true;
                    alerts.Add(ThresholdAlert(session, critical, snapshot.AsOf));
                }
                else if (session.Percent >= warn && !_warnFired)
                {
                    _warnFired = true;
                    alerts.Add(ThresholdAlert(session, warn, snapshot.AsOf));
                }
            }

            if (sessionProjection is { HitsLimitBeforeReset: true } && session.Percent < 100 && !_projectionFired)
            {
                _projectionFired = true;
                alerts.Add(new UsageAlert(UsageAlertKind.ProjectedToHitLimit, "On pace to hit the session limit", sessionProjection.Describe()));
            }
        }
        return alerts;
    }

    private static bool IsNewWindow(DateTimeOffset? previous, DateTimeOffset? current) =>
        previous is { } before && current is { } after && after - before > SameWindowTolerance;

    private static UsageAlert ThresholdAlert(LimitReading session, double threshold, DateTimeOffset now)
    {
        var title = string.Create(CultureInfo.InvariantCulture, $"Session usage passed {threshold:0.#}%");
        var used = string.Create(CultureInfo.InvariantCulture, $"You've used {Math.Floor(session.Percent):0}% of your session limit.");
        var message = session.ResetsAt is { } resetsAt && resetsAt > now
            ? $"{used} It resets in {BurnRate.FormatCountdown(resetsAt - now)}."
            : used;
        return new UsageAlert(UsageAlertKind.ThresholdCrossed, title, message);
    }

    private static UsageAlert ResetAlert(LimitReading reading)
    {
        var name = reading.Kind switch
        {
            LimitKind.Session => "Session",
            LimitKind.WeeklyAll => "Weekly",
            _ => $"{reading.Label} weekly",
        };
        return new UsageAlert(
            UsageAlertKind.LimitReset,
            $"{name} limit reset",
            string.Create(CultureInfo.InvariantCulture, $"{name} usage is now {Math.Round(reading.Percent):0}%."));
    }
}
