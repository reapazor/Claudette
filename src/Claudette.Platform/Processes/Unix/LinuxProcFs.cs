using System.Globalization;
using System.Text;

namespace Claudette.Platform.Processes.Unix;

/// <summary>The fields read from <c>/proc/&lt;pid&gt;/stat</c>. CPU and start times are in clock ticks.</summary>
internal readonly record struct LinuxStat(int Pid, string Comm, char State, int ParentPid, long UserTicks, long SystemTicks, long StartTicks)
    : IScannedProcess
{
    /// <summary>Start time in clock ticks since boot.</summary>
    public long StartKey => StartTicks;

    /// <summary>Exited, waiting for its parent to collect it.</summary>
    public bool IsZombie => State is 'Z' or 'X' or 'x';
}

/// <summary>Parsers for the <c>/proc</c> files the Linux process tree reads. Pure, so they're tested from strings.</summary>
internal static class LinuxProcFs
{
    /// <summary>
    /// Parses <c>/proc/&lt;pid&gt;/stat</c>. The command name is in parentheses and can itself hold spaces and
    /// parentheses, so the other fields are read after the last <c>)</c>.
    /// </summary>
    public static LinuxStat? ParseStat(string text)
    {
        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.LastIndexOf(')');
        if (open < 0 || close < open
            || !int.TryParse(text.AsSpan(0, open).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            return null;
        }
        // After the name: state (field 3), ppid (4), ... utime (14), stime (15), ... starttime (22).
        var fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length < 20 || fields[0].Length != 1
            || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ppid)
            || !long.TryParse(fields[11], NumberStyles.None, CultureInfo.InvariantCulture, out var utime)
            || !long.TryParse(fields[12], NumberStyles.None, CultureInfo.InvariantCulture, out var stime)
            || !long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var starttime))
        {
            return null;
        }
        return new LinuxStat(pid, text[(open + 1)..close], fields[0][0], ppid, utime, stime, starttime);
    }

    /// <summary>Resident size from the <c>VmRSS</c> line of <c>/proc/&lt;pid&gt;/status</c>, in bytes. Null when absent (kernel threads).</summary>
    public static long? ParseVmRssBytes(string status)
    {
        foreach (var line in status.Split('\n'))
        {
            if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
            {
                continue;
            }
            var parts = line["VmRSS:".Length..].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }
            var unit = parts.Length > 1 ? parts[1] : "kB";
            return unit.ToUpperInvariant() switch
            {
                "B" => value,
                "MB" => value * 1024 * 1024,
                "GB" => value * 1024 * 1024 * 1024,
                _ => value * 1024,
            };
        }
        return null;
    }

    /// <summary>
    /// <c>/proc/&lt;pid&gt;/cmdline</c>: arguments separated by NULs, joined here with spaces. Null when empty (kernel
    /// threads, zombies).
    /// </summary>
    public static string? ParseCmdline(ReadOnlySpan<byte> bytes)
    {
        while (bytes.Length > 0 && bytes[^1] == 0)
        {
            bytes = bytes[..^1];
        }
        if (bytes.IsEmpty)
        {
            return null;
        }
        return Encoding.UTF8.GetString(bytes).Replace('\0', ' ');
    }

    /// <summary>The <c>btime</c> line of <c>/proc/stat</c>: when the system booted, in seconds since the Unix epoch.</summary>
    public static long? ParseBootTime(string procStat)
    {
        foreach (var line in procStat.Split('\n'))
        {
            if (line.StartsWith("btime ", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("btime ".Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return seconds;
            }
        }
        return null;
    }
}
