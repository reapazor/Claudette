namespace Claudette.Core.Diffs;

/// <summary>How changed files open (DESIGN.md §8, "External diff tool").</summary>
public enum DiffToolKind
{
    BuiltIn,
    Preset,
    Custom,
}

/// <summary>The OS a preset variant is for. Injectable so detection can be tested for every OS on any OS.</summary>
public enum DiffToolPlatform
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// Where a preset's tool is installed on one OS, and how to call it there.
/// </summary>
/// <param name="Paths">Install locations, which may contain <c>%VARIABLES%</c>. Checked first, in order.</param>
/// <param name="PathNames">Executable names looked up on <c>PATH</c>.</param>
/// <param name="Arguments">
/// The argument template, one token per argument, with the placeholders <c>{left}</c>, <c>{right}</c>,
/// <c>{leftTitle}</c> and <c>{rightTitle}</c>.
/// </param>
public sealed record DiffToolVariant(DiffToolPlatform Platform, IReadOnlyList<string> Paths, IReadOnlyList<string> PathNames, IReadOnlyList<string> Arguments);

/// <summary>A diff tool Claudette knows how to call (DESIGN.md §8).</summary>
public sealed record DiffToolPreset(string Id, string Name, IReadOnlyList<DiffToolVariant> Variants)
{
    public DiffToolVariant? For(DiffToolPlatform platform) => Variants.FirstOrDefault(v => v.Platform == platform);
}

/// <summary>A preset whose tool was found on this machine.</summary>
public sealed record DetectedDiffTool(DiffToolPreset Preset, string ExecutablePath);

/// <summary>The built-in presets (DESIGN.md §8, "External diff tool").</summary>
public static class DiffToolPresets
{
    public const string BeyondCompare = "beyond-compare";
    public const string VSCode = "vscode";
    public const string WinMerge = "winmerge";
    public const string Kaleidoscope = "kaleidoscope";
    public const string Meld = "meld";
    public const string P4Merge = "p4merge";
    public const string GitDifftool = "git-difftool";

    public static DiffToolPlatform CurrentPlatform { get; } =
        OperatingSystem.IsWindows() ? DiffToolPlatform.Windows
        : OperatingSystem.IsMacOS() ? DiffToolPlatform.MacOS
        : DiffToolPlatform.Linux;

    public static IReadOnlyList<DiffToolPreset> All { get; } =
    [
        new(BeyondCompare, "Beyond Compare",
        [
            new(DiffToolPlatform.Windows,
                [@"%ProgramFiles%\Beyond Compare 5\BCompare.exe", @"%ProgramFiles%\Beyond Compare 4\BCompare.exe", @"%LOCALAPPDATA%\Programs\Beyond Compare 5\BCompare.exe"],
                ["BCompare.exe"],
                ["/lro", "/title1={leftTitle}", "/title2={rightTitle}", "{left}", "{right}"]),
            new(DiffToolPlatform.MacOS,
                ["/usr/local/bin/bcomp", "/Applications/Beyond Compare.app/Contents/MacOS/bcomp"],
                ["bcomp"],
                ["-lro", "-title1={leftTitle}", "-title2={rightTitle}", "{left}", "{right}"]),
            new(DiffToolPlatform.Linux,
                [],
                ["bcompare"],
                ["-lro", "-title1={leftTitle}", "-title2={rightTitle}", "{left}", "{right}"]),
        ]),
        new(VSCode, "VS Code",
        [
            new(DiffToolPlatform.Windows,
                [@"%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd", @"%ProgramFiles%\Microsoft VS Code\bin\code.cmd"],
                ["code.cmd"],
                ["--diff", "--wait", "{left}", "{right}"]),
            new(DiffToolPlatform.MacOS,
                ["/usr/local/bin/code", "/opt/homebrew/bin/code", "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code"],
                ["code"],
                ["--diff", "--wait", "{left}", "{right}"]),
            new(DiffToolPlatform.Linux,
                [],
                ["code"],
                ["--diff", "--wait", "{left}", "{right}"]),
        ]),
        new(WinMerge, "WinMerge",
        [
            new(DiffToolPlatform.Windows,
                [@"%ProgramFiles%\WinMerge\WinMergeU.exe", @"%ProgramFiles(x86)%\WinMerge\WinMergeU.exe", @"%LOCALAPPDATA%\Programs\WinMerge\WinMergeU.exe"],
                ["WinMergeU.exe"],
                ["/e", "/u", "/wl", "/dl", "{leftTitle}", "/dr", "{rightTitle}", "{left}", "{right}"]),
        ]),
        new(Kaleidoscope, "Kaleidoscope",
        [
            new(DiffToolPlatform.MacOS,
                ["/usr/local/bin/ksdiff", "/opt/homebrew/bin/ksdiff"],
                ["ksdiff"],
                ["{left}", "{right}"]),
        ]),
        new(Meld, "Meld",
        [
            new(DiffToolPlatform.Windows,
                [@"%ProgramFiles%\Meld\Meld.exe", @"%ProgramFiles(x86)%\Meld\Meld.exe"],
                ["Meld.exe"],
                ["--label={leftTitle}", "--label={rightTitle}", "{left}", "{right}"]),
            new(DiffToolPlatform.MacOS,
                ["/opt/homebrew/bin/meld", "/usr/local/bin/meld"],
                ["meld"],
                ["--label={leftTitle}", "--label={rightTitle}", "{left}", "{right}"]),
            new(DiffToolPlatform.Linux,
                [],
                ["meld"],
                ["--label={leftTitle}", "--label={rightTitle}", "{left}", "{right}"]),
        ]),
        new(P4Merge, "P4Merge",
        [
            new(DiffToolPlatform.Windows,
                [@"%ProgramFiles%\Perforce\p4merge.exe"],
                ["p4merge.exe"],
                ["-nl", "{leftTitle}", "-nr", "{rightTitle}", "{left}", "{right}"]),
            new(DiffToolPlatform.MacOS,
                ["/Applications/p4merge.app/Contents/MacOS/p4merge"],
                ["p4merge"],
                ["-nl", "{leftTitle}", "-nr", "{rightTitle}", "{left}", "{right}"]),
            new(DiffToolPlatform.Linux,
                [],
                ["p4merge"],
                ["-nl", "{leftTitle}", "-nr", "{rightTitle}", "{left}", "{right}"]),
        ]),
        new(GitDifftool, "Your git difftool",
        [
            new(DiffToolPlatform.Windows, [], ["git.exe"], ["difftool", "--no-prompt", "--no-index", "{left}", "{right}"]),
            new(DiffToolPlatform.MacOS, ["/opt/homebrew/bin/git", "/usr/local/bin/git"], ["git"], ["difftool", "--no-prompt", "--no-index", "{left}", "{right}"]),
            new(DiffToolPlatform.Linux, [], ["git"], ["difftool", "--no-prompt", "--no-index", "{left}", "{right}"]),
        ]),
    ];

    public static DiffToolPreset? Find(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// Finds which presets are installed (DESIGN.md §8). Only checks for files, so it's cheap enough for the Settings
/// window to call whenever it opens.
/// </summary>
public static class DiffToolDetector
{
    /// <summary>The presets found on this machine, in <see cref="DiffToolPresets.All"/> order.</summary>
    public static IReadOnlyList<DetectedDiffTool> Detect(IFileProbe probe) => Detect(probe, DiffToolPresets.CurrentPlatform);

    public static IReadOnlyList<DetectedDiffTool> Detect(IFileProbe probe, DiffToolPlatform platform) =>
        DiffToolPresets.All
            .Select(preset => Find(probe, platform, preset))
            .OfType<DetectedDiffTool>()
            .ToArray();

    /// <summary>One preset, or null when it isn't installed (or doesn't exist on this OS).</summary>
    public static DetectedDiffTool? Find(IFileProbe probe, DiffToolPlatform platform, DiffToolPreset preset)
    {
        if (preset.For(platform) is not { } variant)
        {
            return null;
        }
        foreach (var candidate in variant.Paths)
        {
            var path = probe.ExpandEnvironmentVariables(candidate);
            if (!path.Contains('%', StringComparison.Ordinal) && probe.FileExists(path))
            {
                return new DetectedDiffTool(preset, path);
            }
        }
        foreach (var name in variant.PathNames)
        {
            if (probe.FindOnPath(name) is { } found)
            {
                return new DetectedDiffTool(preset, found);
            }
        }
        return null;
    }
}
