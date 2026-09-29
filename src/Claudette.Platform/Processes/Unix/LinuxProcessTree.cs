using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Claudette.Platform.Processes.Unix;

/// <summary>
/// A process tree on Linux, read from <c>/proc</c> (DESIGN.md §4, Process monitor): parent PIDs and times from
/// <c>stat</c>, resident size from <c>status</c>, the command line from <c>cmdline</c> and the executable from the
/// <c>exe</c> link. CPU % follows <c>top</c>: 100% is one core.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxProcessTree(int rootPid, TimeProvider time, ILogger logger)
    : UnixProcessTree<LinuxStat>(rootPid, time, logger)
{
    private const int ScClkTck = 2;
    private const long DefaultClockTicks = 100;

    private static readonly Lazy<long> s_clockTicks = new(ReadClockTicks);
    private static readonly Lazy<DateTimeOffset?> s_bootTime = new(ReadBootTime);

    protected override int SigStop => 19;

    protected override int SigCont => 18;

    protected override Dictionary<int, LinuxStat>? ScanAll()
    {
        var all = new Dictionary<int, LinuxStat>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                    && ReadStat(pid) is { IsZombie: false } stat)
                {
                    all[pid] = stat;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Couldn't list /proc.");
            return null;
        }
        return all;
    }

    protected override RawProcess? Describe(LinuxStat stat, bool includeCommandLines, bool isRoot, bool isDetached)
    {
        var pid = stat.Pid;
        var executable = ReadLink($"/proc/{pid}/exe");
        var memory = ReadText($"/proc/{pid}/status") is { } status ? LinuxProcFs.ParseVmRssBytes(status) ?? 0 : 0;
        var commandLine = includeCommandLines && ReadBytes($"/proc/{pid}/cmdline") is { } bytes ? LinuxProcFs.ParseCmdline(bytes) : null;
        var ticks = (double)s_clockTicks.Value;
        var name = stat.Comm.Length > 0 ? stat.Comm : Path.GetFileName(executable) ?? $"PID {pid}";
        return new RawProcess(
            pid,
            stat.ParentPid,
            stat.StartKey,
            name,
            executable,
            commandLine,
            TimeSpan.FromSeconds((stat.UserTicks + stat.SystemTicks) / ticks),
            memory,
            s_bootTime.Value?.AddSeconds(stat.StartTicks / ticks),
            isRoot,
            isDetached);
    }

    protected override bool IsAlive(int pid, long startKey) =>
        ReadStat(pid) is { IsZombie: false } stat && stat.StartKey == startKey;

    private static LinuxStat? ReadStat(int pid) =>
        ReadText($"/proc/{pid}/stat") is { } text ? LinuxProcFs.ParseStat(text) : null;

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The process exited, or isn't ours to read.
            return null;
        }
    }

    private static byte[]? ReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>readlink(2). Reading another user's <c>exe</c> link fails with EACCES; that gives null.</summary>
    private static string? ReadLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long ReadClockTicks()
    {
        try
        {
            var ticks = (long)LibC.SysConf(ScClkTck);
            return ticks > 0 ? ticks : DefaultClockTicks;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return DefaultClockTicks;
        }
    }

    private static DateTimeOffset? ReadBootTime() =>
        ReadText("/proc/stat") is { } text && LinuxProcFs.ParseBootTime(text) is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
