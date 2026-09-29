using System.ComponentModel;
using Claudette.Core.Processes;

namespace Claudette.Core.Perforce;

/// <summary>The outcome of <c>p4 login</c>.</summary>
/// <param name="NeedsUserLogin">The server wants single sign-on or a second factor: the user has to log in themselves.</param>
/// <param name="Message">Perforce's own words, such as "Password invalid.".</param>
public sealed record PerforceLoginResult(bool Succeeded, bool NeedsUserLogin, string Message);

/// <summary>
/// Runs the <c>p4</c> commands of Perforce ticket handling (DESIGN.md §18) through <see cref="IProcessLauncher"/>,
/// always in the tab's folder. Never throws for a missing <c>p4</c>, a timeout or an error: those come back as results.
/// </summary>
/// <param name="environment">
/// The user environment <c>p4</c> runs with, and whose <c>PATH</c> it's found on (DESIGN.md §13): the same as Claude's
/// own <c>p4</c> commands get, so <c>P4CONFIG</c>, <c>P4PORT</c> and the like set in a shell profile agree. Null:
/// Claudette's own.
/// </param>
public sealed class PerforceClient(IProcessLauncher launcher, TimeProvider timeProvider, string executable = "p4", UserEnvironment? environment = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Detection holds up the tab's start, so it gives up sooner on a server that doesn't answer.</summary>
    public static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(10);

    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    public string Executable => executable;

    /// <summary>
    /// <c>p4 -ztag info</c> in the tab's folder, with <c>p4 set</c> for the P4PORT and P4LOGINSSO it resolves there.
    /// Null when the folder isn't in a Perforce workspace, or <c>p4</c> isn't installed or can't reach the server.
    /// </summary>
    public async Task<PerforceWorkspace?> DetectAsync(PerforceTarget target, CancellationToken cancellationToken = default)
    {
        var info = RunAsync(target.Folder, [.. target.GlobalOptions(), "-ztag", "info"], cancellationToken, timeout: DetectTimeout);
        var port = target.Port is { Length: > 0 } overridden ? Task.FromResult<string?>(overridden) : GetSettingAsync(target.Folder, "P4PORT", cancellationToken);
        var sso = GetSettingAsync(target.Folder, "P4LOGINSSO", cancellationToken);
        var result = await info.ConfigureAwait(false);
        if (result is not { ExitCode: 0 })
        {
            return null;
        }
        return PerforceWorkspace.FromInfo(result.StandardOutput, target.Folder, await port.ConfigureAwait(false), await sso.ConfigureAwait(false));
    }

    /// <summary><c>p4 -ztag login -s</c>: whether the ticket is valid, and for how long.</summary>
    public async Task<TicketStatus> GetTicketStatusAsync(PerforceTarget target, PerforceWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(target.Folder, [.. ConnectionOptions(target, workspace), "-ztag", "login", "-s"], cancellationToken).ConfigureAwait(false);
        return result is null
            ? new TicketStatus(TicketState.Unknown, null, $"Couldn't run {executable}.")
            : TicketStatus.Parse(result);
    }

    /// <summary>
    /// <c>p4 -p &lt;server&gt; -u &lt;user&gt; login [-a]</c>, with the password written to its standard input. The
    /// password never goes on the command line, where other processes and the process monitor could see it.
    /// </summary>
    /// <param name="allHosts">Ask for a ticket valid on every host (<c>login -a</c>).</param>
    public async Task<PerforceLoginResult> LoginAsync(PerforceTarget target, PerforceWorkspace workspace, string password, bool allHosts, CancellationToken cancellationToken = default)
    {
        List<string> args = [.. ConnectionOptions(target, workspace), "login"];
        if (allHosts)
        {
            args.Add("-a");
        }
        var result = await RunAsync(target.Folder, args, cancellationToken, password).ConfigureAwait(false);
        if (result is null)
        {
            return new PerforceLoginResult(false, false, $"Couldn't run {executable}.");
        }
        // p4 prompts "Enter password:" on standard output, without a line break.
        var text = $"{result.StandardOutput}\n{result.StandardError}".Replace("Enter password:", "", StringComparison.OrdinalIgnoreCase);
        var message = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        if (result.ExitCode == 0)
        {
            return new PerforceLoginResult(true, false, message);
        }
        return new PerforceLoginResult(false, PerforceErrors.NeedsUserLogin(text), message.Length > 0 ? message : $"p4 login failed (exit {result.ExitCode}).");
    }

    /// <summary>
    /// <c>-p &lt;server&gt; -u &lt;user&gt;</c>: the workspace's own server and user, or the per-folder overrides. With no
    /// P4PORT set anywhere, <c>-p</c> is left out so <c>p4</c> uses its default, as Claude's own commands do.
    /// </summary>
    public static IReadOnlyList<string> ConnectionOptions(PerforceTarget target, PerforceWorkspace workspace)
    {
        List<string> options = [];
        if ((target.Port ?? workspace.Port) is { Length: > 0 } port)
        {
            options.AddRange(["-p", port]);
        }
        options.AddRange(["-u", target.User ?? workspace.User]);
        return options;
    }

    /// <summary><c>p4 set -q NAME</c> in <paramref name="folder"/>: the value Perforce resolves there, or null.</summary>
    public async Task<string?> GetSettingAsync(string folder, string name, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(folder, ["set", "-q", name], cancellationToken).ConfigureAwait(false);
        return result is { ExitCode: 0 } ? PerforceWorkspace.ParseSetting(result.StandardOutput, name) : null;
    }

    private async Task<ProcessResult?> RunAsync(string folder, IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? inputLine = null, TimeSpan? timeout = null)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return null;
            }
            var spec = new ProcessStartSpec(executable, arguments) { WorkingDirectory = folder };
            if (environment is not null)
            {
                spec = await environment.ApplyAsync(spec, cancellationToken).ConfigureAwait(false);
            }
            return await ProcessRunner.RunAsync(launcher, spec, timeout ?? Timeout, timeProvider, cancellationToken, inputLine: inputLine).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
