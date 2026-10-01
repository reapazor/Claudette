using System.Globalization;

namespace Claudette.Usage;

/// <summary>A usage percentage at a moment, for the trendline.</summary>
public readonly record struct UsagePoint(DateTimeOffset Time, double Percent);

/// <summary>How a meter is colored (DESIGN.md §6, "Header meters").</summary>
public enum UsageLevel
{
    Normal,
    Warning,
    Critical,
}

/// <summary>Where the session is heading at the current rate (DESIGN.md §6, "Burn trendline").</summary>
public sealed record BurnProjection
{
    public double CurrentPercent { get; init; }

    /// <summary>Percent per hour over the recent window, or null when there isn't enough data yet.</summary>
    public double? RatePerHour { get; init; }

    /// <summary>No increase over the recent window: nothing is running, so there's no rate to project.</summary>
    public bool IsIdle { get; init; }

    public DateTimeOffset? LimitAt { get; init; }

    public TimeSpan? TimeToLimit { get; init; }

    public bool HitsLimitBeforeReset { get; init; }

    /// <summary>How long before the reset the limit is hit, when <see cref="HitsLimitBeforeReset"/>.</summary>
    public TimeSpan? MarginBeforeReset { get; init; }

    /// <summary>The expected percentage when the session resets, at most 100.</summary>
    public double? PercentAtReset { get; init; }

    /// <summary>The line under the session meter.</summary>
    public string Describe()
    {
        if (CurrentPercent >= 100)
        {
            return "You've hit the limit.";
        }
        if (RatePerHour is null)
        {
            return "Not enough data yet.";
        }
        if (IsIdle)
        {
            return "No recent usage.";
        }
        if (HitsLimitBeforeReset && TimeToLimit is { } toLimit && MarginBeforeReset is { } margin)
        {
            return $"At this rate you'll hit the limit in {BurnRate.FormatDuration(toLimit)}, {BurnRate.FormatDuration(margin)} before it resets.";
        }
        if (PercentAtReset is { } atReset)
        {
            return string.Create(CultureInfo.InvariantCulture, $"On track: about {Math.Round(atReset):0}% used when the session resets.");
        }
        return TimeToLimit is { } time ? $"At this rate you'll hit the limit in {BurnRate.FormatDuration(time)}." : "Not enough data yet.";
    }
}

/// <summary>
/// The moment the projection reaches <paramref name="Level"/> (DESIGN.md §6, "Detailed header"), and how long that is
/// before the window resets.
/// </summary>
public sealed record LevelCrossing(double Level, DateTimeOffset At, TimeSpan BeforeReset)
{
    /// <summary>"Hits 90% at 14:05, 25m before it resets.", or "Hits the limit at …" for 100%.</summary>
    public string Describe()
    {
        var what = Level >= 100 ? "the limit" : $"{Level:0}%";
        return $"Hits {what} at {At.ToLocalTime():t}, {BurnRate.FormatDuration(BeforeReset)} before it resets.";
    }
}

/// <summary>Session burn rate, projection and the wording around them (DESIGN.md §6, "Burn trendline").</summary>
public static class BurnRate
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan SessionLength = TimeSpan.FromHours(5);

    /// <summary>A rate needs points at least this far apart.</summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(2);

    /// <summary>At or below this many percent per hour, the session counts as idle.</summary>
    public const double IdleRatePerHour = 0.1;

    /// <summary>A fall of more than this many points between two samples means the window reset in between.</summary>
    private const double ResetDrop = 5;

    public static BurnProjection Project(IReadOnlyList<UsagePoint> history, double currentPercent, DateTimeOffset? resetsAt, DateTimeOffset now) =>
        Project(history, currentPercent, resetsAt, now, DefaultWindow);

    /// <summary>
    /// The session trendline's points from stored samples, oldest first. Within one window usage only rises, as live
    /// readings are merged (<see cref="UsageParser.Merge"/>), so each point is the highest reading of its window so far.
    /// Another machine's samples (DESIGN.md §6, "Sharing across machines") can lag behind this one's, from a cached
    /// <c>get_usage</c> answer: taken as they are, a lower one in the middle would look like a reset and throw the rate
    /// off. Windows are told apart by their reset times; a reading for an earlier window than the current one is
    /// stale and left out, and one without a reset time is taken as it is.
    /// </summary>
    public static List<UsagePoint> SessionHistory(IEnumerable<UsageSample> samples)
    {
        var points = new List<UsagePoint>();
        DateTimeOffset? windowResets = null;
        var highest = 0.0;
        foreach (var sample in samples.Where(s => s.SessionPercent is { } p && double.IsFinite(p)).OrderBy(s => s.Timestamp))
        {
            var percent = sample.SessionPercent!.Value;
            if (sample.SessionResetsAt is not { } resets)
            {
                points.Add(new UsagePoint(sample.Timestamp, percent));
                continue;
            }
            if (windowResets is { } current && current - resets > UsageAlerts.SameWindowTolerance)
            {
                continue;
            }
            if (windowResets is { } same && (resets - same).Duration() <= UsageAlerts.SameWindowTolerance)
            {
                highest = Math.Max(highest, percent);
            }
            else
            {
                windowResets = resets;
                highest = percent;
            }
            points.Add(new UsagePoint(sample.Timestamp, highest));
        }
        return points;
    }

    /// <summary>
    /// Fits a least-squares line to the points in the current 5-hour window that are at most <paramref name="window"/>
    /// old, plus <paramref name="currentPercent"/> at <paramref name="now"/>, and projects it forward.
    /// </summary>
    public static BurnProjection Project(IReadOnlyList<UsagePoint> history, double currentPercent, DateTimeOffset? resetsAt, DateTimeOffset now, TimeSpan window)
    {
        var from = now - window;
        if (resetsAt is { } reset && reset - SessionLength > from)
        {
            from = reset - SessionLength;
        }
        var points = history
            .Where(p => p.Time >= from && p.Time < now && double.IsFinite(p.Percent))
            .OrderBy(p => p.Time)
            .Append(new UsagePoint(now, currentPercent))
            .ToList();
        // Samples from before a reset that the window boundary didn't catch (for example with no reset time).
        for (var i = points.Count - 1; i > 0; i--)
        {
            if (points[i].Percent < points[i - 1].Percent - ResetDrop)
            {
                points.RemoveRange(0, i);
                break;
            }
        }

        var projection = new BurnProjection { CurrentPercent = currentPercent };
        var untilReset = resetsAt is { } r && r > now ? r - now : (TimeSpan?)null;
        if (currentPercent >= 100)
        {
            projection = projection with
            {
                LimitAt = now,
                TimeToLimit = TimeSpan.Zero,
                HitsLimitBeforeReset = untilReset is not null,
                MarginBeforeReset = untilReset,
                PercentAtReset = untilReset is null ? null : currentPercent,
            };
        }

        if (points.Count < 2 || points[^1].Time - points[0].Time < MinimumSpan || Slope(points) is not { } rate)
        {
            return projection;
        }
        projection = projection with { RatePerHour = rate };
        if (currentPercent >= 100)
        {
            return projection;
        }

        if (rate <= IdleRatePerHour || currentPercent <= points[0].Percent)
        {
            return projection with { IsIdle = true, PercentAtReset = untilReset is null ? null : currentPercent };
        }
        return Forward(projection, rate, now, untilReset);
    }

    /// <summary>
    /// The week's projection (DESIGN.md §6, "Detailed header"): the average pace since <paramref name="since"/>, when
    /// the window started, carried on to the reset. A week has nights and days off in it, so the last half hour's rate
    /// says little about the rest of it.
    /// </summary>
    public static BurnProjection ProjectAverage(double currentPercent, DateTimeOffset since, DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        var projection = new BurnProjection { CurrentPercent = currentPercent };
        var elapsed = now - since;
        var untilReset = resetsAt is { } r && r > now ? r - now : (TimeSpan?)null;
        if (elapsed < MinimumSpan || !double.IsFinite(currentPercent))
        {
            return projection;
        }
        var rate = Math.Max(0, currentPercent) / elapsed.TotalHours;
        projection = projection with { RatePerHour = rate };
        if (currentPercent >= 100)
        {
            return projection with
            {
                LimitAt = now,
                TimeToLimit = TimeSpan.Zero,
                HitsLimitBeforeReset = untilReset is not null,
                MarginBeforeReset = untilReset,
                PercentAtReset = untilReset is null ? null : currentPercent,
            };
        }
        return rate <= 0
            ? projection with { IsIdle = true, PercentAtReset = untilReset is null ? null : currentPercent }
            : Forward(projection, rate, now, untilReset);
    }

    /// <summary>Carries <paramref name="rate"/> on from now: when the limit is hit, and where usage is at the reset.</summary>
    private static BurnProjection Forward(BurnProjection projection, double rate, DateTimeOffset now, TimeSpan? untilReset)
    {
        var currentPercent = projection.CurrentPercent;
        var toLimit = TimeSpan.FromHours((100 - currentPercent) / rate);
        projection = projection with { LimitAt = now + toLimit, TimeToLimit = toLimit };
        if (untilReset is not { } remaining)
        {
            return projection;
        }
        var atReset = currentPercent + (rate * remaining.TotalHours);
        return toLimit < remaining
            ? projection with { HitsLimitBeforeReset = true, MarginBeforeReset = remaining - toLimit, PercentAtReset = 100 }
            : projection with { PercentAtReset = Math.Min(100, atReset) };
    }

    /// <summary>
    /// Where the projection first crosses a level before the window resets (DESIGN.md §6, "Detailed header"): the
    /// critical threshold, or the limit once usage is past the threshold. Null when it crosses neither before the reset,
    /// or when there's no rate to go on.
    /// </summary>
    public static LevelCrossing? FirstCrossing(BurnProjection projection, DateTimeOffset? resetsAt, DateTimeOffset now, double criticalPercent)
    {
        if (projection.IsIdle || projection.RatePerHour is not { } rate || rate <= IdleRatePerHour || resetsAt is not { } reset || reset <= now)
        {
            return null;
        }
        var current = projection.CurrentPercent;
        var level = current < criticalPercent && criticalPercent < 100 ? criticalPercent : 100;
        if (current >= level)
        {
            return null;
        }
        var at = now + TimeSpan.FromHours((level - current) / rate);
        return at < reset ? new LevelCrossing(level, at, reset - at) : null;
    }

    public static UsageLevel Level(double percent, double warn = 75, double critical = 90) =>
        percent >= critical ? UsageLevel.Critical
        : percent >= warn ? UsageLevel.Warning
        : UsageLevel.Normal;

    /// <summary>For "resets in 2h 14m". Over a day it reads like <c>3d 4h</c>.</summary>
    public static string FormatCountdown(TimeSpan remaining) => FormatDuration(remaining);

    /// <summary><c>1h 05m</c>, <c>12m</c> or <c>&lt;1m</c>; whole minutes, rounded down.</summary>
    public static string FormatDuration(TimeSpan span)
    {
        var seconds = Math.Round(span.TotalSeconds);
        if (seconds < 60)
        {
            return "<1m";
        }
        var minutes = (long)(seconds / 60);
        var hours = minutes / 60;
        if (hours >= 24)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{hours / 24}d {hours % 24}h");
        }
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}h {minutes % 60:00}m")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}m");
    }

    /// <summary>Least-squares slope in percent per hour, or null if the points don't spread over time.</summary>
    private static double? Slope(IReadOnlyList<UsagePoint> points)
    {
        var origin = points[0].Time;
        var meanX = points.Average(p => (p.Time - origin).TotalHours);
        var meanY = points.Average(p => p.Percent);
        double covariance = 0, variance = 0;
        foreach (var p in points)
        {
            var dx = (p.Time - origin).TotalHours - meanX;
            covariance += dx * (p.Percent - meanY);
            variance += dx * dx;
        }
        return variance > 0 ? covariance / variance : null;
    }
}
