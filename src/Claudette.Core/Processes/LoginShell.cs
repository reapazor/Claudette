namespace Claudette.Core.Processes;

/// <summary>What became of the login shell's environment this run (DESIGN.md §13, "Login shell environment").</summary>
public enum LoginShellOutcome
{
    /// <summary>Read, and merged into the environment of the user's processes.</summary>
    Used,

    /// <summary>Windows: apps get the user's full environment already.</summary>
    NotNeededOnWindows,

    /// <summary>Claudette was started from a terminal, so it has the shell's environment already.</summary>
    StartedFromTerminal,

    /// <summary>The user's shell isn't one Claudette knows how to ask, such as nushell or PowerShell.</summary>
    UnsupportedShell,

    /// <summary>The shell didn't finish in time; its rc files may wait for something.</summary>
    TimedOut,

    /// <summary>The shell couldn't be started, or printed no environment.</summary>
    Failed,
}

/// <summary>What reading the login shell found.</summary>
/// <param name="Shell">The shell that was run, or would have been.</param>
/// <param name="Duration">How long the shell took, when it ran.</param>
/// <param name="Environment">The shell's variables, when <paramref name="Outcome"/> is <see cref="LoginShellOutcome.Used"/>.</param>
/// <param name="Detail">
/// Why it wasn't used, as words that follow the shell's name ("didn't finish within 10 s"). Never holds a variable's
/// value: those can be secrets.
/// </param>
public sealed record LoginShellResult(
    LoginShellOutcome Outcome,
    string Shell,
    TimeSpan? Duration = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? Detail = null);

/// <summary>
/// Reads the environment the user's login shell sets up: the <c>PATH</c> and variables from <c>~/.zprofile</c>,
/// <c>~/.bashrc</c> and the like, which an app started from the Dock or a desktop launcher doesn't get (DESIGN.md §13).
/// Implemented in Claudette.Platform.
/// </summary>
public interface ILoginShell
{
    /// <summary>The shell it runs: <c>$SHELL</c>, or the OS's default.</summary>
    string Shell { get; }

    /// <summary>
    /// Why this run doesn't need it (<see cref="LoginShellOutcome.NotNeededOnWindows"/> or
    /// <see cref="LoginShellOutcome.StartedFromTerminal"/>), or null when it does.
    /// </summary>
    LoginShellOutcome? NotNeeded { get; }

    /// <summary>Runs the shell and reads its environment. Never throws for a failure or a timeout: those are in the result.</summary>
    Task<LoginShellResult> ReadAsync(CancellationToken cancellationToken = default);
}
