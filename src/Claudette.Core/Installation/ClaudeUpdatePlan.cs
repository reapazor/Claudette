using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Claudette.Core.Diffs;
using Claudette.Core.Json;
using Claudette.Core.Processes;

namespace Claudette.Core.Installation;

public enum ClaudeUpdateKind
{
    /// <summary>Updates are turned off, or Claudette doesn't know how this installation updates.</summary>
    None,
    /// <summary>Native and npm installs: <c>claude update</c>. They also update themselves in the background.</summary>
    SelfUpdate,
    /// <summary><c>brew upgrade claude-code</c> (or <c>claude-code@latest</c>).</summary>
    Homebrew,
    /// <summary><c>winget upgrade Anthropic.ClaudeCode</c>. Fails while any <c>claude</c> is running.</summary>
    WinGet,
    /// <summary>apt, dnf and apk need admin rights: show the command, don't run it.</summary>
    Manual,
}

/// <summary>
/// How to update this Claude Code installation (DESIGN.md §12, the install method table): the command Claudette runs for
/// <b>Update now</b>, or the one it shows the user.
/// </summary>
/// <param name="Method">The install method in words, for Settings and the update dialog.</param>
/// <param name="Command">What <b>Update now</b> runs, or null when Claudette doesn't run anything.</param>
/// <param name="ManualCommand">A command to show instead, for installs that need admin rights.</param>
/// <param name="Check">
/// Asks the package manager whether a newer version is available (Homebrew and WinGet only; native installs update
/// themselves, and <c>claude doctor</c> reports the result).
/// </param>
/// <param name="Note">Why there's no update action, when there isn't one.</param>
public sealed partial record ClaudeUpdatePlan(
    ClaudeUpdateKind Kind,
    string Method,
    ProcessStartSpec? Command = null,
    string? ManualCommand = null,
    ProcessStartSpec? Check = null,
    string? Note = null)
{
    public const string WinGetPackage = "Anthropic.ClaudeCode";

    public const string DefaultCask = "claude-code";

    public bool CanRun => Command is not null;

    /// <summary>
    /// Windows locks a running executable, so a WinGet upgrade fails while any <c>claude</c> is running; Claudette then
    /// offers <b>Update on next launch</b> (DESIGN.md §12).
    /// </summary>
    public bool NeedsClaudeStopped => Kind == ClaudeUpdateKind.WinGet;

    /// <summary>The command as the user would type it.</summary>
    public string? CommandText => ManualCommand ?? (Command is { } command ? string.Join(' ', [Path.GetFileNameWithoutExtension(command.FileName), .. command.Arguments]) : null);

    /// <summary>
    /// Picks the plan from <c>claude doctor</c>'s report. <paramref name="resolvedClaudePath"/> is the real file behind
    /// the <c>claude</c> launcher, which shows the Homebrew cask (<c>…/Caskroom/claude-code@latest/…</c>).
    /// </summary>
    public static ClaudeUpdatePlan For(ClaudeDoctorReport? report, string claudePath, string? resolvedClaudePath, IFileProbe probe)
    {
        if (report is { UpdatesBlocked: true })
        {
            return new ClaudeUpdatePlan(ClaudeUpdateKind.None, MethodName(report), Note: "Updates are turned off on this machine (DISABLE_UPDATES).");
        }
        switch (report?.InstallType)
        {
            case ClaudeInstallType.Development:
                return new ClaudeUpdatePlan(ClaudeUpdateKind.None, "A development build", Note: "Development builds don't update.");
            case ClaudeInstallType.PackageManager:
                return ForPackageManager(report, claudePath, resolvedClaudePath, probe);
            default:
                // Native and npm installs, and anything doctor couldn't tell: claude update works out its own install.
                return new ClaudeUpdatePlan(
                    ClaudeUpdateKind.SelfUpdate,
                    report is null ? "Unknown" : MethodName(report),
                    new ProcessStartSpec(claudePath, ["update"]));
        }
    }

    private static ClaudeUpdatePlan ForPackageManager(ClaudeDoctorReport report, string claudePath, string? resolvedClaudePath, IFileProbe probe)
    {
        switch (report.PackageManager)
        {
            case "homebrew":
                {
                    var caskPath = new[] { report.InstallPath, resolvedClaudePath, claudePath }.FirstOrDefault(p => p is not null && CaskPattern().IsMatch(p));
                    var cask = caskPath is null ? DefaultCask : CaskPattern().Match(caskPath).Groups[2].Value;
                    var brew = FindBrew(caskPath is null ? null : CaskPattern().Match(caskPath).Groups[1].Value, probe);
                    var method = $"Homebrew ({cask})";
                    return brew is null
                        ? new ClaudeUpdatePlan(ClaudeUpdateKind.Manual, method, ManualCommand: $"brew upgrade {cask}")
                        : new ClaudeUpdatePlan(
                            ClaudeUpdateKind.Homebrew,
                            method,
                            new ProcessStartSpec(brew, ["upgrade", cask]),
                            Check: new ProcessStartSpec(brew, ["outdated", "--cask", "--greedy", "--json=v2", cask]));
                }
            case "winget":
                {
                    var winget = probe.FindOnPath("winget.exe") ?? KnownPath(probe, @"%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe");
                    const string agree = "--accept-source-agreements";
                    return winget is null
                        ? new ClaudeUpdatePlan(ClaudeUpdateKind.Manual, "WinGet", ManualCommand: $"winget upgrade {WinGetPackage}")
                        : new ClaudeUpdatePlan(
                            ClaudeUpdateKind.WinGet,
                            "WinGet",
                            new ProcessStartSpec(winget, ["upgrade", "--id", WinGetPackage, "--exact", agree, "--accept-package-agreements"]),
                            Check: new ProcessStartSpec(winget, ["list", "--id", WinGetPackage, "--exact", "--upgrade-available", agree]));
                }
            case "deb":
                return new ClaudeUpdatePlan(ClaudeUpdateKind.Manual, "apt", ManualCommand: "sudo apt update && sudo apt upgrade claude-code");
            case "rpm":
                return new ClaudeUpdatePlan(ClaudeUpdateKind.Manual, "dnf", ManualCommand: "sudo dnf upgrade claude-code");
            case "apk":
                return new ClaudeUpdatePlan(ClaudeUpdateKind.Manual, "apk", ManualCommand: "apk update && apk upgrade claude-code");
            default:
                var name = report.PackageManager is { Length: > 0 } pm && pm != "unknown" ? pm : "a package manager";
                return new ClaudeUpdatePlan(ClaudeUpdateKind.None, name, Note: $"Update Claude Code with {name}, the way you installed it.");
        }
    }

    /// <summary>The install method in words, such as "Native installer" or "npm (global)".</summary>
    public static string MethodName(ClaudeDoctorReport report) => report.InstallType switch
    {
        ClaudeInstallType.Native => "Native installer",
        ClaudeInstallType.NpmGlobal => "npm (global)",
        ClaudeInstallType.NpmLocal => "npm (local)",
        ClaudeInstallType.PackageManager => report.PackageManager switch
        {
            "homebrew" => "Homebrew",
            "winget" => "WinGet",
            "deb" => "apt",
            "rpm" => "dnf",
            { Length: > 0 } other => other,
            _ => "A package manager",
        },
        ClaudeInstallType.Development => "A development build",
        _ => report.InstallTypeText ?? "Unknown",
    };

    private static string? FindBrew(string? homebrewPrefix, IFileProbe probe)
    {
        // Homebrew only runs on macOS and Linux, so its paths always use '/'.
        if (homebrewPrefix is not null && $"{homebrewPrefix}/bin/brew" is var fromCask && probe.FileExists(fromCask))
        {
            return fromCask;
        }
        return probe.FindOnPath("brew")
            ?? new[] { "/opt/homebrew/bin/brew", "/usr/local/bin/brew", "/home/linuxbrew/.linuxbrew/bin/brew" }.FirstOrDefault(probe.FileExists);
    }

    private static string? KnownPath(IFileProbe probe, string path) =>
        probe.ExpandEnvironmentVariables(path) is var expanded && !expanded.Contains('%', StringComparison.Ordinal) && probe.FileExists(expanded) ? expanded : null;

    /// <summary><c>&lt;prefix&gt;/Caskroom/&lt;cask&gt;/&lt;version&gt;/…</c>: group 1 is the Homebrew prefix, group 2 the cask.</summary>
    [GeneratedRegex(@"^(.*)[/\\]Caskroom[/\\]([^/\\]+)[/\\]")]
    private static partial Regex CaskPattern();
}

/// <summary>Reads what package managers and <c>claude update</c> print.</summary>
public static partial class ClaudeUpdateOutput
{
    /// <summary>
    /// The newer version in <c>brew outdated --cask --json=v2 &lt;cask&gt;</c>, or null when the cask is up to date.
    /// For example <c>{"formulae":[],"casks":[{"name":"claude-code","current_version":"2.1.290",…}]}</c>.
    /// </summary>
    public static Version? ParseBrewOutdated(string json, string cask)
    {
        try
        {
            if (JsonTree.Parse(json)?["casks"] is JsonArray casks)
            {
                foreach (var entry in casks.OfType<JsonObject>())
                {
                    if (entry["name"]?.GetValueKind() == JsonValueKind.String && entry["name"]!.GetValue<string>() == cask
                        && entry["current_version"]?.GetValueKind() == JsonValueKind.String
                        && ClaudeLocator.TryParseVersion(entry["current_version"]!.GetValue<string>(), out var version))
                    {
                        return version;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    /// <summary>
    /// The available version in <c>winget list --id Anthropic.ClaudeCode --upgrade-available</c>: the table row for the
    /// package has its installed version, then the available one. Null when there's no row (no upgrade).
    /// </summary>
    public static Version? ParseWinGetList(string output)
    {
        foreach (var line in output.Split('\r', '\n'))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var id = Array.FindIndex(tokens, t => string.Equals(t, ClaudeUpdatePlan.WinGetPackage, StringComparison.OrdinalIgnoreCase));
            if (id >= 0 && id + 2 < tokens.Length
                && ClaudeLocator.TryParseVersion(tokens[id + 1], out var installed)
                && ClaudeLocator.TryParseVersion(tokens[id + 2], out var available)
                && available > installed)
            {
                return available;
            }
        }
        return null;
    }

    /// <summary>
    /// The version <c>claude update</c> left installed: "Successfully updated from 2.1.284 to version 2.1.290", or
    /// "Claude Code is up to date (2.1.290)". Null when neither line is there.
    /// </summary>
    public static Version? ParseClaudeUpdate(string output)
    {
        var match = UpdatedLine().Match(output);
        if (!match.Success)
        {
            match = UpToDateLine().Match(output);
        }
        return match.Success && ClaudeLocator.TryParseVersion(match.Groups[1].Value, out var version) ? version : null;
    }

    [GeneratedRegex(@"Successfully updated from \S+ to version (\S+)")]
    private static partial Regex UpdatedLine();

    [GeneratedRegex(@"is up to date \(([^)]+)\)")]
    private static partial Regex UpToDateLine();
}
