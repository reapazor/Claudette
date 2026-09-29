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

    /// <summary>
    /// Start it with this process's standard streams instead of redirected ones, for a program that outlives Claudette,
    /// such as Claudette starting itself from a copy of its build (DESIGN.md §9). Its output channels are empty.
    /// </summary>
    public bool Detached { get; init; }

    /// <summary>
    /// The arguments as one command line, passed as they are instead of <see cref="Arguments"/>. Only for
    /// <c>cmd.exe</c> on Windows, which doesn't read its command line by the usual quoting rules, so the arguments of a
    /// <c>.bat</c> file have to be quoted for it by hand (<see cref="ProjectTools.CommandLines"/>). Null uses
    /// <see cref="Arguments"/>, quoted by .NET.
    /// </summary>
    public string? CommandLine { get; init; }
}
