using System.Text.RegularExpressions;

namespace Claudette.Core.Installation;

/// <summary>How Claude Code was installed: the first word of the <c>Running:</c> line of <c>claude doctor</c>.</summary>
public enum ClaudeInstallType
{
    Unknown,
    /// <summary>The native installer, which updates itself in the background.</summary>
    Native,
    NpmGlobal,
    NpmLocal,
    /// <summary>Homebrew, WinGet, apt, dnf, apk and the like; <see cref="ClaudeDoctorReport.PackageManager"/> says which.</summary>
    PackageManager,
    Development,
}

/// <summary>A problem <c>claude doctor</c> found, with the fix it suggests.</summary>
public sealed record DoctorWarning(string Issue, string? Fix);

/// <summary>
/// What <c>claude doctor</c> reports about the installation (DESIGN.md §12). The command is documented; the line format
/// isn't, so every field is optional and unknown lines are ignored.
/// </summary>
public sealed record ClaudeDoctorReport
{
    public ClaudeInstallType InstallType { get; init; }

    /// <summary>The <c>Running:</c> line as printed, for display when the type isn't recognized.</summary>
    public string? InstallTypeText { get; init; }

    public Version? Version { get; init; }

    /// <summary><c>homebrew</c>, <c>winget</c>, <c>deb</c>, <c>rpm</c>, <c>apk</c>, <c>pacman</c>, <c>mise</c> or <c>asdf</c>.</summary>
    public string? PackageManager { get; init; }

    public string? InstallPath { get; init; }

    public string? ConfigInstallMethod { get; init; }

    /// <summary><c>enabled</c>, <c>disabled (set by env: DISABLE_AUTOUPDATER)</c>, <c>Managed by package manager</c>, …</summary>
    public string? AutoUpdates { get; init; }

    /// <summary><c>latest</c> or <c>stable</c>.</summary>
    public string? Channel { get; init; }

    /// <summary>For example <c>success → 2.1.284 (2026-09-28)</c> or <c>none recorded</c>.</summary>
    public string? LastUpdateAttempt { get; init; }

    public IReadOnlyList<DoctorWarning> Warnings { get; init; } = [];

    public bool AutoUpdatesEnabled => AutoUpdates?.StartsWith("enabled", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// <c>DISABLE_UPDATES</c> blocks every update path, including <c>claude update</c>; Claudette then shows the version
    /// but doesn't offer to update (DESIGN.md §12). <c>DISABLE_AUTOUPDATER</c> only stops the background updater.
    /// </summary>
    public bool UpdatesBlocked => AutoUpdates?.Contains("DISABLE_UPDATES", StringComparison.Ordinal) == true;
}

/// <summary>Reads the output of <c>claude doctor</c>.</summary>
public static partial class ClaudeDoctor
{
    public static ClaudeDoctorReport Parse(string output)
    {
        var report = new ClaudeDoctorReport();
        var warnings = new List<DoctorWarning>();
        var inWarnings = false;
        foreach (var rawLine in AnsiEscape().Replace(output, "").Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (WarningsHeader().IsMatch(line))
            {
                inWarnings = true;
                continue;
            }
            if (inWarnings)
            {
                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    warnings.Add(new DoctorWarning(line[2..].Trim(), null));
                }
                else if (line.TrimStart().StartsWith("Fix:", StringComparison.Ordinal) && warnings.Count > 0 && warnings[^1].Fix is null)
                {
                    warnings[^1] = warnings[^1] with { Fix = line.TrimStart()["Fix:".Length..].Trim() };
                }
                else if (line.Length == 0)
                {
                    inWarnings = false;
                }
                continue;
            }
            if (line.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0)
            {
                report = Apply(report, line[..colon], line[(colon + 2)..].Trim());
            }
        }
        return report with { Warnings = warnings };
    }

    private static ClaudeDoctorReport Apply(ClaudeDoctorReport report, string key, string value) => key switch
    {
        "Running" => RunningLine().Match(value) is { Success: true } running
            ? report with
            {
                InstallTypeText = running.Groups[1].Value,
                InstallType = ParseInstallType(running.Groups[1].Value),
                Version = ClaudeLocator.TryParseVersion(running.Groups[2].Value, out var version) ? version : null,
            }
            : report with { InstallTypeText = value, InstallType = ParseInstallType(value) },
        "Package manager" => report with { PackageManager = value },
        "Path" => report with { InstallPath = value },
        "Config install method" => report with { ConfigInstallMethod = value },
        "Auto-updates" => report with { AutoUpdates = value },
        "Auto-update channel" => report with { Channel = value },
        "Last update attempt" => report with { LastUpdateAttempt = value },
        _ => report,
    };

    public static ClaudeInstallType ParseInstallType(string text) => text.Trim() switch
    {
        "native" => ClaudeInstallType.Native,
        "npm-global" => ClaudeInstallType.NpmGlobal,
        "npm-local" => ClaudeInstallType.NpmLocal,
        "package-manager" => ClaudeInstallType.PackageManager,
        "development" => ClaudeInstallType.Development,
        _ => ClaudeInstallType.Unknown,
    };

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"^\d+ warnings? found", RegexOptions.IgnoreCase)]
    private static partial Regex WarningsHeader();

    [GeneratedRegex(@"^(\S+)\s*\(([^)]*)\)")]
    private static partial Regex RunningLine();
}
