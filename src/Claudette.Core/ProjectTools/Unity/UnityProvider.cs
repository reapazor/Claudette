using System.Security.Cryptography;
using System.Text;
using Claudette.Core.Settings;

namespace Claudette.Core.ProjectTools.Unity;

/// <summary>
/// Unity projects (DESIGN.md §18, "Unity"): the editor for the project's version, opening it, EditMode tests and
/// regenerating the C# solution in batch mode, the solution and logs, cleaning <c>Library</c>, and ending editors.
/// </summary>
public sealed class UnityProvider : IProjectToolProvider
{
    public const string KindId = "unity";

    /// <summary>Where a project's code optimization is kept in its memory.</summary>
    public const string OptimizationKey = "codeOptimization";

    /// <summary>The editor's process name (<c>Unity.exe</c> on Windows); Unity Hub is another process and isn't counted.</summary>
    public static readonly IReadOnlyList<string> EditorProcessNames = ["Unity"];

    public string Kind => KindId;

    public IReadOnlyList<ProjectCandidate> Find(string folder, ProjectToolContext context) =>
        ProjectSearch.Find(folder, dir => UnityProject.IsProject(dir) ? [dir] : [], context)
            .Select(root => new ProjectCandidate(KindId, root, Path.GetFileName(Path.TrimEndingDirectorySeparator(root))))
            .ToArray();

    public ProjectInfo Describe(ProjectCandidate candidate, ProjectToolContext context)
    {
        var os = context.OS;
        var project = UnityProject.Read(candidate.Path);
        var editor = UnityEditors.Find(project, context);
        var optimization = Enum.TryParse<UnityCodeOptimization>(context.Memory.Get(project.Root, OptimizationKey), out var remembered)
            ? remembered
            : context.Settings.UnityCodeOptimization;
        var version = project.EditorVersion;

        string? problem = null;
        if (version is null)
        {
            problem = "ProjectSettings/ProjectVersion.txt doesn't say which Unity version the project uses.";
        }
        else if (editor is null)
        {
            problem = $"Unity {version} isn't installed, or Unity Hub doesn't list it. Choose its editor in this menu.";
        }
        var noEditor = editor is null ? "The Unity editor wasn't found: choose it in this menu." : null;
        var isOpen = IsOpen(project, context);
        var locked = isOpen ? "Unity has this project open, and locks it while it does: close the editor first." : null;

        var actions = new List<ProjectAction>
        {
            new("open-in-unity", isOpen ? "Unity has this project open" : optimization == UnityCodeOptimization.Debug ? "Open in Unity (Debug)" : "Open in Unity", ProjectActionKind.Launch)
            {
                Description = optimization == UnityCodeOptimization.Debug ? "Opens the project with -debugCodeOptimization, for stepping through scripts." : "Opens the project in the Unity editor.",
                Process = editor is null ? null : UnityEditors.Open(editor, project.Root, optimization),
                IsMain = true,
                DisabledReason = noEditor ?? (isOpen ? "Unity has this project open." : null),
            },
        };

        var results = Path.Combine(context.JobsDirectory ?? Path.GetTempPath(), $"unity-{ShortHash(project.Root)}", "EditModeResults.xml");
        actions.Add(new ProjectAction("run-editmode-tests", "Run EditMode tests", ProjectActionKind.Run)
        {
            Description = "Runs the EditMode tests in batch mode, and says how many passed.",
            Process = editor is null ? null : UnityEditors.RunEditModeTests(editor, project.Root, results),
            ResultFile = results,
            Summarize = _ => UnityProject.SummarizeTestResults(results),
            DisabledReason = noEditor ?? locked,
        });

        var method = project.SyncMethod(context.Settings.OpenSolutionsWith == SolutionOpener.Rider);
        actions.Add(new ProjectAction("regenerate-solution", "Regenerate the C# solution", ProjectActionKind.Run)
        {
            Description = method is null ? null : $"Runs {method} in batch mode.",
            Process = editor is null || method is null ? null : UnityEditors.SyncSolution(editor, project.Root, method),
            DisabledReason = noEditor ?? locked
                ?? (method is null ? "The project has no IDE package (com.jetbrains.rider or com.unity.ide.visualstudio) in Packages/manifest.json." : null),
        });

        var solution = Path.Combine(project.Root, $"{project.Name}.sln");
        actions.Add(new ProjectAction("open-solution", "Open solution", ProjectActionKind.Open)
        {
            Description = $"Opens {project.Name}.sln.",
            OpenPath = solution,
            OpenWithIde = true,
            DisabledReason = File.Exists(solution) ? null : "Regenerate the C# solution first",
        });

        var editorLog = UnityEditors.EditorLog(context.Paths, os);
        actions.Add(new ProjectAction("open-editor-log", "Open Editor log", ProjectActionKind.Open)
        {
            OpenPath = editorLog,
            DisabledReason = editorLog is not null && File.Exists(editorLog) ? null : "No Editor log yet.",
        });

        var playerLog = project.CompanyName is { } company && project.ProductName is { } product ? UnityEditors.PlayerLog(context.Paths, os, company, product) : null;
        actions.Add(new ProjectAction("open-player-log", "Open Player log", ProjectActionKind.Open)
        {
            Description = playerLog is null ? null : $"Opens the log a player build of {project.ProductName} writes.",
            OpenPath = playerLog,
            DisabledReason = playerLog is null ? "ProjectSettings.asset doesn't name the company and product."
                : File.Exists(playerLog) ? null : "No Player log yet: the game hasn't run as a player.",
        });

        var cleanable = CleanFolders(project.Root);
        actions.Add(new ProjectAction("clean-library", "Clean Library…", ProjectActionKind.Destructive)
        {
            Description = "Deletes Library, Temp and obj, so Unity rebuilds them.",
            Destructive = new DeleteFolders(project.Root, () => CleanFolders(project.Root),
                "Unity reimports every asset the next time it opens the project, which can take a long while.", EditorProcessNames, project.Root),
            DisabledReason = locked ?? (cleanable.Count == 0 ? "Nothing to clean: there's no Library, Temp or obj folder." : null),
        });

        var running = context.Processes?.Find(EditorProcessNames);
        actions.Add(new ProjectAction("kill-editors", "Kill all Unity editors…", ProjectActionKind.Destructive)
        {
            Description = "Ends every running Unity editor (not Unity Hub), with whatever each started.",
            Destructive = new KillProcesses(EditorProcessNames, "Unity editor", p => SystemProcessNames.FolderAfter(p.CommandLine, "-projectPath")),
            DisabledReason = running is { Count: 0 } ? "No Unity editor is running" : null,
        });

        var versionText = version is null ? "Unity" : $"Unity {version}";
        var details = new List<ProjectDetail>
        {
            new("Project", project.Root),
            new("Version", project.Revision is { } revision ? $"{versionText} ({revision})" : versionText),
            new("Editor", editor ?? problem ?? "Not found"),
            new("Optimization", optimization.ToString()),
        };
        if (project.ProductName is { } productName)
        {
            details.Add(new ProjectDetail("Product", project.CompanyName is { } companyName ? $"{productName} by {companyName}" : productName));
        }
        if (isOpen)
        {
            details.Add(new ProjectDetail("Open", "Unity has this project open."));
        }

        return new ProjectInfo
        {
            Kind = KindId,
            KindName = "Unity",
            Name = project.Name,
            Root = project.Root,
            ProjectPath = project.Root,
            ShortVersion = version is null ? "Unity" : $"Unity {ShortVersion(version)}",
            HeaderLines = problem is not null ? [problem] : [versionText, editor!],
            Details = details,
            Actions = actions,
            Choice = new ProjectChoice(OptimizationKey, "Code optimization",
                [new(nameof(UnityCodeOptimization.Release), "Release"), new(nameof(UnityCodeOptimization.Debug), "Debug")],
                optimization.ToString()),
            Fix = new ProjectFix(UnityEditors.EditorKey, editor is null ? "Choose Unity editor…" : "Choose another Unity editor…",
                $"Choose the Unity {version} editor for {project.Name}", PickFolder: false, picked => UnityEditors.Validate(picked, os)),
            Problem = problem,
            SystemPromptNote = context.Settings.TellClaudeAboutUnity ? SystemPromptNote(project, editor, os) : null,
        };
    }

    /// <summary>"2022.3" from "2022.3.20f1".</summary>
    public static string ShortVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
    }

    /// <summary>
    /// The editor has the project open: <c>Temp/UnityLockfile</c> exists and a Unity editor is running for it. A lock
    /// left by an editor that crashed doesn't count. Where processes can't be listed (or their command lines read),
    /// the lock file alone counts, since Unity refuses a locked project anyway.
    /// </summary>
    public static bool IsOpen(UnityProject project, ProjectToolContext context)
    {
        if (!File.Exists(Path.Combine(project.Root, "Temp", "UnityLockfile")))
        {
            return false;
        }
        if (context.Processes is not { } processes)
        {
            return true;
        }
        return processes.Find(EditorProcessNames).Any(p => p.CommandLine is null || SystemProcessNames.CommandLineMentions(p, project.Root));
    }

    /// <summary>What <b>Clean Library</b> deletes: <c>Library</c>, <c>Temp</c> and <c>obj</c> in the project, and nothing else.</summary>
    public static IReadOnlyList<string> CleanFolders(string root) =>
        ((string[])["Library", "Temp", "obj"]).Select(name => Path.Combine(root, name)).Where(Directory.Exists).ToArray();

    /// <summary>
    /// The note to Claude (DESIGN.md §18): the version and editor, how to run the EditMode tests in batch mode, which
    /// folders are generated, the <c>.meta</c> rule, and not to open the editor unasked.
    /// </summary>
    public static string SystemPromptNote(UnityProject project, string? editor, ToolOS os)
    {
        var version = project.EditorVersion is { } v ? $"Unity {v}" : "Unity";
        var lines = new List<string> { $"This is a {version} project, {project.Name}, at {project.Root}." };
        if (editor is null)
        {
            lines.Add("Its editor wasn't found on this machine.");
        }
        else
        {
            lines.Add($"The editor is at {editor}.");
            var results = os.Join(project.Root, "Logs", "EditModeResults.xml");
            lines.Add($"To run the EditMode tests, with the editor closed, run: {CommandLines.Display(UnityEditors.RunEditModeTests(editor, project.Root, results))}");
        }
        lines.Add("Library/, Temp/ and obj/ are generated: don't edit them.");
        lines.Add("A .meta file must move and be renamed with its asset.");
        lines.Add("Don't open the editor unless asked.");
        return string.Join('\n', lines);
    }

    private static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant())))[..12];
}
