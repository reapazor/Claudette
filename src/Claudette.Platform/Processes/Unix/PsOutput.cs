using System.Globalization;

namespace Claudette.Platform.Processes.Unix;

/// <summary>One line of <c>ps -axo pid=,ppid=,rss=,time=,lstart=,args=</c>.</summary>
/// <param name="RssKilobytes">Resident size in KB.</param>
/// <param name="CpuTime">Cumulative CPU time.</param>
internal sealed record PsEntry(int Pid, int ParentPid, long RssKilobytes, TimeSpan CpuTime, DateTimeOffset? StartTime, string Args)
    : IScannedProcess
{
    /// <summary>Start time in whole seconds: <c>lstart</c> has no finer resolution.</summary>
    public long StartKey => StartTime?.ToUnixTimeSeconds() ?? 0;

    /// <summary>The first word of the command line. Paths with spaces in them are cut short; <c>ps</c> can't tell.</summary>
    public string Program
    {
        get
        {
            var end = Args.IndexOf(' ', StringComparison.Ordinal);
            return end < 0 ? Args : Args[..end];
        }
    }
}

/// <summary>Parses <c>ps</c> output for the macOS process tree. Pure, so it's tested with canned output.</summary>
internal static class PsOutput
{
    /// <summary>The arguments for <c>ps</c>. <c>ww</c> keeps long command lines whole.</summary>
    public static readonly IReadOnlyList<string> Arguments = ["-axww", "-o", "pid=,ppid=,rss=,time=,lstart=,args="];

    /// <summary>Parses every line. Lines that don't fit the format are skipped.</summary>
    public static List<PsEntry> Parse(string output)
    {
        var entries = new List<PsEntry>();
        foreach (var rawLine in output.Split('\n'))
        {
            if (ParseLine(rawLine.TrimEnd('\r')) is { } entry)
            {
                entries.Add(entry);
            }
        }
        return entries;
    }

    /// <summary>
    /// <c>pid ppid rss time lstart args</c>, where <c>lstart</c> is five words (<c>Mon Sep 28 10:15:30 2026</c>) and
    /// <c>args</c> is the rest of the line.
    /// </summary>
    public static PsEntry? ParseLine(string line)
    {
        const int FixedWords = 9;
        var words = new string[FixedWords];
        var position = 0;
        for (var i = 0; i < FixedWords; i++)
        {
            while (position < line.Length && char.IsWhiteSpace(line[position]))
            {
                position++;
            }
            var start = position;
            while (position < line.Length && !char.IsWhiteSpace(line[position]))
            {
                position++;
            }
            if (start == position)
            {
                return null;
            }
            words[i] = line[start..position];
        }
        if (!int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid)
            || !long.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out var rss)
            || ParseCpuTime(words[3]) is not { } cpu)
        {
            return null;
        }
        var startTime = ParseStartTime(words[5], words[6], words[7], words[8]);
        return new PsEntry(pid, ppid, rss, cpu, startTime, line[position..].Trim());
    }

    /// <summary>
    /// CPU time as <c>ps</c> prints it: <c>M:SS.cc</c> on macOS (minutes can pass 59), <c>[D-]HH:MM:SS</c> in procps.
    /// </summary>
    public static TimeSpan? ParseCpuTime(string text)
    {
        var days = 0;
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            if (!int.TryParse(text.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out days))
            {
                return null;
            }
            text = text[(dash + 1)..];
        }
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3
            || !double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return null;
        }
        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
        {
            return null;
        }
        return TimeSpan.FromDays(days) + TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The time zone <c>ps</c> runs in, so <c>lstart</c> is in UTC: in local time, the hour that repeats when the clocks
    /// go back would be ambiguous, and a child started in it could seem to start before its parent.
    /// </summary>
    public const string TimeZone = "UTC0";

    /// <summary><c>lstart</c> without its weekday, in UTC (<see cref="TimeZone"/>): <c>Sep 28 10:15:30 2026</c>.</summary>
    public static DateTimeOffset? ParseStartTime(string month, string day, string time, string year) =>
        DateTime.TryParseExact(
            $"{month} {day} {time} {year}",
            "MMM d HH:mm:ss yyyy",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var utc)
            ? new DateTimeOffset(utc, TimeSpan.Zero)
            : null;
}
