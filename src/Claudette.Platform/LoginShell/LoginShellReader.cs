using System.ComponentModel;
using System.Globalization;
using Claudette.Core.Claude;
using Claudette.Core.Processes;

namespace Claudette.Platform.LoginShell;

/// <summary>What reading the login shell needs to know about Claudette's own process. Tests make their own.</summary>
/// <param name="Environment">Claudette's own environment: the shell starts with it, and <c>$SHELL</c> and <c>TERM</c> come from it.</param>
/// <param name="HasControllingTerminal">Whether Claudette has a controlling terminal. Only asked on macOS and Linux, without <c>TERM</c>.</param>
/// <param name="HomeDirectory">Where the shell starts, as a new terminal would.</param>
public sealed record LoginShellHost(
    bool IsWindows,
    bool IsMacOS,
    IReadOnlyDictionary<string, string> Environment,
    Func<bool> HasControllingTerminal,
    string? HomeDirectory)
{
    /// <summary>This process on this machine.</summary>
    public static LoginShellHost Current() => new(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsMacOS(),
        UserEnvironment.Snapshot(),
        () => (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && ControllingTerminal.Exists(),
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));
}

/// <summary>
/// Reads the environment of the user's login shell on macOS and Linux, the way VS Code does (DESIGN.md §13, "Login shell
/// environment"): <c>$SHELL -i -l -c</c> with a command that prints the environment between random markers, through
/// <see cref="IProcessLauncher"/>, with a timeout.
/// </summary>
public sealed class LoginShellReader : ILoginShell
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Set to 1 for the shell, so an rc file can skip slow or interactive setup, like VS Code's <c>VSCODE_RESOLVING_ENVIRONMENT</c>.</summary>
    public const string ResolvingVariable = "CLAUDETTE_RESOLVING_ENVIRONMENT";

    private readonly IProcessLauncher _launcher;
    private readonly TimeProvider _time;
    private readonly LoginShellHost _host;
    private readonly TimeSpan _timeout;

    public LoginShellReader(IProcessLauncher launcher, TimeProvider timeProvider, LoginShellHost host, TimeSpan? timeout = null)
    {
        _launcher = launcher;
        _time = timeProvider;
        _host = host;
        _timeout = timeout ?? DefaultTimeout;
        Shell = ChooseShell(host);
        NotNeeded = WhyNotNeeded(host);
    }

    /// <summary>The reader for this process. Pass Claudette's plain launcher: the shell isn't a tab's process.</summary>
    public static LoginShellReader CreateForCurrentOS(IProcessLauncher launcher, TimeProvider timeProvider) =>
        new(launcher, timeProvider, LoginShellHost.Current());

    public string Shell { get; }

    public LoginShellOutcome? NotNeeded { get; }

    /// <summary>Makes each run's marker. Tests fix it.</summary>
    internal Func<string> NewMarker { get; init; } = () => $"_CLAUDETTE_ENV_{Guid.NewGuid():N}_";

    /// <summary>
    /// Why this run doesn't need the login shell, or null when it does.
    /// <list type="bullet">
    /// <item>Windows: apps get the user's full environment.</item>
    /// <item>Started from a terminal (<see cref="StartedFromTerminal"/>): Claudette has a shell's environment already.</item>
    /// </list>
    /// </summary>
    public static LoginShellOutcome? WhyNotNeeded(LoginShellHost host)
    {
        if (host.IsWindows)
        {
            return LoginShellOutcome.NotNeededOnWindows;
        }
        return StartedFromTerminal(host.Environment, host.HasControllingTerminal) ? LoginShellOutcome.StartedFromTerminal : null;
    }

    /// <summary>
    /// <c>TERM</c> is set, or Claudette has a controlling terminal. An app started from the Dock, Finder, <c>open</c>
    /// or a desktop launcher has neither; one started from a terminal (<c>dotnet run</c> included) has both, and its
    /// environment came from the terminal's shell. Either one is enough:
    /// <list type="bullet">
    /// <item><c>TERM</c> without a terminal: the terminal has closed, or a desktop session started with <c>startx</c>
    /// from a console left it there. Either way the environment came from a login shell.</item>
    /// <item>A terminal without <c>TERM</c>: started from a terminal with <c>TERM</c> removed. An interactive shell run
    /// then would take that terminal over (bash opens <c>/dev/tty</c> for job control), so it's never run.</item>
    /// </list>
    /// </summary>
    public static bool StartedFromTerminal(IReadOnlyDictionary<string, string> environment, Func<bool> hasControllingTerminal) =>
        (environment.TryGetValue("TERM", out var term) && !string.IsNullOrWhiteSpace(term)) || hasControllingTerminal();

    /// <summary><c>$SHELL</c>, or the OS's default shell: zsh on macOS, bash elsewhere.</summary>
    public static string ChooseShell(LoginShellHost host) =>
        host.Environment.TryGetValue("SHELL", out var shell) && !string.IsNullOrWhiteSpace(shell)
            ? shell.Trim()
            : host.IsMacOS ? "/bin/zsh" : "/bin/bash";

    public async Task<LoginShellResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (NotNeeded is { } reason)
        {
            return new LoginShellResult(reason, Shell);
        }
        var marker = NewMarker();
        if (LoginShellOutput.Arguments(Shell, marker) is not { } arguments)
        {
            return new LoginShellResult(LoginShellOutcome.UnsupportedShell, Shell,
                Detail: $"isn't a shell Claudette can read (it reads {LoginShellOutput.SupportedNames})");
        }
        var spec = new ProcessStartSpec(Shell, arguments)
        {
            // Claudette's own environment, without the variables of a Claude Code session it may have been started from.
            Environment = ClaudeEnvironment.From(_host.Environment, new Dictionary<string, string?> { [ResolvingVariable] = "1" }),
            WorkingDirectory = _host.HomeDirectory is { Length: > 0 } home && Directory.Exists(home) ? home : null,
        };
        var started = _time.GetTimestamp();
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(_launcher, spec, _timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new LoginShellResult(LoginShellOutcome.TimedOut, Shell, _time.GetElapsedTime(started),
                Detail: $"didn't finish within {_timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s");
        }
        catch (Win32Exception ex)
        {
            // Only the OS's reason: the exception's own message names the home folder.
            return new LoginShellResult(LoginShellOutcome.Failed, Shell, Detail: $"couldn't be started ({new Win32Exception(ex.NativeErrorCode).Message})");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new LoginShellResult(LoginShellOutcome.Failed, Shell, Detail: "couldn't be started");
        }
        var elapsed = _time.GetElapsedTime(started);
        // The output is never logged or shown: the values can be secrets.
        return LoginShellOutput.Parse(result.StandardOutput, marker) is { } environment
            ? new LoginShellResult(LoginShellOutcome.Used, Shell, elapsed, environment)
            : new LoginShellResult(LoginShellOutcome.Failed, Shell, elapsed,
                Detail: result.ExitCode == 0 ? "printed no environment" : $"exited with code {result.ExitCode} without printing its environment");
    }
}
