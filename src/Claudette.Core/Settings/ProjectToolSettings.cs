using Claudette.Core.ProjectTools;

namespace Claudette.Core.Settings;

/// <summary>Unreal's editor configurations: which module DLLs the editor loads (DESIGN.md §18, "Project tools").</summary>
public enum UnrealConfiguration
{
    Development,
    DebugGame,
}

/// <summary>The project files Unreal generates.</summary>
public enum ProjectFileFormat
{
    VisualStudio,
    VSCode,
    Xcode,
}

/// <summary>How Unity compiles scripts in the editor: Debug adds <c>-debugCodeOptimization</c> (DESIGN.md §18).</summary>
public enum UnityCodeOptimization
{
    Release,
    Debug,
}

/// <summary>What <b>Open solution</b> opens a solution or workspace with.</summary>
public enum SolutionOpener
{
    /// <summary>The app the OS uses for the file.</summary>
    System,
    Rider,
    VisualStudio,
    VSCode,
    /// <summary>A program the user chose (<see cref="ProjectToolSettings.CustomIdePath"/>).</summary>
    Custom,
}

/// <summary>
/// Settings → Project tools (DESIGN.md §18). They stay on each machine, like the diff tool: installed IDEs and
/// program paths differ between machines.
/// </summary>
public sealed class ProjectToolSettings
{
    /// <summary>The editor configuration a project uses until it's given its own in the chip menu.</summary>
    public UnrealConfiguration UnrealConfiguration { get; set; } = UnrealConfiguration.Development;

    /// <summary>Null means the OS's own: Visual Studio on Windows, Xcode on macOS, VS Code on Linux.</summary>
    public ProjectFileFormat? ProjectFileFormat { get; set; }

    public SolutionOpener OpenSolutionsWith { get; set; } = SolutionOpener.System;

    /// <summary>The program for <see cref="SolutionOpener.Custom"/>; it's given the solution's path.</summary>
    public string? CustomIdePath { get; set; }

    /// <summary>Add a note about a detected Unreal project to Claude's system prompt.</summary>
    public bool TellClaudeAboutUnreal { get; set; } = true;

    /// <summary>The code optimization a Unity project opens with until it's given its own in the chip menu.</summary>
    public UnityCodeOptimization UnityCodeOptimization { get; set; } = UnityCodeOptimization.Release;

    /// <summary>Add a note about a detected Unity project to Claude's system prompt.</summary>
    public bool TellClaudeAboutUnity { get; set; } = true;

    /// <summary>The Godot executable; null finds it (DESIGN.md §18, "Godot").</summary>
    public string? GodotPath { get; set; }

    /// <summary>Add a note about a detected Godot project to Claude's system prompt.</summary>
    public bool TellClaudeAboutGodot { get; set; } = true;

    /// <summary>The format in effect on <paramref name="os"/>.</summary>
    public ProjectFileFormat FormatFor(ToolOS os) => ProjectFileFormat ?? DefaultFormat(os);

    public static ProjectFileFormat DefaultFormat(ToolOS os) => os switch
    {
        ToolOS.Windows => Settings.ProjectFileFormat.VisualStudio,
        ToolOS.MacOS => Settings.ProjectFileFormat.Xcode,
        _ => Settings.ProjectFileFormat.VSCode,
    };
}
