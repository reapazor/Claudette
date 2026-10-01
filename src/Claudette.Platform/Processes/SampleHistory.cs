namespace Claudette.Platform.Processes;

/// <summary>What a platform tree reads about one process, before CPU % and first-seen time are worked out.</summary>
/// <param name="StartKey">
/// When the process started, in the platform's own units. With <see cref="Pid"/> it identifies the process, so a reused
/// PID is never mistaken for the process that had it before.
/// </param>
/// <param name="CpuTime">Total kernel plus user CPU time used so far.</param>
internal sealed record RawProcess(
    int Pid,
    int ParentPid,
    long StartKey,
    string Name,
    string? ExecutablePath,
    string? CommandLine,
    TimeSpan CpuTime,
    long MemoryBytes,
    DateTimeOffset? StartTime,
    bool IsRoot,
    bool IsDetached);

/// <summary>
/// Turns raw samples into <see cref="ProcessSnapshot"/>s: CPU % from the change in CPU time between two samples of the
/// same process, and the time each process was first seen (DESIGN.md §4, Process monitor).
/// </summary>
/// <param name="cpuDivisor">
/// What 100% means. <see cref="Environment.ProcessorCount"/> on Windows, where 100% is all cores (as in Task Manager);
/// 1 on macOS and Linux, where 100% is one core (as in Activity Monitor and <c>top</c>).
/// </param>
internal sealed class SampleHistory(TimeProvider time, int cpuDivisor)
{
    private readonly int _cpuDivisor = Math.Max(1, cpuDivisor);
    private Dictionary<(int Pid, long StartKey), Seen> _seen = [];

    /// <summary>The divisor for the current OS.</summary>
    public static int PlatformCpuDivisor => OperatingSystem.IsWindows() ? Environment.ProcessorCount : 1;

    /// <summary>Not thread-safe: callers serialize samples.</summary>
    /// <param name="scannedAt">
    /// When the processes were read (<see cref="TimeProvider.GetTimestamp"/>), if not just now: a scan shared with other
    /// trees may be a few seconds old, and CPU use is the CPU time read over the time between two scans.
    /// </param>
    public IReadOnlyList<ProcessSnapshot> Update(IReadOnlyList<RawProcess> processes, long? scannedAt = null)
    {
        var now = time.GetUtcNow();
        var timestamp = scannedAt ?? time.GetTimestamp();
        var next = new Dictionary<(int, long), Seen>(processes.Count);
        var snapshots = new List<ProcessSnapshot>(processes.Count);
        foreach (var process in processes)
        {
            var key = (process.Pid, process.StartKey);
            var firstSeen = now;
            double? cpu = null;
            if (_seen.TryGetValue(key, out var previous))
            {
                firstSeen = previous.FirstSeen;
                var wall = time.GetElapsedTime(previous.Timestamp, timestamp);
                cpu = wall > TimeSpan.Zero ? CpuPercent(process.CpuTime - previous.CpuTime, wall, _cpuDivisor) : previous.CpuPercent;
            }
            next[key] = new Seen(firstSeen, process.CpuTime, timestamp, cpu);
            snapshots.Add(new ProcessSnapshot
            {
                Pid = process.Pid,
                ParentPid = process.ParentPid,
                Name = process.Name,
                ExecutablePath = process.ExecutablePath,
                CommandLine = process.CommandLine,
                CpuPercent = cpu,
                MemoryBytes = process.MemoryBytes,
                StartTime = process.StartTime,
                IsRoot = process.IsRoot,
                IsDetached = process.IsDetached,
                FirstSeen = firstSeen,
            });
        }
        _seen = next;
        return snapshots;
    }

    /// <summary>CPU time used over a stretch of wall-clock time, as a percentage of <paramref name="divisor"/> cores.</summary>
    public static double CpuPercent(TimeSpan cpuUsed, TimeSpan wallElapsed, int divisor)
    {
        if (wallElapsed <= TimeSpan.Zero || cpuUsed <= TimeSpan.Zero)
        {
            return 0;
        }
        return cpuUsed.TotalSeconds / wallElapsed.TotalSeconds * 100 / Math.Max(1, divisor);
    }

    private readonly record struct Seen(DateTimeOffset FirstSeen, TimeSpan CpuTime, long Timestamp, double? CpuPercent);
}
