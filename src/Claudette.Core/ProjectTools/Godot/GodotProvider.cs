using System.Text.RegularExpressions;
using Claudette.Core.Processes;

namespace Claudette.Core.ProjectTools.Godot;

/// <summary>
/// A Godot project (DESIGN.md §18, "Godot"): a folder with <c>project.godot</c>, a Godot <c>ConfigFile</c> read line
/// by line and tolerantly.
/// </summary>
/// <param name="ConfigVersion"><c>config_version</c>: 5 for Godot 4, 4 for Godot 3.</param>
/// <param name="Version">From <c>config/features</c> (<c>"4.3"</c>), else "4" or "3" from <c>config_version</c>; null when neither says.</param>
public sealed partial record GodotProject(string File, string Root, string Name, int? ConfigVersion, string? Version, bool IsCSharp, IReadOnlyList<string> Features)
{
    public static GodotProject Read(string file)
    {
        var root = Path.GetDirectoryName(file) ?? ".";
        string text;
        try
        {
            text = System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = "";
        }
        return Parse(text, file, root, CSharpFiles(root).Count > 0);
    }

    /// <param name="hasCSharpFiles">The folder has a <c>.csproj</c> or <c>.sln</c>, as a C# project has once Godot has built it.</param>
    public static GodotProject Parse(string text, string file, string root, bool hasCSharpFiles)
    {
        var section = "";
        var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        int? configVersion = null;
        var features = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';')
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                sections.Add(section);
                continue;
            }
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }
            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (section.Length == 0 && key == "config_version" && int.TryParse(value, out var version))
            {
                configVersion = version;
            }
            else if (section == "application" && key == "config/name")
            {
                name = Unquote(value);
            }
            else if (section == "application" && key == "config/features")
            {
                features.AddRange(QuotedStrings().Matches(value).Select(m => m.Groups[1].Value));
            }
        }
        var featureVersion = features.FirstOrDefault(f => VersionPattern().IsMatch(f));
        var inferred = configVersion switch
        {
            >= 5 => "4",
            4 or 3 => "3",
            _ => null,
        };
        var isCSharp = features.Any(f => f.Equals("C#", StringComparison.OrdinalIgnoreCase)) || sections.Contains("dotnet") || sections.Contains("mono") || hasCSharpFiles;
        return new GodotProject(file, root, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) : name,
            configVersion, featureVersion ?? inferred, isCSharp, features);
    }

    /// <summary>Godot 4 keeps its imports in <c>.godot/</c>; Godot 3 in <c>.import/</c>.</summary>
    public string ImportFolder => ConfigVersion is >= 5 || ConfigVersion is null && Version?.StartsWith('4') == true ? ".godot" : ".import";

    /// <summary>The project's <c>.sln</c> files, then its <c>.csproj</c> files, by name.</summary>
    public static IReadOnlyList<string> CSharpFiles(string root) => [.. ProjectSearch.FilesIn(root, "*.sln"), .. ProjectSearch.FilesIn(root, "*.csproj")];

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal) : value;

    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex QuotedStrings();

    [GeneratedRegex(@"^\d+\.\d+")]
    private static partial Regex VersionPattern();
}

/// <summary>
/// Finds the Godot executable (DESIGN.md §18, "Godot"). There's no standard install, so: a pick remembered for the
/// project, Settings' path, <c>godot</c>, <c>godot4</c>, <c>Godot</c> or <c>godot-mono</c> on the <c>PATH</c>,
/// <c>/Applications/Godot.app</c> or <c>Godot_mono.app</c> on macOS, and WinGet's and Scoop's usual places on Windows.
/// </summary>
public static class GodotExecutables
{
    public const string ExecutableKey = "godot";

    public static readonly IReadOnlyList<string> PathNames = ["godot", "godot4", "Godot", "godot-mono"];

    public static string? Find(GodotProject project, ProjectToolContext context) =>
        (context.Memory.Get(project.File, ExecutableKey) is { Length: > 0 } picked ? Executable(picked, context.OS) : null)
        ?? (context.Settings.GodotPath is { Length: > 0 } setting ? Executable(setting, context.OS) : null)
        ?? Detect(context);

    /// <summary>Godot where it's usually found, ignoring Settings and picks: for <b>Detect</b> in Settings too.</summary>
    public static string? Detect(ProjectToolContext context)
    {
        var probe = context.Probe;
        var exe = context.OS == ToolOS.Windows ? ".exe" : "";
        foreach (var name in PathNames)
        {
            if (probe.FindOnPath(name + exe) is { } onPath)
            {
                return onPath;
            }
        }
        IEnumerable<string> candidates = context.OS switch
        {
            ToolOS.MacOS => [Path.Combine(context.Paths.Applications, "Godot.app"), Path.Combine(context.Paths.Applications, "Godot_mono.app")],
            ToolOS.Windows => WindowsPlaces(context),
            _ => [],
        };
        return candidates.Select(c => Executable(c, context.OS)).FirstOrDefault(c => c is not null);
    }

    /// <summary>Scoop's shims, WinGet's links, and WinGet's package folder: a modest look, not a search.</summary>
    private static IEnumerable<string> WindowsPlaces(ProjectToolContext context)
    {
        var scoop = Path.Combine(context.Paths.Home, "scoop", "shims");
        yield return Path.Combine(scoop, "godot.exe");
        yield return Path.Combine(scoop, "godot-mono.exe");
        if (context.Paths.LocalAppData is not { } local)
        {
            yield break;
        }
        var winget = Path.Combine(local, "Microsoft", "WinGet");
        yield return Path.Combine(winget, "Links", "godot.exe");
        string[] packages;
        try
        {
            var folder = Path.Combine(winget, "Packages");
            packages = Directory.Exists(folder)
                ? Directory.EnumerateDirectories(folder, "GodotEngine.GodotEngine*")
                    .SelectMany(p => Directory.EnumerateFiles(p, "Godot*.exe"))
                    .Where(f => !f.Contains("_console", StringComparison.OrdinalIgnoreCase))
                    .OrderDescending(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            packages = [];
        }
        foreach (var package in packages)
        {
            yield return package;
        }
    }

    /// <summary>The executable itself, or macOS's <c>Godot.app</c>; null when there's none.</summary>
    public static string? Executable(string path, ToolOS os)
    {
        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path.Trim());
            if (File.Exists(trimmed))
            {
                return trimmed;
            }
            if (os == ToolOS.MacOS && Directory.Exists(trimmed) && trimmed.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                var macOS = Path.Combine(trimmed, "Contents", "MacOS");
                return File.Exists(Path.Combine(macOS, "Godot")) ? Path.Combine(macOS, "Godot")
                    : Directory.Exists(macOS) ? Directory.EnumerateFiles(macOS).Order(StringComparer.Ordinal).FirstOrDefault()
                    : null;
            }
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The .NET ("mono") build of Godot, which a C# project needs: its name usually has <c>mono</c> in it, and it has a
    /// <c>GodotSharp</c> folder beside it (in <c>Contents/Resources</c> on macOS).
    /// </summary>
    public static bool IsDotNetBuild(string executable)
    {
        if (executable.Contains("mono", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var folder = Path.GetDirectoryName(executable) ?? "";
        return Directory.Exists(Path.Combine(folder, "GodotSharp"))
            || Directory.Exists(Path.Combine(Path.GetDirectoryName(folder) ?? folder, "Resources", "GodotSharp"));
    }

    public static (string? Path, string? Error) Validate(string picked, ToolOS os) =>
        Executable(picked, os) is { } found && Path.GetFileName(found).StartsWith("godot", StringComparison.OrdinalIgnoreCase)
            ? (found, null)
            : (null, $"{picked} doesn't look like Godot: its name should start with Godot.");

    // ---- Commands ---------------------------------------------------------------------------------------------------

    /// <summary><b>Open in Godot</b>: <c>godot --editor --path "&lt;folder&gt;"</c>, detached.</summary>
    public static ProcessStartSpec OpenEditor(string godot, string root) =>
        new(godot, ["--editor", "--path", root]) { WorkingDirectory = root, Detached = true };

    /// <summary><b>Run project</b>: <c>godot --path "&lt;folder&gt;"</c>, detached.</summary>
    public static ProcessStartSpec Run(string godot, string root) =>
        new(godot, ["--path", root]) { WorkingDirectory = root, Detached = true };

    /// <summary><b>Build C#</b>: <c>dotnet build "&lt;solution or project&gt;"</c>.</summary>
    public static ProcessStartSpec BuildCSharp(string solution, string root) =>
        new("dotnet", ["build", solution]) { WorkingDirectory = root };

    /// <summary>For Claude: checking the project without the editor.</summary>
    public static ProcessStartSpec Check(string godot, string root) => new(godot, ["--headless", "--path", root, "--quit"]);

    /// <summary>For Claude: checking one script.</summary>
    public static ProcessStartSpec CheckScript(string godot, string root, string script) =>
        new(godot, ["--headless", "--path", root, "--check-only", "--script", script]);
}

/// <summary>Godot projects (DESIGN.md §18, "Godot").</summary>
public sealed class GodotProvider : IProjectToolProvider
{
    public const string KindId = "godot";

    /// <summary>Every running Godot, whatever its build is called: <c>godot</c>, <c>Godot_v4.3-stable_mono_win64</c>.</summary>
    public static readonly IReadOnlyList<string> EditorProcessNames = ["godot*"];

    public string Kind => KindId;

    public IReadOnlyList<ProjectCandidate> Find(string folder, ProjectToolContext context) =>
        ProjectSearch.Find(folder, dir => File.Exists(Path.Combine(dir, "project.godot")) ? [Path.Combine(dir, "project.godot")] : [], context)
            .Select(file => new ProjectCandidate(KindId, file, Path.GetFileName(Path.GetDirectoryName(file) ?? file)))
            .ToArray();

    public ProjectInfo Describe(ProjectCandidate candidate, ProjectToolContext context)
    {
        var project = GodotProject.Read(candidate.Path);
        var godot = GodotExecutables.Find(project, context);
        var language = project.IsCSharp ? "C#" : "GDScript";
        var versionText = project.Version is { } v ? $"Godot {v}" : "Godot";

        string? problem = null;
        if (godot is null)
        {
            problem = "Godot wasn't found. Set its path in Settings → Project tools, or choose it in this menu.";
        }
        else if (project.IsCSharp && !GodotExecutables.IsDotNetBuild(godot))
        {
            problem = $"This is a C# project, but {godot} isn't the .NET build of Godot, which C# needs. Get the .NET build (its name has \"mono\" in it), or choose it in this menu.";
        }
        var noGodot = godot is null ? "Godot wasn't found: choose it in this menu, or set it in Settings." : null;

        var actions = new List<ProjectAction>
        {
            new("open-in-godot", "Open in Godot", ProjectActionKind.Launch)
            {
                Description = "Opens the project in the Godot editor.",
                Process = godot is null ? null : GodotExecutables.OpenEditor(godot, project.Root),
                IsMain = true,
                DisabledReason = noGodot,
            },
            new("run-project", "Run project", ProjectActionKind.Launch)
            {
                Description = "Runs the project's main scene.",
                Process = godot is null ? null : GodotExecutables.Run(godot, project.Root),
                DisabledReason = noGodot,
            },
        };

        var csharp = GodotProject.CSharpFiles(project.Root);
        var solution = csharp.FirstOrDefault(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase));
        if (project.IsCSharp)
        {
            var target = solution ?? csharp.FirstOrDefault();
            actions.Add(new ProjectAction("build-csharp", "Build C#", ProjectActionKind.Run)
            {
                Description = target is null ? null : $"Runs dotnet build on {Path.GetFileName(target)}.",
                Process = target is null ? null : GodotExecutables.BuildCSharp(target, project.Root),
                DisabledReason = target is null ? "There's no .sln or .csproj yet: open the project in Godot, which makes them." : null,
            });
            actions.Add(new ProjectAction("open-solution", "Open solution", ProjectActionKind.Open)
            {
                Description = solution is null ? null : $"Opens {Path.GetFileName(solution)}.",
                OpenPath = solution,
                OpenWithIde = true,
                DisabledReason = solution is null ? "There's no .sln yet: open the project in Godot, which makes it." : null,
            });
        }

        var imports = Path.Combine(project.Root, project.ImportFolder);
        actions.Add(new ProjectAction("clean", $"Clean {project.ImportFolder}…", ProjectActionKind.Destructive)
        {
            Description = $"Deletes {project.ImportFolder}/, Godot's imported assets and caches.",
            Destructive = new DeleteFolders(project.Root, () => Directory.Exists(imports) ? [imports] : [],
                "Godot reimports every asset the next time it opens the project, which can take a while."),
            DisabledReason = Directory.Exists(imports) ? null : $"Nothing to clean: there's no {project.ImportFolder} folder.",
        });

        var running = context.Processes?.Find(EditorProcessNames);
        actions.Add(new ProjectAction("kill-editors", "Kill all Godot editors…", ProjectActionKind.Destructive)
        {
            Description = "Ends every running Godot, with whatever each started.",
            Destructive = new KillProcesses(EditorProcessNames, "Godot editor", p => SystemProcessNames.FolderAfter(p.CommandLine, "--path")),
            DisabledReason = running is { Count: 0 } ? "No Godot editor is running" : null,
        });

        var details = new List<ProjectDetail>
        {
            new("Project", project.File),
            new("Version", $"{versionText}, {language}"),
            new("Godot", godot ?? problem!),
        };
        if (project.Features.Count > 0)
        {
            details.Add(new ProjectDetail("Features", string.Join(", ", project.Features)));
        }

        return new ProjectInfo
        {
            Kind = KindId,
            KindName = "Godot",
            Name = project.Name,
            Root = project.Root,
            ProjectPath = project.File,
            ShortVersion = project.Version is { } short_ ? $"Godot {short_}" : "Godot",
            HeaderLines = problem is not null ? [problem] : [$"{versionText} · {language}", godot!],
            Details = details,
            Actions = actions,
            Fix = new ProjectFix(GodotExecutables.ExecutableKey, godot is null ? "Choose Godot executable…" : "Choose another Godot executable…",
                $"Choose the Godot executable for {project.Name}", PickFolder: false, picked => GodotExecutables.Validate(picked, context.OS)),
            Problem = problem,
            SystemPromptNote = context.Settings.TellClaudeAboutGodot ? SystemPromptNote(project, godot, solution ?? csharp.FirstOrDefault()) : null,
        };
    }

    /// <summary>
    /// The note to Claude (DESIGN.md §18): the version and language, how to check the project and a script without the
    /// editor, how to build the C# code, and not to open the editor unasked.
    /// </summary>
    public static string SystemPromptNote(GodotProject project, string? godot, string? csharpTarget)
    {
        var version = project.Version is { } v ? $"Godot {v}" : "Godot";
        var executable = godot ?? "godot";
        var lines = new List<string>
        {
            $"This is a {version} project ({(project.IsCSharp ? "C#" : "GDScript")}), {project.Name}, at {project.Root}.",
            $"To check it without the editor, run: {CommandLines.Display(GodotExecutables.Check(executable, project.Root))}",
            $"To check one script, run: {CommandLines.Display(GodotExecutables.CheckScript(executable, project.Root, "res://path/to/script.gd"))}",
        };
        if (project.IsCSharp && csharpTarget is not null)
        {
            lines.Add($"To build the C# code, run: {CommandLines.Display(GodotExecutables.BuildCSharp(csharpTarget, project.Root))}");
        }
        lines.Add("Don't open the editor unless asked.");
        return string.Join('\n', lines);
    }
}
