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

    /// <summary>Several trees' summaries added up, such as every tab's for the header.</summary>
    public static ProcessSummary Sum(IEnumerable<ProcessSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        var total = Empty;
        foreach (var summary in summaries)
        {
            total = new ProcessSummary(total.Count + summary.Count, total.CpuPercent + summary.CpuPercent, total.MemoryBytes + summary.MemoryBytes);
        }
        return total;
    }

    /// <summary>The CPU and memory without the count, for example <c>42% CPU · 1.1 GB</c>.</summary>
    public string UsageText =>
        $"{Math.Round(CpuPercent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}% CPU · {FormatMemory(MemoryBytes)}";

    public override string ToString() => $"{(Count == 1 ? "1 proc" : $"{Count} procs")} · {UsageText}";

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
