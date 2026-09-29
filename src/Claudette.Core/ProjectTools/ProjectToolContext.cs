using Claudette.Core.Diffs;
using Claudette.Core.ProjectTools.Unreal;
using Claudette.Core.Settings;

namespace Claudette.Core.ProjectTools;

/// <summary>
/// Finds one kind of project for a tab's folder, and says what can be done with it (DESIGN.md §18, "Project tools").
/// Unreal Engine is the first provider. Both methods run off the UI thread and must be cheap: bounded walks, small
/// files, nothing started.
/// </summary>
public interface IProjectToolProvider
{
    /// <summary>A stable id, such as <c>unreal</c>.</summary>
    string Kind { get; }

    /// <summary>The projects this provider finds for <paramref name="folder"/>, nearest first.</summary>
    IReadOnlyList<ProjectCandidate> Find(string folder, ProjectToolContext context);

    /// <summary>Reads a project and builds its actions. Never throws for a broken project: <see cref="ProjectInfo.Problem"/> says what's wrong.</summary>
    ProjectInfo Describe(ProjectCandidate candidate, ProjectToolContext context);
}

/// <summary>Where other programs keep their files, injected so tests use temporary folders (DESIGN.md §15).</summary>
/// <param name="Home">The user's home folder: <c>~</c>.</param>
/// <param name="AppData"><c>%APPDATA%</c> on Windows.</param>
/// <param name="LocalAppData"><c>%LOCALAPPDATA%</c> on Windows.</param>
/// <param name="ProgramData"><c>%ProgramData%</c> on Windows, where the Epic launcher lists its installs.</param>
/// <param name="ProgramFiles"><c>%ProgramFiles%</c> on Windows.</param>
public sealed record ProjectToolPaths(string Home, string? AppData = null, string? LocalAppData = null, string? ProgramData = null, string? ProgramFiles = null)
{
    public static ProjectToolPaths ForCurrentUser()
    {
        static string? Folder(Environment.SpecialFolder folder)
        {
            var path = Environment.GetFolderPath(folder);
            return path.Length > 0 ? path : null;
        }
        return new ProjectToolPaths(
            Folder(Environment.SpecialFolder.UserProfile) ?? "",
            Folder(Environment.SpecialFolder.ApplicationData),
            Folder(Environment.SpecialFolder.LocalApplicationData),
            Folder(Environment.SpecialFolder.CommonApplicationData),
            Folder(Environment.SpecialFolder.ProgramFiles));
    }
}

/// <summary>What's remembered about a project on this machine: its configuration choice, a chosen engine folder.</summary>
public interface IProjectMemory
{
    /// <summary>The value kept under <paramref name="key"/> for the project at <paramref name="projectPath"/>, or null.</summary>
    string? Get(string projectPath, string key);
}

/// <summary>A memory that remembers nothing.</summary>
public sealed class NoProjectMemory : IProjectMemory
{
    public static NoProjectMemory Instance { get; } = new();

    public string? Get(string projectPath, string key) => null;
}

/// <summary>Everything a provider needs besides the folder: the OS, the settings, other programs' files, the machine's memory.</summary>
public sealed class ProjectToolContext
{
    public required ToolOS OS { get; init; }

    public required ProjectToolSettings Settings { get; init; }

    public required ProjectToolPaths Paths { get; init; }

    public IProjectMemory Memory { get; init; } = NoProjectMemory.Instance;

    /// <summary>The Windows registry's Unreal entries; nothing elsewhere.</summary>
    public IUnrealEngineRegistry UnrealRegistry { get; init; } = NoUnrealEngineRegistry.Instance;

    /// <summary>Running processes, to tell whether an editor has the project open. Null can't tell, and doesn't guess.</summary>
    public ISystemProcesses? Processes { get; init; }

    public IFileProbe Probe { get; init; } = FileProbe.Instance;

    /// <summary>The user's shell (<c>$SHELL</c>) for custom actions on macOS and Linux; null uses <c>/bin/sh</c>.</summary>
    public string? Shell { get; init; }

    /// <summary>The name of Perforce's config file (<c>P4CONFIG</c>), which marks a workspace's root like <c>.git</c>.</summary>
    public string? P4ConfigName { get; init; }
}
