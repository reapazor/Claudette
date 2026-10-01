using System.Globalization;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using System.Text.RegularExpressions;

namespace Claudette.Usage;

/// <summary>
/// Turns the three plan-usage sources into <see cref="UsageSnapshot"/>s (DESIGN.md §6, "Data source"). Tolerant of
/// changes: unknown fields and kinds are skipped, and nothing here throws on an unexpected shape.
/// </summary>
public static partial class UsageParser
{
    public const string SessionLabel = "Session";
    public const string WeeklyLabel = "Weekly";

    /// <summary>
    /// <c>/usage</c> shows times to the minute, and a reset can be printed a few seconds before it happens; a time
    /// this far in the past still counts as the current occurrence.
    /// </summary>
    private static readonly TimeSpan DisplayRounding = TimeSpan.FromMinutes(1);

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>
    /// The response to the undocumented <c>get_usage</c> control request. Reads <c>rate_limits.limits[]</c>, or the
    /// older <c>five_hour</c> / <c>seven_day</c> / <c>model_scoped</c> fields when that list is missing. Returns null
    /// when <c>rate_limits_available</c> is false or nothing usable is found.
    /// </summary>
    public static UsageSnapshot? FromGetUsage(JsonObject response, DateTimeOffset now)
    {
        try
        {
            if (response.GetBool("rate_limits_available") == false || response.GetObject("rate_limits") is not { } rateLimits)
            {
                return null;
            }
            var limits = FromLimitsList(rateLimits);
            if (limits.Count == 0)
            {
                limits = FromWindowFields(rateLimits);
            }
            return Snapshot(limits, now, UsageSource.GetUsage);
        }
        catch (Exception)
        {
            // A node of an unexpected type, duplicate keys, a number out of range: an unusable shape, not a crash.
            return null;
        }
    }

    /// <summary>
    /// The <c>rate_limit_info</c> of a <c>rate_limit_event</c> message. Gives the session and weekly readings only:
    /// <c>unifiedWindows</c> when present (undocumented), otherwise the documented top-level window.
    /// </summary>
    public static UsageSnapshot? FromRateLimitEvent(JsonObject rateLimitInfo, DateTimeOffset now)
    {
        try
        {
            var limits = new List<LimitReading>();
            if (rateLimitInfo.GetObject("unifiedWindows") is { } windows)
            {
                AddWindow(limits, windows.GetObject("five_hour"), LimitKind.Session, SessionLabel);
                AddWindow(limits, windows.GetObject("seven_day"), LimitKind.WeeklyAll, WeeklyLabel);
            }
            if (limits.Count == 0)
            {
                switch (rateLimitInfo.GetString("rateLimitType"))
                {
                    case "five_hour":
                        AddWindow(limits, rateLimitInfo, LimitKind.Session, SessionLabel);
                        break;
                    case "seven_day":
                        AddWindow(limits, rateLimitInfo, LimitKind.WeeklyAll, WeeklyLabel);
                        break;
                }
            }
            return Snapshot(limits, now, UsageSource.RateLimitEvent);
        }
        catch (Exception)
        {
            return null;
        }

        static void AddWindow(List<LimitReading> limits, JsonObject? window, LimitKind kind, string label)
        {
            if (window?.GetDouble("utilization") is { } utilization)
            {
                limits.Add(new LimitReading(kind, label, Percent(utilization * 100), window.GetTime("resetsAt"), null, false));
            }
        }
    }

    /// <summary>
    /// The text of the local <c>/usage</c> command: the last-resort source. Accepts both the one-line form
    /// (<c>Current week (Fable): 100% used · resets Sep 30, 6:59am (America/Toronto)</c>) and the terminal's
    /// block form, where the heading, the percentage and the reset time are on separate lines.
    /// </summary>
    public static UsageSnapshot? FromUsageCommand(string text, DateTimeOffset now)
    {
        try
        {
            var blocks = new List<UsageBlock>();
            UsageBlock? block = null;
            foreach (var rawLine in AnsiEscape().Replace(text, "").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    block = null;
                    continue;
                }
                var rest = line;
                if (UsageHeading().Match(line) is { Success: true } heading)
                {
                    block = new UsageBlock(heading.Groups["what"].Value, heading.Groups["scope"].Success ? heading.Groups["scope"].Value.Trim() : null);
                    blocks.Add(block);
                    rest = line[(heading.Index + heading.Length)..];
                }
                if (block is null)
                {
                    continue;
                }
                if (block.Percent is null && UsedPercent().Match(rest) is { Success: true } used
                    && double.TryParse(used.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    block.Percent = percent;
                    rest = rest[(used.Index + used.Length)..];
                }
                if (block.ResetText is null && ResetsClause().Match(rest) is { Success: true } resets)
                {
                    block.ResetText = resets.Groups["when"].Value;
                }
            }

            var limits = new List<LimitReading>();
            foreach (var b in blocks)
            {
                if (b.Percent is not { } value)
                {
                    continue;
                }
                var (kind, label) = b.What.Equals("session", StringComparison.OrdinalIgnoreCase)
                    ? (LimitKind.Session, SessionLabel)
                    : WeekScope(b.Scope);
                var resetsAt = b.ResetText is null ? null : ParseResetTime(b.ResetText, now);
                limits.Add(new LimitReading(kind, label, Percent(value), resetsAt, null, false));
            }
            return Snapshot(limits, now, UsageSource.UsageCommand);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// How long a lower reading than the current one is taken as stale. When Anthropic's usage endpoint fails,
    /// <c>get_usage</c> answers from Claude Code's cached reading without saying so, and Claude Code keeps that cache
    /// for an hour.
    /// </summary>
    public static readonly TimeSpan StaleReadingAge = TimeSpan.FromHours(1);

    /// <summary>
    /// Combines a new snapshot with the current one (DESIGN.md §6, "Sampling").
    /// <list type="bullet">
    /// <item>Within one window a limit's usage only rises. A reading lower than the current one for the same window, or
    /// one for an earlier window, is stale, and the current one stays. Once the current one is
    /// <see cref="StaleReadingAge"/> old, no cached reading can be older, so a lower one is believed: the limit really
    /// went down, as after a plan change.</item>
    /// <item>A <c>rate_limit_event</c> carries no model-specific limits, so it replaces only the session and weekly
    /// readings it has, and keeps the rest; that way the model meters don't disappear between polls. Any other source
    /// replaces the set of limits.</item>
    /// </list>
    /// Readings kept from <paramref name="previous"/> keep the time they were reported.
    /// </summary>
    public static UsageSnapshot Merge(UsageSnapshot? previous, UsageSnapshot update)
    {
        if (previous is null)
        {
            return update;
        }
        var limits = new List<LimitReading>();
        var keptAny = false;
        foreach (var reading in update.Limits)
        {
            var current = previous.Limits.FirstOrDefault(l => l.Kind == reading.Kind && l.Label.Equals(reading.Label, StringComparison.OrdinalIgnoreCase));
            if (current is not null && IsStale(reading, current, current.ReportedAt ?? previous.AsOf, update.AsOf))
            {
                limits.Add(Kept(current, previous.AsOf));
                keptAny = true;
            }
            else
            {
                limits.Add(reading);
            }
        }
        if (update.Source == UsageSource.RateLimitEvent)
        {
            var replaced = update.Limits.Select(l => l.Kind).ToHashSet();
            foreach (var other in previous.Limits.Where(l => l.Kind == LimitKind.WeeklyModel || !replaced.Contains(l.Kind)))
            {
                limits.Add(Kept(other, previous.AsOf));
                keptAny = true;
            }
        }
        return keptAny ? update with { Limits = limits } : update;
    }

    /// <summary>Whether <paramref name="update"/> is older than <paramref name="current"/>, which was reported at <paramref name="reportedAt"/>.</summary>
    private static bool IsStale(LimitReading update, LimitReading current, DateTimeOffset reportedAt, DateTimeOffset now)
    {
        if (now - reportedAt >= StaleReadingAge)
        {
            return false;
        }
        // Without both reset times the windows can't be told apart.
        if (update.ResetsAt is not { } updateResets || current.ResetsAt is not { } currentResets)
        {
            return false;
        }
        if (updateResets - currentResets > UsageAlerts.SameWindowTolerance)
        {
            // A later window.
            return false;
        }
        // An earlier window, or less of the same one.
        return currentResets - updateResets > UsageAlerts.SameWindowTolerance || update.Percent < current.Percent;
    }

    private static LimitReading Kept(LimitReading reading, DateTimeOffset asOf) => reading with { ReportedAt = reading.ReportedAt ?? asOf };

    private static List<LimitReading> FromLimitsList(JsonObject rateLimits)
    {
        var limits = new List<LimitReading>();
        foreach (var entry in rateLimits.GetArray("limits")?.OfType<JsonObject>() ?? [])
        {
            if (entry.GetDouble("percent") is not { } percent)
            {
                continue;
            }
            var (kind, label) = entry.GetString("kind") switch
            {
                "session" => (LimitKind.Session, SessionLabel),
                "weekly_all" => (LimitKind.WeeklyAll, WeeklyLabel),
                "weekly_scoped" => (LimitKind.WeeklyModel, entry.GetObject("scope")?.GetObject("model")?.GetString("display_name")),
                _ => (LimitKind.WeeklyModel, null),
            };
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }
            limits.Add(new LimitReading(kind, label, Percent(percent), entry.GetTime("resets_at"), entry.GetString("severity"), entry.GetBool("is_active") ?? false));
        }
        return limits;
    }

    private static List<LimitReading> FromWindowFields(JsonObject rateLimits)
    {
        var limits = new List<LimitReading>();
        Add(rateLimits.GetObject("five_hour"), LimitKind.Session, SessionLabel);
        Add(rateLimits.GetObject("seven_day"), LimitKind.WeeklyAll, WeeklyLabel);
        foreach (var model in rateLimits.GetArray("model_scoped")?.OfType<JsonObject>() ?? [])
        {
            Add(model, LimitKind.WeeklyModel, model.GetString("display_name"));
        }
        return limits;

        void Add(JsonObject? window, LimitKind kind, string? label)
        {
            if (window?.GetDouble("utilization") is { } utilization && !string.IsNullOrWhiteSpace(label))
            {
                limits.Add(new LimitReading(kind, label, Percent(utilization), window.GetTime("resets_at"), null, false));
            }
        }
    }

    /// <summary>Keeps the first reading of each limit, and returns null when there are none.</summary>
    private static UsageSnapshot? Snapshot(List<LimitReading> limits, DateTimeOffset now, UsageSource source)
    {
        var distinct = limits.DistinctBy(l => (l.Kind, l.Label.ToUpperInvariant())).ToArray();
        return distinct.Length == 0 ? null : new UsageSnapshot(distinct, now, source);
    }

    /// <summary>Rounded to hundredths, so <c>0.57 × 100</c> reads as 57 rather than 56.99999999999999.</summary>
    private static double Percent(double value) => Math.Round(Math.Max(0, value), 2);

    private static (LimitKind, string) WeekScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Equals("all models", StringComparison.OrdinalIgnoreCase) || scope.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return (LimitKind.WeeklyAll, WeeklyLabel);
        }
        // "Sonnet only" in some versions; get_usage calls the same limit "Sonnet".
        var label = scope.EndsWith(" only", StringComparison.OrdinalIgnoreCase) ? scope[..^" only".Length].Trim() : scope;
        return (LimitKind.WeeklyModel, label);
    }

    /// <summary>
    /// A reset time as <c>/usage</c> prints it: <c>Sep 28, 10:19pm (America/Toronto)</c>, <c>3am (Europe/Paris)</c>,
    /// <c>Sep 30</c> or <c>in 2h 14m</c>. The zone in parentheses is an IANA id; without a known one, the local zone
    /// is used. The year, or the day for a bare time, is the next occurrence after <paramref name="now"/>.
    /// </summary>
    internal static DateTimeOffset? ParseResetTime(string text, DateTimeOffset now)
    {
        try
        {
            return ReadResetTime(text, now);
        }
        catch (Exception)
        {
            // An out-of-range number or date. The reading is still good without its reset time.
            return null;
        }
    }

    private static DateTimeOffset? ReadResetTime(string text, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.Local;
        text = text.Trim().TrimEnd('.');
        if (TrailingZone().Match(text) is { Success: true } zoneMatch)
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(zoneMatch.Groups["zone"].Value.Trim(), out var found))
            {
                zone = found;
            }
            text = text[..zoneMatch.Index].Trim();
        }

        if (RelativeTime().Match(text) is { Success: true } relative)
        {
            var span = TimeSpan.Zero;
            foreach (Match part in DurationPart().Matches(relative.Groups["parts"].Value))
            {
                var amount = int.Parse(part.Groups["n"].Value, CultureInfo.InvariantCulture);
                span += char.ToLowerInvariant(part.Groups["unit"].Value[0]) switch
                {
                    'd' => TimeSpan.FromDays(amount),
                    'h' => TimeSpan.FromHours(amount),
                    _ => TimeSpan.FromMinutes(amount),
                };
            }
            return span > TimeSpan.Zero ? UsageJson.RoundToSecond(now + span) : null;
        }

        if (AbsoluteTime().Match(text) is not { Success: true } match)
        {
            return null;
        }
        var hasDate = match.Groups["month"].Success;
        var hasTime = match.Groups["hour"].Success;
        if (!hasDate && !hasTime)
        {
            return null;
        }

        var hour = 0;
        var minute = 0;
        if (hasTime)
        {
            hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
            minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
            if (match.Groups["ampm"].Success)
            {
                if (hour is < 1 or > 12)
                {
                    return null;
                }
                var pm = char.ToLowerInvariant(match.Groups["ampm"].Value[0]) == 'p';
                hour = (hour % 12) + (pm ? 12 : 0);
            }
            if (hour > 23 || minute > 59)
            {
                return null;
            }
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var earliest = now - DisplayRounding;
        if (!hasDate)
        {
            var today = At(zone, local.Year, local.Month, local.Day, hour, minute);
            var tomorrow = local.AddDays(1);
            return today is { } t && t < earliest ? At(zone, tomorrow.Year, tomorrow.Month, tomorrow.Day, hour, minute) : today;
        }

        var month = Array.IndexOf(Months, match.Groups["month"].Value[..3].ToLowerInvariant()) + 1;
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        if (month == 0)
        {
            return null;
        }
        if (match.Groups["year"].Success)
        {
            return At(zone, int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture), month, day, hour, minute);
        }
        for (var year = local.Year; year <= local.Year + 4; year++)
        {
            // Four years, so Feb 29 finds a leap year.
            if (At(zone, year, month, day, hour, minute) is { } candidate && candidate >= earliest)
            {
                return candidate;
            }
        }
        return null;
    }

    private static DateTimeOffset? At(TimeZoneInfo zone, int year, int month, int day, int hour, int minute)
    {
        if (year is < 1 or > 9998 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }
        var wallClock = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock)).ToUniversalTime();
    }

    private sealed class UsageBlock(string what, string? scope)
    {
        public string What { get; } = what;

        public string? Scope { get; } = scope;

        public double? Percent { get; set; }

        public string? ResetText { get; set; }
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"\bcurrent\s+(?<what>session|week)\b(?:\s*\((?<scope>[^)]*)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex UsageHeading();

    [GeneratedRegex(@"(?<percent>\d+(?:\.\d+)?)\s*%\s*used", RegexOptions.IgnoreCase)]
    private static partial Regex UsedPercent();

    [GeneratedRegex(@"\bresets?\s+(?<when>.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ResetsClause();

    [GeneratedRegex(@"\((?<zone>[^()]+)\)\s*$")]
    private static partial Regex TrailingZone();

    [GeneratedRegex(@"^in\s+(?<parts>(?:\d+\s*[a-z]+[\s,]*(?:and\s+)?)+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeTime();

    [GeneratedRegex(@"(?<n>\d+)\s*(?<unit>d|days?|h|hrs?|hours?|m|mins?|minutes?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPart();

    [GeneratedRegex(
        @"^(?:(?<month>[a-z]{3,9})\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(?<year>\d{4}))?)?[\s,]*(?:at\s+)?(?:(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>[ap]\.?m\.?)?)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteTime();
}
