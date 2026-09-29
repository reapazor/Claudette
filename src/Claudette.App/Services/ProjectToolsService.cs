using Claudette.Core.Diffs;
using Claudette.Core.Processes;
using Claudette.Core.ProjectTools;
using Claudette.Core.ProjectTools.Unreal;
using Claudette.Core.Settings;

namespace Claudette.App.Services;

/// <summary>
/// What project tools share between tabs (DESIGN.md §18, "Project tools"): the providers, other programs' folders, the
/// registry and running processes, and starting the actions' processes with the right environment.
/// </summary>
public sealed class ProjectToolsService(AppServices services, ISystemProcesses? processes, IUnrealEngineRegistry registry, ProjectToolPaths paths)
{
    public ProjectToolDetector Detector { get; } = new([new UnrealProvider(), new Core.ProjectTools.Unity.UnityProvider(), new Core.ProjectTools.Godot.GodotProvider()]);

    /// <summary>Running processes by name; null when this machine can't tell.</summary>
    public ISystemProcesses? Processes { get; internal set; } = processes;

    public ProjectToolPaths Paths { get; internal set; } = paths;

    public IUnrealEngineRegistry Registry { get; internal set; } = registry;

    /// <summary>The OS actions are built for. Tests can build another OS's actions.</summary>
    public ToolOS OS { get; internal set; } = ToolOSExtensions.Current;

    /// <summary>Looks for IDEs for <b>Open solution</b>. Tests replace it.</summary>
    internal IFileProbe Probe { get; set; } = FileProbe.Instance;

    public ProjectToolSettings Settings => services.Settings.ProjectTools;

    public ProjectToolState State => services.State.ProjectTools;

    /// <summary>The user's shell for custom actions on macOS and Linux.</summary>
    internal string? Shell { get; set; } = ProjectToolEnvironment.UserShell();

    /// <summary>What providers need, with a copy of the machine's memory so detection can read it off the UI thread.</summary>
    public ProjectToolContext Context() => new()
    {
        OS = OS,
        Settings = Settings,
        Paths = Paths,
        Memory = State.Snapshot(),
        UnrealRegistry = Registry,
        Processes = Processes,
        Probe = Probe,
        Shell = Shell,
        P4ConfigName = Environment.GetEnvironmentVariable("P4CONFIG"),
        JobsDirectory = services.Paths.ProjectJobsDirectory,
    };

    /// <summary>Finds the project for <paramref name="folder"/>, off the UI thread.</summary>
    public Task<ProjectDetection> DetectAsync(string folder)
    {
        var context = Context();
        var chosen = State.ChosenProjectFor(folder);
        return Task.Run(() => Detector.Detect(folder, context, chosen));
    }

    /// <summary>The folder's <c>claudette.json</c> and <c>claudette.local.json</c>, off the UI thread.</summary>
    public Task<ProjectFileContents> ReadProjectFileAsync(string folder)
    {
        var os = OS;
        return Task.Run(() => ProjectFile.Read(folder, os));
    }

    /// <summary>The folder's custom actions, ready to run.</summary>
    public IReadOnlyList<(CustomProjectAction Custom, ProjectAction Action)> CustomActions(ProjectFileContents contents, string folder) =>
        contents.Actions.Select(a => (a, a.ToAction(folder, OS, Shell))).ToArray();

    /// <summary>
    /// Whether the user trusts the folder's shared actions as they are now (DESIGN.md §18, "Trust"). The local file's
    /// actions are the user's own and never need it.
    /// </summary>
    public bool IsTrusted(string folder, ProjectFileContents contents) => State.IsTrusted(folder, ProjectFile.Hash(contents.SharedActions));

    public void Trust(string folder, ProjectFileContents contents)
    {
        State.Trust(folder, ProjectFile.Hash(contents.SharedActions));
        services.SaveState();
    }

    /// <summary>
    /// Starts a program that outlives Claudette, such as the editor. The returned process is dropped rather than
    /// disposed: disposing it would end it.
    /// </summary>
    public void Launch(ProcessStartSpec spec) =>
        _ = services.Launcher.Start(ProjectToolEnvironment.Apply(spec with { Detached = true }));

    /// <summary>Starts a long job, its process tree tracked so it shows in the process monitor and Stop ends all of it.</summary>
    public ProjectJob StartJob(string name, ProcessStartSpec spec) =>
        ProjectJob.Start(name, services.Launcher, ProjectToolEnvironment.Apply(spec with { TrackProcessTree = true, Detached = false }));

    /// <summary>How <b>Open solution</b> opens <paramref name="path"/>, as Settings → Project tools says.</summary>
    public SolutionOpening Opener(string path) =>
        SolutionOpeners.For(Settings.OpenSolutionsWith, path, OS, Paths, Probe, Settings.CustomIdePath);

    /// <summary>Remembers a choice for a project, such as its editor configuration or engine folder.</summary>
    public void Remember(string projectPath, string key, string? value)
    {
        State.Set(projectPath, key, value);
        services.SaveState();
    }
}
