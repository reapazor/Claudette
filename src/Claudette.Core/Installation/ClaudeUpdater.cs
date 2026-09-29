using Claudette.Core.Claude;
using Claudette.Core.Diffs;
using Claudette.Core.Processes;

namespace Claudette.Core.Installation;

/// <summary>What a check found (DESIGN.md §12, "Detecting an update").</summary>
/// <param name="InstalledVersion">From <c>claude --version</c>: what new tabs will run.</param>
/// <param name="Doctor">The <c>claude doctor</c> report, or null if it couldn't be read.</param>
/// <param name="AvailableVersion">A newer version the package manager has but hasn't installed (Homebrew, WinGet).</param>
/// <param name="Error">Why the check failed, when it did.</param>
public sealed record ClaudeUpdateCheck(
    Version? InstalledVersion,
    ClaudeDoctorReport? Doctor,
    ClaudeUpdatePlan Plan,
    Version? AvailableVersion,
    DateTimeOffset CheckedAt,
    string? Error = null);

/// <summary>How <b>Update now</b> went.</summary>
/// <param name="Version">The version installed afterwards, from <c>claude --version</c>.</param>
public sealed record ClaudeUpdateResult(bool Succeeded, Version? Version, string Message);

/// <summary>Checks for and applies Claude Code updates. An interface so the app's tests can fake it.</summary>
public interface IClaudeUpdater
{
    Task<ClaudeUpdateCheck> CheckAsync(CancellationToken cancellationToken = default);

    /// <param name="onOutput">Each line the command prints, for the progress dialog. Called on a background thread.</param>
    Task<ClaudeUpdateResult> UpdateAsync(ClaudeUpdatePlan plan, Action<string>? onOutput = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs <c>claude --version</c> and <c>claude doctor</c>, asks Homebrew or WinGet about newer versions, and runs the
/// update command for the install method (DESIGN.md §12). Everything goes through <see cref="IProcessLauncher"/>.
/// </summary>
/// <param name="workingDirectory">
/// Where the commands run. <c>claude doctor</c> reads the settings files of its working folder, so this is a neutral
/// folder of Claudette's rather than whatever folder Claudette was started from.
/// </param>
/// <param name="environmentOverrides">Applied on top of the cleaned environment (DESIGN.md §13) of every command.</param>
/// <param name="userEnvironment">
/// The user environment every command starts from, Homebrew and WinGet too (DESIGN.md §13). Null: Claudette's own.
/// </param>
public sealed class ClaudeUpdater(
    string claudePath,
    string workingDirectory,
    IProcessLauncher launcher,
    TimeProvider timeProvider,
    IFileProbe? probe = null,
    IReadOnlyDictionary<string, string?>? environmentOverrides = null,
    UserEnvironment? userEnvironment = null)
    : IClaudeUpdater
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DoctorTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(15);

    private readonly IFileProbe _probe = probe ?? FileProbe.Instance;

    public async Task<ClaudeUpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var installed = await GetInstalledVersionAsync(cancellationToken).ConfigureAwait(false);
        ClaudeDoctorReport? doctor = null;
        string? error = installed is null ? $"Couldn't read the version from '{claudePath} --version'." : null;
        try
        {
            var result = await RunAsync(new ProcessStartSpec(claudePath, ["doctor"]), DoctorTimeout, cancellationToken).ConfigureAwait(false);
            doctor = ClaudeDoctor.Parse(result.StandardOutput);
        }
        catch (Exception ex) when (IsRunFailure(ex))
        {
            error ??= $"Couldn't run 'claude doctor': {ex.Message}";
        }

        var plan = ClaudeUpdatePlan.For(doctor, claudePath, ResolveLink(claudePath), _probe);
        Version? available = null;
        if (plan.Check is { } check)
        {
            try
            {
                var result = await RunAsync(check, CheckTimeout, cancellationToken).ConfigureAwait(false);
                available = plan.Kind switch
                {
                    ClaudeUpdateKind.Homebrew => ClaudeUpdateOutput.ParseBrewOutdated(result.StandardOutput, check.Arguments[^1]),
                    ClaudeUpdateKind.WinGet => ClaudeUpdateOutput.ParseWinGetList(result.StandardOutput),
                    _ => null,
                };
            }
            catch (Exception ex) when (IsRunFailure(ex))
            {
                // Package manager checks are a bonus; the installed version is still worth reporting.
            }
        }
        if (available is not null && installed is not null && available <= installed)
        {
            available = null;
        }
        return new ClaudeUpdateCheck(installed, doctor, plan, available, timeProvider.GetUtcNow(), error);
    }

    public async Task<ClaudeUpdateResult> UpdateAsync(ClaudeUpdatePlan plan, Action<string>? onOutput = null, CancellationToken cancellationToken = default)
    {
        if (plan.Command is not { } command)
        {
            return new ClaudeUpdateResult(false, null, plan.Note ?? "Claudette can't run this update.");
        }
        ProcessResult result;
        try
        {
            result = await RunAsync(command, UpdateTimeout, cancellationToken, onOutput).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRunFailure(ex))
        {
            return new ClaudeUpdateResult(false, null, $"Couldn't run '{plan.CommandText}': {ex.Message}");
        }
        var version = await GetInstalledVersionAsync(cancellationToken).ConfigureAwait(false)
            ?? ClaudeUpdateOutput.ParseClaudeUpdate(result.StandardOutput);
        if (result.ExitCode != 0)
        {
            var detail = LastLines(result.StandardError) ?? LastLines(result.StandardOutput) ?? "";
            return new ClaudeUpdateResult(false, version, $"'{plan.CommandText}' failed (exit {result.ExitCode}). {detail}".Trim());
        }
        return new ClaudeUpdateResult(true, version, version is null ? "The update finished." : $"Claude Code {version} is installed.");
    }

    private async Task<Version?> GetInstalledVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunAsync(new ProcessStartSpec(claudePath, ["--version"]), VersionTimeout, cancellationToken).ConfigureAwait(false);
            return ClaudeLocator.TryParseVersion(result.StandardOutput, out var version) ? version : null;
        }
        catch (Exception ex) when (IsRunFailure(ex))
        {
            return null;
        }
    }

    private async Task<ProcessResult> RunAsync(ProcessStartSpec spec, TimeSpan timeout, CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        Directory.CreateDirectory(workingDirectory);
        var baseEnvironment = userEnvironment is null ? null : await userEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        var ready = spec with { WorkingDirectory = workingDirectory, Environment = ClaudeEnvironment.From(baseEnvironment, environmentOverrides) };
        return await ProcessRunner.RunAsync(launcher, ready, timeout, timeProvider, cancellationToken, onLine).ConfigureAwait(false);
    }

    private static bool IsRunFailure(Exception ex) =>
        ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException;

    /// <summary>The file a symlinked launcher points at, such as a Homebrew Caskroom path.</summary>
    private static string? ResolveLink(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? LastLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? null : string.Join(" ", lines.TakeLast(3));
    }
}
