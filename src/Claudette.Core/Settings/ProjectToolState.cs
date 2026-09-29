using Claudette.Core.ProjectTools;

namespace Claudette.Core.Settings;

/// <summary>
/// What project tools remember on this machine (DESIGN.md §18, "Project tools"), in <c>state.json</c>. Folders and
/// projects are kept by their full path; nothing here syncs. A folder's own actions and links are in its
/// <c>claudette.json</c> and <c>claudette.local.json</c>, not here.
/// </summary>
public sealed class ProjectToolState
{
    /// <summary>The project a folder uses when it has several, by folder.</summary>
    public List<ChosenProject> ChosenProjects { get; set; } = [];

    /// <summary>Each project's choices: its editor configuration, a chosen engine folder.</summary>
    public List<ProjectMemoryEntry> Projects { get; set; } = [];

    public string? ChosenProjectFor(string folder) =>
        ChosenProjects.FirstOrDefault(c => FolderHistory.SamePath(c.Folder, folder))?.Project;

    public void ChooseProject(string folder, string project)
    {
        ChosenProjects.RemoveAll(c => FolderHistory.SamePath(c.Folder, folder));
        ChosenProjects.Add(new ChosenProject { Folder = FolderHistory.Normalize(folder), Project = project });
    }

    /// <summary>A value remembered for a project, or null.</summary>
    public string? Get(string projectPath, string key) =>
        Projects.FirstOrDefault(p => SameFile(p.Path, projectPath))?.Values.GetValueOrDefault(key);

    /// <summary>Remembers a value for a project; null forgets it.</summary>
    public void Set(string projectPath, string key, string? value)
    {
        var entry = Projects.FirstOrDefault(p => SameFile(p.Path, projectPath));
        if (entry is null)
        {
            if (value is null)
            {
                return;
            }
            entry = new ProjectMemoryEntry { Path = projectPath };
            Projects.Add(entry);
        }
        if (value is null)
        {
            entry.Values.Remove(key);
            if (entry.Values.Count == 0)
            {
                Projects.Remove(entry);
            }
        }
        else
        {
            entry.Values[key] = value;
        }
    }

    /// <summary>A copy of what's remembered, for detection to read off the UI thread while the state may change.</summary>
    public IProjectMemory Snapshot()
    {
        var copy = new ProjectToolState
        {
            Projects = Projects.Select(p => new ProjectMemoryEntry { Path = p.Path, Values = new Dictionary<string, string>(p.Values, StringComparer.Ordinal) }).ToList(),
        };
        return new StateProjectMemory(copy);
    }

    private static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), ToolOSExtensions.Current.PathComparison());
}

public sealed class ChosenProject
{
    public string Folder { get; set; } = "";

    /// <summary>The project's file or folder, as its provider found it.</summary>
    public string Project { get; set; } = "";
}

/// <summary>What's remembered about one project, such as <c>configuration</c> and <c>engine</c>.</summary>
public sealed class ProjectMemoryEntry
{
    public string Path { get; set; } = "";

    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The machine's state as a provider sees it.</summary>
public sealed class StateProjectMemory(ProjectToolState state) : IProjectMemory
{
    public string? Get(string projectPath, string key) => state.Get(projectPath, key);
}
