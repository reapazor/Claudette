using Claudette.Core.Claude;
using Claudette.Core.Processes;

namespace Claudette.Core.ProjectTools;

/// <summary>
/// The environment project actions run with (DESIGN.md §18, "Project tools"): the user's, with the login shell's
/// merged in on macOS and Linux (DESIGN.md §13, "Login shell environment"), as git, p4 and <c>claude</c> get it, so a
/// build finds <c>dotnet</c>, the editor or a Homebrew tool on the same <c>PATH</c> as a terminal would.
/// </summary>
/// <remarks>
/// Claude Code's variables for its child processes are removed (<see cref="ClaudeEnvironment.SessionVariables"/>): a
/// custom action that runs <c>claude</c> then behaves as it would from a terminal, not as a child of the session
/// Claudette may have been started from.
/// </remarks>
public static class ProjectToolEnvironment
{
    /// <summary>
    /// The spec with its environment filled in, unless it already has one. With the login shell's environment, a bare
    /// program name is found on its <c>PATH</c> too, since .NET would look on Claudette's own.
    /// </summary>
    /// <param name="userEnvironment">
    /// The user's environment with the login shell's (<see cref="UserEnvironment.Current"/>), or null for Claudette's own.
    /// </param>
    public static ProcessStartSpec Apply(ProcessStartSpec spec, IReadOnlyDictionary<string, string>? userEnvironment = null) =>
        spec.Environment is not null ? spec
        : userEnvironment is null ? spec with { Environment = Create() }
        : UserEnvironment.Apply(spec, Create(userEnvironment));

    public static IReadOnlyDictionary<string, string> Create(IReadOnlyDictionary<string, string>? userEnvironment = null) =>
        ClaudeEnvironment.From(userEnvironment);

    /// <summary>The user's shell, for custom actions on macOS and Linux: <c>$SHELL</c>, or null for <c>/bin/sh</c>.</summary>
    public static string? UserShell() => Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell ? shell : null;
}
