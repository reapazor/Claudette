using System.Globalization;

namespace Claudette.Core;

/// <summary>How Claudette writes times, durations and names, the same wherever they show.</summary>
public static class Formats
{
    /// <summary>How long ago: "just now", "5 min ago", "2 h ago", "yesterday", "3 days ago".</summary>
    public static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes} min ago"),
        { TotalDays: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h ago"),
        { TotalDays: < 2 } => "yesterday",
        _ => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays} days ago"),
    };

    /// <summary>
    /// A running time that ticks each second, such as the working line's or a job's: "8s", "1m 05s", "1h 02m". Seconds
    /// show until an hour has gone.
    /// </summary>
    public static string Elapsed(TimeSpan span) => span switch
    {
        { TotalHours: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes:00}m"),
        { TotalMinutes: >= 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m {span.Seconds:00}s"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)span.TotalSeconds)}s"),
    };

    /// <summary>How long something has run or took, in round figures: "8s", "3m", "1h 02m", "2d 3h".</summary>
    public static string Duration(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)span.TotalSeconds)}s"),
        { TotalHours: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m"),
        { TotalDays: < 1 } => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes:00}m"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}d {span.Hours}h"),
    };

    /// <summary>A folder's own name, with or without a separator at its end; the whole path when it has none (a drive's root).</summary>
    public static string FolderName(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : path;

    /// <summary>"High" for "high".</summary>
    public static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
