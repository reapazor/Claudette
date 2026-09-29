using Claudette.Core.Diffs;
using Claudette.Core.Processes;
using Claudette.Core.Settings;

namespace Claudette.Core.ProjectTools;

/// <summary>How to open a solution: a program to start, or the OS's own app when <see cref="Spec"/> is null.</summary>
/// <param name="Note">Why the OS's app is used instead of the chosen one, such as "Rider wasn't found".</param>
public sealed record SolutionOpening(ProcessStartSpec? Spec, string? Note = null);

/// <summary>
/// Opens a solution or workspace with the IDE chosen in Settings → Project tools (DESIGN.md §18). Pure apart from
/// looking for the IDE's files, so every OS is tested on any machine.
/// </summary>
public static class SolutionOpeners
{
    private const string Open = "/usr/bin/open";

    public static SolutionOpening For(SolutionOpener opener, string path, ToolOS os, ProjectToolPaths paths, IFileProbe probe, string? customPath = null)
    {
        switch (opener)
        {
            case SolutionOpener.Custom:
                if (string.IsNullOrWhiteSpace(customPath))
                {
                    return new SolutionOpening(null, "No program is set for opening solutions; used the OS's app. Choose one in Settings → Project tools.");
                }
                return new SolutionOpening(Program(customPath.Trim(), path, os));
            case SolutionOpener.Rider:
                return Find(os switch
                {
                    ToolOS.MacOS => MacApp("Rider", path),
                    ToolOS.Windows => First(probe, probe.FindOnPath("rider64.exe"), paths.LocalAppData is { } local ? Path.Combine(local, "JetBrains", "Toolbox", "scripts", "rider.cmd") : null)
                        ?? Newest(paths.ProgramFiles, Path.Combine("JetBrains"), "JetBrains Rider*", Path.Combine("bin", "rider64.exe")),
                    _ => First(probe, probe.FindOnPath("rider"), probe.FindOnPath("rider.sh"), Path.Combine(paths.Home, ".local", "share", "JetBrains", "Toolbox", "scripts", "rider")),
                }, "Rider", path, os);
            case SolutionOpener.VisualStudio:
                return Find(os switch
                {
                    ToolOS.MacOS => MacApp("Visual Studio", path),
                    ToolOS.Windows => Newest(paths.ProgramFiles, "Microsoft Visual Studio", "*", Path.Combine("*", "Common7", "IDE", "devenv.exe")),
                    _ => null,
                }, "Visual Studio", path, os);
            case SolutionOpener.VSCode:
                return Find(os switch
                {
                    ToolOS.MacOS => MacApp("Visual Studio Code", path),
                    ToolOS.Windows => First(probe,
                        paths.LocalAppData is { } local ? Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe") : null,
                        paths.ProgramFiles is { } programs ? Path.Combine(programs, "Microsoft VS Code", "Code.exe") : null,
                        probe.FindOnPath("code.cmd")),
                    _ => First(probe, probe.FindOnPath("code")),
                }, "VS Code", path, os);
            default:
                return new SolutionOpening(null);
        }
    }

    private static SolutionOpening Find(object? found, string name, string path, ToolOS os) => found switch
    {
        ProcessStartSpec spec => new SolutionOpening(spec),
        string program => new SolutionOpening(Program(program, path, os)),
        _ => new SolutionOpening(null, $"{name} wasn't found, so the OS's app opened it. Choose another in Settings → Project tools."),
    };

    /// <summary><c>open -a "Rider" &lt;path&gt;</c>: macOS finds the app wherever it's installed.</summary>
    private static ProcessStartSpec MacApp(string app, string path) => new(Open, ["-a", app, path]) { Detached = true };

    private static ProcessStartSpec Program(string program, string path, ToolOS os) =>
        os == ToolOS.Windows && (program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            ? CommandLines.BatchFile(program, [path]) with { Detached = true }
            : new ProcessStartSpec(program, [path]) { Detached = true };

    private static string? First(IFileProbe probe, params string?[] candidates) =>
        candidates.FirstOrDefault(c => c is not null && probe.FileExists(c));

    /// <summary>The newest install under a folder: <c>&lt;root&gt;\&lt;folder&gt;\&lt;pattern&gt;\&lt;file&gt;</c>, by folder name.</summary>
    private static string? Newest(string? root, string folder, string pattern, string file)
    {
        if (root is null)
        {
            return null;
        }
        try
        {
            var parent = Path.Combine(root, folder);
            if (!Directory.Exists(parent))
            {
                return null;
            }
            var parts = file.Split(Path.DirectorySeparatorChar);
            IEnumerable<string> candidates = Directory.EnumerateDirectories(parent, pattern);
            foreach (var part in parts[..^1])
            {
                candidates = part.Contains('*', StringComparison.Ordinal)
                    ? candidates.SelectMany(c => Directory.Exists(c) ? Directory.EnumerateDirectories(c, part) : [])
                    : candidates.Select(c => Path.Combine(c, part));
            }
            return candidates.Select(c => Path.Combine(c, parts[^1])).Where(File.Exists).OrderDescending(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
