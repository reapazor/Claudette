namespace Claudette.Core.Updates;

/// <summary>
/// This copy of Claudette, as the foot of the Settings sidebar shows it and a bug report gives it (DESIGN.md §14,
/// "Version"): its version, how it was installed, and for a source build the commit it was built from, so a build of a
/// checkout can be told from the release with the same version.
/// </summary>
/// <param name="Commit">The commit it was built from, shortened; null when the build didn't record one.</param>
public sealed record AppBuild(AppVersion Version, AppInstallKind Kind, string? Commit = null)
{
    /// <summary>How many characters of the commit are shown, as git shortens it.</summary>
    public const int ShortCommitLength = 7;

    /// <summary>"Claudette 0.1.0", or "Claudette 0.1.0 · 842169b" for a source build.</summary>
    public string Label => Kind == AppInstallKind.SourceBuild && Commit is { } commit
        ? $"Claudette {Version} · {commit}"
        : $"Claudette {Version}";

    /// <summary>"0.1.0 (MSIX)", "0.1.0 (source build 842169b)".</summary>
    public string Description => Kind switch
    {
        AppInstallKind.SourceBuild => Commit is { } commit ? $"{Version} (source build {commit})" : $"{Version} (source build)",
        AppInstallKind.Msix => $"{Version} (MSIX)",
        AppInstallKind.MacApp => $"{Version} (.dmg)",
        _ => Version.ToString(),
    };

    /// <summary>
    /// The commit in an informational version such as <c>0.1.0+842169b…</c>, which the .NET SDK writes from the
    /// checkout, shortened. Null when there's no build metadata or it isn't a commit.
    /// </summary>
    public static string? CommitOf(string? informationalVersion)
    {
        if (informationalVersion?.IndexOf('+') is not (>= 0 and var plus))
        {
            return null;
        }
        // Metadata can hold several parts (1.0.0+build.5.abc123); the commit is the last.
        var metadata = informationalVersion[(plus + 1)..].Split('.', '+')[^1].Trim();
        return metadata.Length >= ShortCommitLength && metadata.All(char.IsAsciiHexDigit)
            ? metadata[..ShortCommitLength].ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// What a bug report needs to know, one line each: Claudette's version, Claude Code's, the OS and the runtime.
    /// Only versions: never paths, names or account details, since it's meant to be pasted in public.
    /// </summary>
    /// <param name="claudeCodeVersion">The Claude Code version found, or null when none was.</param>
    /// <param name="os">The OS as .NET describes it, such as "Microsoft Windows 10.0.26100".</param>
    /// <param name="runtimeIdentifier">Such as <c>win-x64</c> or <c>osx-arm64</c>.</param>
    /// <param name="runtime">The .NET runtime, such as ".NET 10.0.1".</param>
    public IReadOnlyList<string> Details(string? claudeCodeVersion, string os, string runtimeIdentifier, string runtime) =>
    [
        $"Claudette: {Description}",
        $"Claude Code: {claudeCodeVersion ?? "not found"}",
        $"OS: {os.Trim()} ({runtimeIdentifier})",
        $"Runtime: {runtime.Trim()}",
    ];

    /// <summary>
    /// A new GitHub issue on <paramref name="repository"/> with the report's outline and <paramref name="details"/>
    /// filled in. Opening it sends nothing: the user reads and submits it in the browser.
    /// </summary>
    public static string NewIssueUrl(string repository, IEnumerable<string> details)
    {
        var body = string.Join("\n",
        [
            "**What happened**",
            "",
            "",
            "**What you expected**",
            "",
            "",
            "**Steps to reproduce**",
            "",
            "1. ",
            "",
            "---",
            .. details,
        ]);
        return $"https://github.com/{repository}/issues/new?body={Uri.EscapeDataString(body)}";
    }
}
