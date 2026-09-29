using System.Globalization;

namespace Claudette.Platform.Processes;

/// <summary>
/// The compact summary shown in the composer bar, for example <c>3 procs · 42% CPU · 1.1 GB</c>
/// (DESIGN.md §4, Process monitor).
/// </summary>
/// <param name="Count">Processes the tab started, not counting its own <c>claude</c> process.</param>
/// <param name="CpuPercent">CPU use of the whole tree, <c>claude</c> included.</param>
/// <param name="MemoryBytes">Memory of the whole tree, <c>claude</c> included.</param>
public sealed record ProcessSummary(int Count, double CpuPercent, long MemoryBytes)
{
    private const long Megabyte = 1024L * 1024;
    private const long Gigabyte = 1024L * Megabyte;

    public static ProcessSummary Empty { get; } = new(0, 0, 0);

    public static ProcessSummary From(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var count = 0;
        var cpu = 0.0;
        var memory = 0L;
        foreach (var snapshot in snapshots)
        {
            if (!snapshot.IsRoot)
            {
                count++;
            }
            cpu += snapshot.CpuPercent ?? 0;
            memory += snapshot.MemoryBytes;
        }
        return new ProcessSummary(count, cpu, memory);
    }

    public override string ToString()
    {
        var procs = Count == 1 ? "1 proc" : $"{Count} procs";
        var cpu = Math.Round(CpuPercent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        return $"{procs} · {cpu}% CPU · {FormatMemory(MemoryBytes)}";
    }

    /// <summary>Whole megabytes under 1 GB, otherwise gigabytes with one decimal.</summary>
    public static string FormatMemory(long bytes)
    {
        var megabytes = Math.Round((double)bytes / Megabyte, MidpointRounding.AwayFromZero);
        if (megabytes < 1024)
        {
            return megabytes.ToString("0", CultureInfo.InvariantCulture) + " MB";
        }
        return ((double)bytes / Gigabyte).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
    }
}
