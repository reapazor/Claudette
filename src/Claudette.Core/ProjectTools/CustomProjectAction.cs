namespace Claudette.Core.ProjectTools;

/// <summary>How a custom action runs: <c>"mode": "output"</c> or <c>"launch"</c> in <c>claudette.json</c>.</summary>
public enum CustomActionMode
{
    /// <summary>Output on the Project page, Stop, and a notification when it ends.</summary>
    RunWithOutput,

    /// <summary>Started and left alone, like the editor.</summary>
    LaunchAndForget,
}

/// <summary>Which of a folder's two project files something comes from (DESIGN.md §18, "claudette.json").</summary>
public enum ProjectFileScope
{
    /// <summary><c>claudette.json</c>: committed with the project and shared with everyone who works on it.</summary>
    Shared,

    /// <summary><c>claudette.local.json</c>: the user's own, usually gitignored.</summary>
    Local,
}

/// <summary>
/// An action from a folder's <c>claudette.json</c> or <c>claudette.local.json</c> (DESIGN.md §18, "Custom actions"):
/// a shell command, run in the tab's folder or a folder under it.
/// </summary>
public sealed class CustomProjectAction
{
    /// <summary>Stable while the file doesn't change: its scope and position, such as <c>shared:0</c>.</summary>
    public string Id { get; set; } = "";

    public ProjectFileScope Scope { get; set; } = ProjectFileScope.Local;

    public string Name { get; set; } = "";

    /// <summary>A command line for the user's shell: <c>cmd.exe</c> on Windows, <c>$SHELL</c> elsewhere.</summary>
    public string Command { get; set; } = "";

    /// <summary>Relative to the tab's folder (<c>"folder"</c> in the file); null or empty runs in the tab's folder.</summary>
    public string? WorkingFolder { get; set; }

    public CustomActionMode Mode { get; set; } = CustomActionMode.RunWithOutput;

    /// <summary>The OSes it's for (<c>"os"</c>: <c>windows</c>, <c>macos</c>, <c>linux</c>); null for all of them.</summary>
    public IReadOnlyList<string>? Os { get; set; }

    public CustomProjectAction Clone() => (CustomProjectAction)MemberwiseClone();

    /// <summary>The folder it runs in: <see cref="WorkingFolder"/> under <paramref name="tabFolder"/>.</summary>
    public string WorkingDirectory(string tabFolder) =>
        string.IsNullOrWhiteSpace(WorkingFolder) ? tabFolder : Path.GetFullPath(Path.Combine(tabFolder, WorkingFolder.Trim()));

    /// <summary>The action to run, for the chip menu and the Project page. Its id is <c>custom:&lt;scope&gt;:&lt;position&gt;</c>.</summary>
    public ProjectAction ToAction(string tabFolder, ToolOS os, string? shell)
    {
        var kind = Mode == CustomActionMode.LaunchAndForget ? ProjectActionKind.Launch : ProjectActionKind.Run;
        var label = Name.Trim().Length > 0 ? Name.Trim() : Command.Trim();
        var id = $"custom:{Id}";
        var file = Scope == ProjectFileScope.Shared ? ProjectFile.SharedName : ProjectFile.LocalName;
        try
        {
            var folder = WorkingDirectory(tabFolder);
            var spec = CommandLines.ShellCommand(Command.Trim(), os, shell, folder) with { Detached = kind == ProjectActionKind.Launch };
            return new ProjectAction(id, label, kind)
            {
                Description = $"{Command.Trim()}  ({file})",
                Process = spec,
                DisabledReason = Command.Trim().Length == 0 ? $"The action has no command. Edit it in {file}."
                    : !Directory.Exists(folder) ? $"Its folder, {folder}, doesn't exist."
                    : null,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ProjectAction(id, label, kind) { Description = Command, DisabledReason = ex.Message };
        }
    }
}

/// <summary>A link from a folder's project files, shown in the sidebar for the selected tab (DESIGN.md §18, "Links").</summary>
/// <param name="Url">The address, with placeholders such as <c>{branch}</c> still in it.</param>
public sealed record ProjectLink(string Name, string Url, ProjectFileScope Scope);
