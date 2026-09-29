using System.Text.Json.Nodes;
using Claudette.Core.Processes;
using Claudette.Core.Settings;

namespace Claudette.Core.ProjectTools.Unity;

/// <summary>
/// Finds the Unity editor for a project's version (DESIGN.md §18, "Unity"): a pick remembered for the project, then
/// Unity Hub's default folder, Hub's custom install folder (<c>secondaryInstallPath.json</c>), and editors added to Hub
/// by hand (<c>editors-v2.json</c>, <c>editors.json</c>). Only an editor that exists counts.
/// </summary>
public static class UnityEditors
{
    /// <summary>Where a chosen editor is kept in the project's memory.</summary>
    public const string EditorKey = "unityEditor";

    public static string? Find(UnityProject project, ProjectToolContext context)
    {
        if (context.Memory.Get(project.Root, EditorKey) is { Length: > 0 } chosen && Executable(chosen, context.OS) is { } picked)
        {
            return picked;
        }
        if (project.EditorVersion is not { } version)
        {
            return null;
        }
        foreach (var folder in InstallFolders(context))
        {
            if (Executable(Path.Combine(folder, version), context.OS) is { } found)
            {
                return found;
            }
        }
        foreach (var (listed, location) in HubEditors(context))
        {
            if (string.Equals(listed, version, StringComparison.OrdinalIgnoreCase) && Executable(location, context.OS) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>Unity Hub's config folder: <c>%APPDATA%\UnityHub</c>, <c>~/Library/Application Support/UnityHub</c>, <c>~/.config/UnityHub</c>.</summary>
    public static string? HubConfigFolder(ProjectToolContext context) => context.OS switch
    {
        ToolOS.Windows => context.Paths.AppData is { } appData ? Path.Combine(appData, "UnityHub") : null,
        ToolOS.MacOS => Path.Combine(context.Paths.Home, "Library", "Application Support", "UnityHub"),
        _ => Path.Combine(context.Paths.Home, ".config", "UnityHub"),
    };

    /// <summary>Folders Hub installs editors in, one folder per version: its default, then its custom one.</summary>
    public static IReadOnlyList<string> InstallFolders(ProjectToolContext context)
    {
        var folders = new List<string>();
        var standard = context.OS switch
        {
            ToolOS.Windows => context.Paths.ProgramFiles is { } programs ? Path.Combine(programs, "Unity", "Hub", "Editor") : null,
            ToolOS.MacOS => Path.Combine(context.Paths.Applications, "Unity", "Hub", "Editor"),
            _ => Path.Combine(context.Paths.Home, "Unity", "Hub", "Editor"),
        };
        if (standard is not null)
        {
            folders.Add(standard);
        }
        if (HubConfigFolder(context) is { } config && LenientJson.ReadFile(Path.Combine(config, "secondaryInstallPath.json")) is JsonValue value
            && value.TryGetValue<string>(out var secondary) && secondary.Trim().Length > 0)
        {
            folders.Add(secondary.Trim());
        }
        return folders;
    }

    /// <summary>
    /// Editors listed in Hub's <c>editors-v2.json</c> (<c>{ "data": [ { "version", "location" } ] }</c>) and
    /// <c>editors.json</c> (<c>{ "&lt;version&gt;": { "version", "location" } }</c>). A location can be a string or a
    /// list; anything else is skipped.
    /// </summary>
    public static IReadOnlyList<(string Version, string Location)> HubEditors(ProjectToolContext context)
    {
        if (HubConfigFolder(context) is not { } config)
        {
            return [];
        }
        var editors = new List<(string, string)>();
        foreach (var file in (string[])["editors-v2.json", "editors.json"])
        {
            var json = LenientJson.ReadFile(Path.Combine(config, file));
            IEnumerable<JsonNode?> entries = json switch
            {
                JsonObject { } obj when obj["data"] is JsonArray data => data,
                JsonObject obj => obj.Select(p => p.Value),
                JsonArray array => array,
                _ => [],
            };
            foreach (var entry in entries.OfType<JsonObject>())
            {
                if (LenientJson.String(entry, "version") is not { Length: > 0 } version)
                {
                    continue;
                }
                var locations = entry["location"] switch
                {
                    JsonValue one when one.TryGetValue<string>(out var s) => [s],
                    JsonArray many => many.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToArray(),
                    _ => Array.Empty<string>(),
                };
                editors.AddRange(locations.Where(l => l.Length > 0).Select(l => (version, l)));
            }
        }
        return editors;
    }

    /// <summary>
    /// The editor's executable in a version's folder (<c>Editor/Unity.exe</c>, <c>Unity.app</c>, <c>Editor/Unity</c>),
    /// or the executable itself, or macOS's <c>Unity.app</c>; null when there's none.
    /// </summary>
    public static string? Executable(string path, ToolOS os)
    {
        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(path.Trim());
            if (File.Exists(trimmed))
            {
                return trimmed;
            }
            if (!Directory.Exists(trimmed))
            {
                return null;
            }
            string[] candidates = os switch
            {
                ToolOS.Windows => [Path.Combine(trimmed, "Editor", "Unity.exe"), Path.Combine(trimmed, "Unity.exe")],
                ToolOS.MacOS => [Path.Combine(trimmed, "Contents", "MacOS", "Unity"), Path.Combine(trimmed, "Unity.app", "Contents", "MacOS", "Unity")],
                _ => [Path.Combine(trimmed, "Editor", "Unity"), Path.Combine(trimmed, "Unity")],
            };
            return candidates.FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The pick for <b>Choose Unity editor…</b>: the executable, or a folder or app that holds it.</summary>
    public static (string? Path, string? Error) Validate(string picked, ToolOS os) =>
        Executable(picked, os) is { } found && string.Equals(Path.GetFileNameWithoutExtension(found), "Unity", StringComparison.OrdinalIgnoreCase)
            ? (found, null)
            : (null, $"{picked} isn't a Unity editor: no Unity executable was found there.");

    // ---- Commands ---------------------------------------------------------------------------------------------------

    /// <summary><b>Open in Unity</b>, detached: <c>Unity -projectPath "&lt;path&gt;"</c>, with <c>-debugCodeOptimization</c> for Debug.</summary>
    public static ProcessStartSpec Open(string editor, string project, UnityCodeOptimization optimization)
    {
        List<string> arguments = ["-projectPath", project];
        if (optimization == UnityCodeOptimization.Debug)
        {
            arguments.Add("-debugCodeOptimization");
        }
        return new ProcessStartSpec(editor, arguments) { WorkingDirectory = project, Detached = true };
    }

    /// <summary>
    /// <b>Run EditMode tests</b>: <c>Unity -batchmode -projectPath "&lt;path&gt;" -runTests -testPlatform EditMode
    /// -testResults "&lt;results&gt;" -logFile -</c>. Unity quits by itself when the tests are done.
    /// </summary>
    public static ProcessStartSpec RunEditModeTests(string editor, string project, string results) =>
        new(editor, ["-batchmode", "-projectPath", project, "-runTests", "-testPlatform", "EditMode", "-testResults", results, "-logFile", "-"])
        {
            WorkingDirectory = project,
        };

    /// <summary><b>Regenerate the C# solution</b>: <c>Unity -batchmode -quit -projectPath "&lt;path&gt;" -executeMethod &lt;method&gt; -logFile -</c>.</summary>
    public static ProcessStartSpec SyncSolution(string editor, string project, string method) =>
        new(editor, ["-batchmode", "-quit", "-projectPath", project, "-executeMethod", method, "-logFile", "-"]) { WorkingDirectory = project };

    /// <summary>
    /// The editor's log: <c>%LOCALAPPDATA%\Unity\Editor\Editor.log</c>, <c>~/Library/Logs/Unity/Editor.log</c> or
    /// <c>~/.config/unity3d/Editor.log</c>.
    /// </summary>
    public static string? EditorLog(ProjectToolPaths paths, ToolOS os) => os switch
    {
        ToolOS.Windows => paths.LocalAppData is { } local ? os.Join(local, "Unity", "Editor", "Editor.log") : null,
        ToolOS.MacOS => os.Join(paths.Home, "Library", "Logs", "Unity", "Editor.log"),
        _ => os.Join(paths.Home, ".config", "unity3d", "Editor.log"),
    };

    /// <summary>
    /// A player's log, by <c>companyName</c> and <c>productName</c>: <c>%USERPROFILE%\AppData\LocalLow\&lt;company&gt;\&lt;product&gt;\Player.log</c>,
    /// <c>~/Library/Logs/&lt;company&gt;/&lt;product&gt;/Player.log</c> or <c>~/.config/unity3d/&lt;company&gt;/&lt;product&gt;/Player.log</c>.
    /// </summary>
    public static string PlayerLog(ProjectToolPaths paths, ToolOS os, string company, string product) => os switch
    {
        ToolOS.Windows => os.Join(paths.Home, "AppData", "LocalLow", company, product, "Player.log"),
        ToolOS.MacOS => os.Join(paths.Home, "Library", "Logs", company, product, "Player.log"),
        _ => os.Join(paths.Home, ".config", "unity3d", company, product, "Player.log"),
    };
}
