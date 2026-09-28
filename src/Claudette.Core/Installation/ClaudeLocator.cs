using System.Text.RegularExpressions;
using Claudette.Core.Claude;
using Claudette.Core.Processes;

namespace Claudette.Core.Installation;

/// <summary>A usable Claude Code installation.</summary>
public sealed record ClaudeInstall(string Path, Version Version);

public enum ClaudeInstallProblem
{
    NotFound,
    /// <summary>Only an npm <c>claude.cmd</c> launcher was found, which can't be started without a shell.</summary>
    UnsupportedLauncher,
    VersionUnreadable,
    TooOld,
}

public sealed record ClaudeLocateResult(ClaudeInstall? Install, ClaudeInstallProblem? Problem, string? Detail, string? FoundPath = null)
{
    public bool IsUsable => Install is not null && Problem is null;
}

/// <summary>Finds the <c>claude</c> executable and checks its version (DESIGN.md §2, "Dependency").</summary>
public sealed partial class ClaudeLocator(IProcessLauncher launcher, TimeProvider timeProvider)
{
    /// <summary>The oldest Claude Code version Claudette supports (DESIGN.md §12, §16). Kept in step with compat/surface.yaml.</summary>
    public static readonly Version MinimumVersion = new(2, 1, 284);

    /// <summary>
    /// The newest Claude Code version Claudette has been checked against (DESIGN.md §16, "Tested versions"). Newer
    /// versions are allowed. Kept in step with compat/surface.yaml.
    /// </summary>
    public static readonly Version LastTestedVersion = new(2, 1, 284);

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);

    public async Task<ClaudeLocateResult> LocateAsync(string? configuredPath, CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? FindOnSystem() : configuredPath;
        if (path is null)
        {
            return OperatingSystem.IsWindows() && FindInPath("claude.cmd") is { } cmd
                ? new ClaudeLocateResult(null, ClaudeInstallProblem.UnsupportedLauncher,
                    "Claude Code was installed with npm, which Claudette can't start directly yet. Install the native build, or set the path to claude.exe in Settings.", cmd)
                : new ClaudeLocateResult(null, ClaudeInstallProblem.NotFound, "Claude Code wasn't found.");
        }
        if (!File.Exists(path))
        {
            return new ClaudeLocateResult(null, ClaudeInstallProblem.NotFound, $"'{path}' doesn't exist.", path);
        }

        ProcessResult result;
        try
        {
            var spec = new ProcessStartSpec(path, ["--version"]) { Environment = ClaudeEnvironment.Create() };
            result = await ProcessRunner.RunAsync(launcher, spec, VersionTimeout, timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ClaudeLocateResult(null, ClaudeInstallProblem.VersionUnreadable, $"Running '{path} --version' failed: {ex.Message}", path);
        }

        if (!TryParseVersion(result.StandardOutput, out var version))
        {
            return new ClaudeLocateResult(null, ClaudeInstallProblem.VersionUnreadable,
                $"Couldn't read the version from '{result.StandardOutput.Trim()}'.", path);
        }
        var install = new ClaudeInstall(path, version);
        return version < MinimumVersion
            ? new ClaudeLocateResult(install, ClaudeInstallProblem.TooOld,
                $"Claude Code {version} is installed. Claudette needs {MinimumVersion} or later.", path)
            : new ClaudeLocateResult(install, null, null, path);
    }

    /// <summary>Reads a version like <c>2.1.284 (Claude Code)</c>.</summary>
    public static bool TryParseVersion(string output, out Version version)
    {
        var match = VersionPattern().Match(output);
        if (match.Success && Version.TryParse(match.Value, out var parsed))
        {
            version = parsed;
            return true;
        }
        version = new Version();
        return false;
    }

    /// <summary>
    /// Looks on <c>PATH</c>, then in the standard install locations. GUI apps on macOS don't inherit the shell's
    /// <c>PATH</c>, so the known locations matter there.
    /// </summary>
    private static string? FindOnSystem()
    {
        var name = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        if (FindInPath(name) is { } onPath)
        {
            return onPath;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] known = OperatingSystem.IsWindows()
            ? [Path.Combine(home, ".local", "bin", "claude.exe")]
            : [Path.Combine(home, ".local", "bin", "claude"), "/opt/homebrew/bin/claude", "/usr/local/bin/claude", "/usr/bin/claude"];
        return known.FirstOrDefault(File.Exists);
    }

    private static string? FindInPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();
}
