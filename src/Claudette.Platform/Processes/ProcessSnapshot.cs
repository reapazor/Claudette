namespace Claudette.Platform.Processes;

/// <summary>One process in a tab's tree at one sample (DESIGN.md §4, Process monitor).</summary>
public sealed record ProcessSnapshot
{
    public required int Pid { get; init; }

    /// <summary>The parent's PID. On Windows this can name a parent that has already exited.</summary>
    public required int ParentPid { get; init; }

    /// <summary>The executable's file name, for example <c>node.exe</c> on Windows or <c>node</c> elsewhere.</summary>
    public required string Name { get; init; }

    public string? ExecutablePath { get; init; }

    /// <summary>The full command line. Null unless command lines were requested, or when it can't be read.</summary>
    public string? CommandLine { get; init; }

    /// <summary>
    /// CPU use since the previous sample, following the platform's convention: on Windows 100% means all cores (as in
    /// Task Manager); on macOS and Linux 100% means one core (as in Activity Monitor and <c>top</c>). Null on the first
    /// sample that includes the process.
    /// </summary>
    public double? CpuPercent { get; init; }

    /// <summary>Working set (Windows) or resident size (macOS, Linux), in bytes.</summary>
    public long MemoryBytes { get; init; }

    public DateTimeOffset? StartTime { get; init; }

    /// <summary>The tab's <c>claude</c> process.</summary>
    public bool IsRoot { get; init; }

    /// <summary>
    /// No longer descends from the root through live parents: its parent exited, or it daemonized and was re-parented.
    /// It's still listed because it came from this tree.
    /// </summary>
    public bool IsDetached { get; init; }

    /// <summary>
    /// When a sample first included the process, from the injected clock. Used to link a process to the Bash tool call
    /// that was running when it appeared. Sampling is periodic, so <see cref="StartTime"/> is more precise when known.
    /// </summary>
    public DateTimeOffset FirstSeen { get; init; }
}
