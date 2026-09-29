using Claudette.Core.Claude;
using Claudette.Core.Processes;

namespace Claudette.Core.ProjectTools;

/// <summary>
/// The environment project actions run with: the one place it's built, so it can move to the login shell's
/// environment on macOS and Linux along with the other processes Claudette starts for the user.
/// </summary>
/// <remarks>
/// Like git and p4, a job gets Claudette's own environment, less the variables Claude Code sets for its child
/// processes (<see cref="ClaudeEnvironment.SessionVariables"/>): a custom action that runs <c>claude</c> then behaves as
/// it would from a terminal, not as a child of the session Claudette may have been started from.
/// </remarks>
public static class ProjectToolEnvironment
{
    /// <summary>The spec with its environment filled in, unless it already has one.</summary>
    public static ProcessStartSpec Apply(ProcessStartSpec spec) =>
        spec.Environment is null ? spec with { Environment = Create() } : spec;

    public static IReadOnlyDictionary<string, string> Create() => ClaudeEnvironment.Create();

    /// <summary>The user's shell, for custom actions on macOS and Linux: <c>$SHELL</c>, or null for <c>/bin/sh</c>.</summary>
    public static string? UserShell() => Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell ? shell : null;
}
