using Claudette.Core.Processes;

namespace Claudette.Core.ProjectTools;

/// <summary>What running a project action does (DESIGN.md §18, "Project tools").</summary>
public enum ProjectActionKind
{
    /// <summary>Starts a program that outlives Claudette, such as the editor. Nothing is shown of its output.</summary>
    Launch,

    /// <summary>A long job: its output goes to the Project page, it can be stopped, and its end is notified.</summary>
    Run,

    /// <summary>Opens a file or folder with the OS, or with the app chosen in Settings → Project tools.</summary>
    Open,

    /// <summary>Changes things that can't be undone, such as deleting folders: always confirmed first.</summary>
    Destructive,
}

/// <summary>
/// One thing a tab can do for its project: launch the editor, generate project files, run a build (DESIGN.md §18).
/// Built fresh each time the project is detected, so whether it's enabled reflects the files as they are.
/// </summary>
public sealed record ProjectAction(string Id, string Label, ProjectActionKind Kind)
{
    /// <summary>A line of explanation, for the tooltip.</summary>
    public string? Description { get; init; }

    /// <summary>The program to start, for <see cref="ProjectActionKind.Launch"/> and <see cref="ProjectActionKind.Run"/>.</summary>
    public ProcessStartSpec? Process { get; init; }

    /// <summary>The file or folder for <see cref="ProjectActionKind.Open"/>.</summary>
    public string? OpenPath { get; init; }

    /// <summary>Open with the IDE chosen in Settings → Project tools → Open solutions with, rather than the OS's app.</summary>
    public bool OpenWithIde { get; init; }

    /// <summary>
    /// What to say, instead of opening the path with the OS's app, when <see cref="OpenWithIde"/> can't find or start
    /// the IDE; null falls back to the OS's app. <b>Open in Rider</b> sets it: the OS's app for a <c>.uproject</c> starts
    /// the Unreal editor, which isn't what was asked for.
    /// </summary>
    public string? WithoutIde { get; init; }

    /// <summary>What a <see cref="ProjectActionKind.Destructive"/> action does, once confirmed.</summary>
    public DestructiveWork? Destructive { get; init; }

    /// <summary>Run after this one succeeds, such as Launch editor after Build editor ("Build and launch").</summary>
    public ProjectAction? ThenOnSuccess { get; init; }

    /// <summary>
    /// Variables added to its process's environment, such as a new worktree's and its main checkout's (DESIGN.md §18,
    /// "Setting up a new worktree").
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }

    /// <summary>Why it can't run now, such as "Generate project files first"; null when it can. Shown as the tooltip.</summary>
    public string? DisabledReason { get; init; }

    public bool IsEnabled => DisabledReason is null;

    /// <summary>The action "Run the project's main action" (Ctrl/Cmd+Shift+E) runs, such as Launch editor.</summary>
    public bool IsMain { get; init; }

    /// <summary>Reads what a finished <see cref="ProjectActionKind.Run"/> job produced, for its status line.</summary>
    public Func<int, string?>? Summarize { get; init; }

    /// <summary>
    /// A file the job writes for <see cref="Summarize"/> to read, such as Unity's test results. It's deleted before the
    /// job starts, so an old one is never read, and its folder is made.
    /// </summary>
    public string? ResultFile { get; init; }

    /// <summary>The command line, for the Project page and tooltips.</summary>
    public string? CommandText => Process is { } spec ? CommandLines.Display(spec) : null;

    /// <summary>The tooltip: why it can't run, else what it does.</summary>
    public string? Tip => DisabledReason ?? Description ?? CommandText;
}

/// <summary>What a destructive action does, once confirmed.</summary>
public abstract record DestructiveWork;

/// <summary>Deletes folders, such as Unreal's Binaries and Intermediate. Only the ones that exist are listed and deleted.</summary>
/// <param name="Root">The project's folder: the confirmation lists the folders relative to it.</param>
/// <param name="Folders">Finds the folders to delete, when the action is clicked, so the list is current.</param>
/// <param name="Warning">Said in the confirmation, such as "Unity reimports every asset next time".</param>
/// <param name="EditorProcessNames">
/// When a process with one of these names has the project's file on its command line, the confirmation warns that the
/// editor seems to have the project open.
/// </param>
public sealed record DeleteFolders(string Root, Func<IReadOnlyList<string>> Folders, string? Warning = null, IReadOnlyList<string>? EditorProcessNames = null, string? ProjectFile = null)
    : DestructiveWork;

/// <summary>Ends every running process with one of these names, and each one's process tree (<b>Kill all editors</b>).</summary>
/// <param name="What">"Unreal editor", for the confirmation: "End 2 Unreal editors?".</param>
/// <param name="ProjectOf">The project a process has open, read from its command line, for the confirmation; null when it can't tell.</param>
public sealed record KillProcesses(IReadOnlyList<string> Names, string What, Func<SystemProcess, string?>? ProjectOf = null) : DestructiveWork;

/// <summary>A row of the project's details: on the Project page and in the chip menu's header.</summary>
public sealed record ProjectDetail(string Label, string Value);

/// <summary>One option of a <see cref="ProjectChoice"/>.</summary>
public sealed record ProjectChoiceOption(string Value, string Label);

/// <summary>
/// A per-project choice shown as a radio pair in the chip menu, such as Unreal's editor configuration (Development or
/// DebugGame). Saved on this machine, keyed by the project's file.
/// </summary>
/// <param name="Key">Where the choice is kept in the project's memory.</param>
public sealed record ProjectChoice(string Key, string Label, IReadOnlyList<ProjectChoiceOption> Options, string Selected);

/// <summary>What to pick when something the project needs wasn't found, such as <b>Choose engine folder…</b>.</summary>
/// <param name="PickFolder">A folder rather than a file.</param>
/// <param name="Validate">Checks the pick: returns the path to remember, or null and the reason it can't be used.</param>
public sealed record ProjectFix(string Key, string Label, string Title, bool PickFolder, Func<string, (string? Path, string? Error)> Validate)
{
    /// <summary>What's picked, for Settings' Tools page: "Engine folder", "Unity editor", "Godot executable".</summary>
    public string Name { get; init; } = "";

    /// <summary>The folder or program in use now, chosen or found; null when none was found.</summary>
    public string? Current { get; init; }
}

/// <summary>A project found for a tab's folder, before it's read.</summary>
/// <param name="Path">The project's file (a <c>.uproject</c>) or folder: its identity for what's remembered about it.</param>
/// <param name="Kind">The provider that found it.</param>
public sealed record ProjectCandidate(string Kind, string Path, string Name);

/// <summary>A detected project and what can be done with it (DESIGN.md §18).</summary>
public sealed record ProjectInfo
{
    public required string Kind { get; init; }

    /// <summary>"Unreal Engine", for the Project page and notes.</summary>
    public required string KindName { get; init; }

    public required string Name { get; init; }

    /// <summary>The folder the project lives in.</summary>
    public required string Root { get; init; }

    /// <summary>The project's file or folder, as found: its identity for what's remembered.</summary>
    public required string ProjectPath { get; init; }

    /// <summary>A short version for the chip, such as "UE 5.4"; null when unknown.</summary>
    public string? ShortVersion { get; init; }

    /// <summary>Lines under the project's name in the chip menu, such as the engine's folder and kind.</summary>
    public IReadOnlyList<string> HeaderLines { get; init; } = [];

    /// <summary>What the Project page lists.</summary>
    public IReadOnlyList<ProjectDetail> Details { get; init; } = [];

    public IReadOnlyList<ProjectAction> Actions { get; init; } = [];

    /// <summary>A per-project choice such as the editor configuration; null when the project has none.</summary>
    public ProjectChoice? Choice { get; init; }

    /// <summary>Offered in the chip menu, such as <b>Choose engine folder…</b>.</summary>
    public ProjectFix? Fix { get; init; }

    /// <summary>What's wrong, such as "The engine wasn't found", shown in the chip menu's header.</summary>
    public string? Problem { get; init; }

    /// <summary>The note added to Claude's system prompt, or null when Settings says not to (DESIGN.md §18).</summary>
    public string? SystemPromptNote { get; init; }

    /// <summary>The action "Run the project's main action" runs.</summary>
    public ProjectAction? MainAction => Actions.FirstOrDefault(a => a.IsMain);
}

/// <summary>What detection found for a folder: every project it could be, and the one in use.</summary>
public sealed record ProjectDetection(IReadOnlyList<ProjectCandidate> Candidates, ProjectInfo? Project)
{
    public static ProjectDetection None { get; } = new([], null);
}
