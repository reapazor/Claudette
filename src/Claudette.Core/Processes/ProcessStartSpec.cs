namespace Claudette.Core.Processes;

/// <summary>A program to start, with its arguments, working folder and environment.</summary>
public sealed record ProcessStartSpec(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// The complete environment for the child process. When null, the child inherits this process's environment.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Put the process and everything it starts in a tracked tree (DESIGN.md §4, Process monitor).</summary>
    public bool TrackProcessTree { get; init; }
}
