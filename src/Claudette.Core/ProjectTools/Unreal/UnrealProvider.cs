using Claudette.Core.Settings;

namespace Claudette.Core.ProjectTools.Unreal;

/// <summary>
/// Unreal Engine projects (DESIGN.md §18, "Project tools"): finds <c>*.uproject</c> files for a tab's folder, the
/// engine each uses, and builds the editor, project file, build, open and clean actions.
/// </summary>
public sealed class UnrealProvider : IProjectToolProvider
{
    public const string KindId = "unreal";

    /// <summary>Where a project's editor configuration is kept in its memory.</summary>
    public const string ConfigurationKey = "configuration";

    /// <summary>The editor's process names, UE5's and UE4's, for the clean warning and Kill all editors.</summary>
    public static readonly IReadOnlyList<string> EditorProcessNames = ["UnrealEditor", "UnrealEditor-Cmd", "UE4Editor", "UE4Editor-Cmd"];

    public string Kind => KindId;

    public IReadOnlyList<ProjectCandidate> Find(string folder, ProjectToolContext context) =>
        ProjectSearch.Find(folder, dir => ProjectSearch.FilesIn(dir, "*.uproject"), context)
            .Select(path => new ProjectCandidate(KindId, path, Path.GetFileNameWithoutExtension(path)))
            .ToArray();

    public ProjectInfo Describe(ProjectCandidate candidate, ProjectToolContext context)
    {
        var os = context.OS;
        var project = UnrealProject.Read(candidate.Path);
        var engine = UnrealEngineLocator.Find(project, context);
        var configuration = Enum.TryParse<UnrealConfiguration>(context.Memory.Get(project.Path, ConfigurationKey), out var remembered)
            ? remembered
            : context.Settings.UnrealConfiguration;
        var format = context.Settings.FormatFor(os);
        var version = engine?.Version?.Short ?? (project.EngineAssociation is { } association && UnrealAssociation.IsVersion(association) ? association : null);
        var debugGame = configuration == UnrealConfiguration.DebugGame;
        var suffix = debugGame ? " (DebugGame)" : "";

        string? problem = null;
        if (engine is null)
        {
            problem = project.EngineAssociation is { } wanted
                ? $"The engine for EngineAssociation \"{wanted}\" wasn't found on this machine."
                : "No engine was found in a folder above the project.";
        }
        var noEngine = engine is null ? "The engine wasn't found: choose its folder in this menu." : null;

        var actions = new List<ProjectAction>();
        ProjectAction? launch = null;
        if (engine is not null)
        {
            var launchSpec = UnrealCommands.LaunchEditor(engine.Root, engine.EditorName, project.Path, configuration, os);
            launch = new ProjectAction("launch-editor", $"Launch editor{suffix}", ProjectActionKind.Launch)
            {
                Description = debugGame ? "Opens the project in the editor, loading its DebugGame modules (-debug)." : "Opens the project in the editor.",
                Process = launchSpec,
                IsMain = true,
                DisabledReason = File.Exists(launchSpec.FileName) ? null : $"The editor isn't built yet ({Path.GetFileName(launchSpec.FileName)} is missing): run Build editor first.",
            };
        }
        actions.Add(launch ?? new ProjectAction("launch-editor", $"Launch editor{suffix}", ProjectActionKind.Launch) { IsMain = true, DisabledReason = noEngine });

        var scriptMissing = engine is not null && !File.Exists(UnrealCommands.BuildScript(engine.Root, os))
            ? $"The engine has no {Path.GetFileName(UnrealCommands.BuildScript(engine.Root, os))}."
            : null;
        // Rider reads the .uproject itself, keeping its own project model, so it needs no project files (issue #6).
        var rider = context.Settings.OpenSolutionsWith == SolutionOpener.Rider && RiderReadsUProject(engine?.Version, os);
        actions.Add(new ProjectAction("generate-project-files", "Generate project files", ProjectActionKind.Run)
        {
            Description = $"Generates the {FormatName(format)} project files, as the .uproject's menu does."
                + (rider ? " Rider doesn't need them: Open in Rider reads the .uproject itself." : ""),
            Process = engine is null ? null : UnrealCommands.GenerateProjectFiles(engine.Root, project.Path, format, os),
            DisabledReason = noEngine ?? scriptMissing ?? (project.HasCode ? null : "The project has no C++ code, so there are no project files to generate."),
        });

        var target = project.HasCode ? project.EditorTarget : engine?.EditorName ?? "UnrealEditor";
        var cantBuild = noEngine ?? scriptMissing
            ?? (!project.HasCode && engine is { IsInstalledBuild: true } ? "The project has no C++ code, and an installed engine's editor is already built." : null);
        var build = new ProjectAction("build-editor", $"Build editor{suffix}", ProjectActionKind.Run)
        {
            Description = $"Builds {target} for {UnrealCommands.Platform(os)} {configuration}.",
            Process = engine is null ? null : UnrealCommands.BuildEditor(engine.Root, target, project.Path, configuration, os),
            DisabledReason = cantBuild,
        };
        actions.Add(build);
        actions.Add(build with
        {
            Id = "build-and-launch",
            Label = $"Build and launch{suffix}",
            Description = "Builds the editor, then launches it if the build succeeds.",
            ThenOnSuccess = launch is { Process: not null } ? launch with { DisabledReason = null } : null,
            DisabledReason = cantBuild ?? (launch is null ? noEngine : null),
        });

        if (rider)
        {
            // Rider's own Unreal project model: nothing to generate first, on any OS. The .sln, for the engine programs and
            // mobile targets Rider's .uproject model doesn't cover yet, is in Rider's own File → Open.
            actions.Add(new ProjectAction("open-solution", "Open in Rider", ProjectActionKind.Open)
            {
                Description = $"Opens {Path.GetFileName(project.Path)} in Rider, which reads the project itself: no project files to generate.",
                OpenPath = project.Path,
                OpenWithIde = true,
                WithoutIde = "Rider wasn't found, so the project wasn't opened: the OS's app would start the Unreal editor instead. "
                    + "If Rider is installed somewhere Claudette doesn't look, choose it with Another program… in Settings → Project tools.",
            });
        }
        else
        {
            var solution = UnrealCommands.SolutionCandidates(project.Root, project.Name, format, os).FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
            actions.Add(new ProjectAction("open-solution", "Open solution", ProjectActionKind.Open)
            {
                Description = solution is null ? null : $"Opens {Path.GetFileName(solution)}.",
                OpenPath = solution,
                OpenWithIde = true,
                DisabledReason = solution is null ? "Generate project files first" : null,
            });
        }

        var log = UnrealCommands.LogPath(project.Root, project.Name, os);
        actions.Add(new ProjectAction("open-log", "Open latest log", ProjectActionKind.Open)
        {
            Description = $"Opens Saved/Logs/{project.Name}.log.",
            OpenPath = log,
            DisabledReason = File.Exists(log) ? null : "No log yet: the editor hasn't run for this project.",
        });

        var cleanable = CleanFolders(project.Root);
        actions.Add(new ProjectAction("clean", "Clean intermediates…", ProjectActionKind.Destructive)
        {
            Description = "Deletes Binaries and Intermediate in the project and each of its plugins, so the next build starts from scratch.",
            Destructive = new DeleteFolders(project.Root, () => CleanFolders(project.Root), EditorProcessNames: EditorProcessNames, ProjectFile: project.Path),
            DisabledReason = cleanable.Count == 0 ? "Nothing to clean: there are no Binaries or Intermediate folders." : null,
        });

        // Every Unreal editor, not just this project's: a stuck one, or several, often need ending (DESIGN.md §18).
        var running = context.Processes?.Find(EditorProcessNames);
        actions.Add(new ProjectAction("kill-editors", "Kill all Unreal editors…", ProjectActionKind.Destructive)
        {
            Description = "Ends every running Unreal editor, with ShaderCompileWorker and whatever else each started.",
            Destructive = new KillProcesses(EditorProcessNames, "Unreal editor", p => SystemProcessNames.FileArgument(p.CommandLine, ".uproject")),
            DisabledReason = running is { Count: 0 } ? "No Unreal editor is running" : null,
        });

        var details = new List<ProjectDetail>
        {
            new("Project", project.Path),
            new("Engine", engine is null ? problem! : $"{EngineVersionText(engine, project)} · {engine.Root} ({engine.KindText})"),
            new("Editor target", project.HasCode ? project.EditorTarget : $"{target} (the project has no C++ code)"),
            new("Configuration", configuration.ToString()),
        };
        if (project.EnabledPlugins.Count > 0)
        {
            details.Add(new ProjectDetail("Plugins", string.Join(", ", project.EnabledPlugins)));
        }

        return new ProjectInfo
        {
            Kind = KindId,
            KindName = "Unreal Engine",
            Name = project.Name,
            Root = project.Root,
            ProjectPath = project.Path,
            ShortVersion = version is null ? "UE" : $"UE {version}",
            HeaderLines = engine is null ? [problem!] : [$"{EngineVersionText(engine, project)} · {engine.KindText}", engine.Root],
            Details = details,
            Actions = actions,
            Choice = new ProjectChoice(ConfigurationKey, "Editor configuration",
                [new(nameof(UnrealConfiguration.Development), "Development"), new(nameof(UnrealConfiguration.DebugGame), "DebugGame")],
                configuration.ToString()),
            Fix = new ProjectFix(UnrealEngineLocator.EngineKey, engine is null ? "Choose engine folder…" : "Choose another engine folder…",
                $"Choose the Unreal Engine folder for {project.Name}", PickFolder: true, ValidateEngineFolder)
            {
                Name = "Engine folder",
                Current = engine?.Root,
            },
            Problem = problem,
            SystemPromptNote = context.Settings.TellClaudeAboutUnreal ? SystemPromptNote(project, engine, version, configuration, format, os) : null,
        };
    }

    /// <summary>"Unreal Engine 5.4.2", from Build.version, else the association.</summary>
    private static string EngineVersionText(UnrealEngine engine, UnrealProject project) =>
        engine.Version is { } v ? $"Unreal Engine {v.Full}" : project.EngineAssociation is { } a && UnrealAssociation.IsVersion(a) ? $"Unreal Engine {a}" : "Unreal Engine";

    /// <summary>The folder a user picked for <b>Choose engine folder…</b>: it has to hold <c>Engine/Build/Build.version</c>.</summary>
    public static (string? Path, string? Error) ValidateEngineFolder(string folder) =>
        UnrealEngineLocator.Engine(folder, UnrealEngineKind.Chosen) is { } engine
            ? (engine.Root, null)
            : (null, $"{folder} isn't an Unreal Engine folder: it has no Engine/Build/Build.version.");

    /// <summary>
    /// Whether Rider can open the engine's projects by their <c>.uproject</c>, without project files: Unreal Engine
    /// 4.25.4 or later on Windows, 4.26 or later on macOS and Linux. An engine whose version isn't known is taken to be
    /// new enough; an older one opens the generated solution instead.
    /// </summary>
    public static bool RiderReadsUProject(UnrealBuildVersion? version, ToolOS os) =>
        version is null || (os == ToolOS.Windows
            ? (version.Major, version.Minor, version.Patch).CompareTo((4, 25, 4)) >= 0
            : (version.Major, version.Minor).CompareTo((4, 26)) >= 0);

    public static string FormatName(ProjectFileFormat format) => format switch
    {
        ProjectFileFormat.VSCode => "VS Code",
        ProjectFileFormat.Xcode => "Xcode",
        _ => "Visual Studio",
    };

    /// <summary>
    /// What <b>Clean intermediates</b> deletes: <c>Binaries</c> and <c>Intermediate</c> in the project, and in each
    /// plugin under <c>Plugins/</c> (a folder with a <c>.uplugin</c>, at any depth). Nothing else: not <c>Saved</c>,
    /// <c>DerivedDataCache</c>, <c>Content</c> or <c>Config</c>. Only folders that exist are listed.
    /// </summary>
    public static IReadOnlyList<string> CleanFolders(string root)
    {
        var folders = new List<string>();
        void AddOutputs(string folder)
        {
            foreach (var name in (string[])["Binaries", "Intermediate"])
            {
                var path = Path.Combine(folder, name);
                if (Directory.Exists(path))
                {
                    folders.Add(path);
                }
            }
        }
        AddOutputs(root);
        var plugins = Path.Combine(root, "Plugins");
        if (Directory.Exists(plugins))
        {
            Walk(plugins, 0);
        }
        return folders;

        void Walk(string folder, int depth)
        {
            if (depth > 8)
            {
                return;
            }
            if (ProjectSearch.FilesIn(folder, "*.uplugin").Count > 0)
            {
                AddOutputs(folder);
            }
            string[] children;
            try
            {
                children = Directory.EnumerateDirectories(folder)
                    .Where(d => !PluginSkipped.Contains(Path.GetFileName(d)) && !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            foreach (var child in children)
            {
                Walk(child, depth + 1);
            }
        }
    }

    /// <summary>Folders inside Plugins that never hold another plugin.</summary>
    private static readonly IReadOnlySet<string> PluginSkipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Binaries", "Intermediate", "Content", "Source", "Resources", "Config", "Saved", "DerivedDataCache", "Shaders", ".git",
    };

    /// <summary>
    /// The note added to Claude's system prompt (DESIGN.md §18): what the project is, where its engine is, the exact
    /// commands to build the editor and regenerate project files, and not to start the editor or packaging unasked.
    /// </summary>
    public static string SystemPromptNote(UnrealProject project, UnrealEngine? engine, string? version, UnrealConfiguration configuration, ProjectFileFormat format, ToolOS os)
    {
        var lines = new List<string>
        {
            $"This is an Unreal Engine {version ?? ""} project, {project.Name}, at {project.Path}.".Replace("Engine  project", "Engine project", StringComparison.Ordinal),
        };
        if (engine is null)
        {
            lines.Add("Its engine wasn't found on this machine.");
        }
        else
        {
            lines.Add($"The engine is at {engine.Root} ({engine.KindForNote}).");
            if (project.HasCode || !engine.IsInstalledBuild)
            {
                var target = project.HasCode ? project.EditorTarget : engine.EditorName;
                lines.Add($"To build the editor, run: {CommandLines.Display(UnrealCommands.BuildEditor(engine.Root, target, project.Path, configuration, os))}");
            }
            if (project.HasCode)
            {
                lines.Add($"To regenerate project files, run: {CommandLines.Display(UnrealCommands.GenerateProjectFiles(engine.Root, project.Path, format, os))}");
            }
        }
        lines.Add("Don't start the editor or packaging unless asked.");
        return string.Join('\n', lines);
    }
}
