namespace Claudette.Core.ProjectTools.Unreal;

/// <summary>
/// The Windows registry's Unreal entries (DESIGN.md §18, "Finding the engine"). The implementation is in
/// Claudette.Platform; elsewhere there's none.
/// </summary>
public interface IUnrealEngineRegistry
{
    /// <summary><c>HKLM\SOFTWARE\EpicGames\Unreal Engine\&lt;version&gt;</c>, value <c>InstalledDirectory</c>: a launcher install.</summary>
    string? FindLauncherInstall(string version);

    /// <summary>
    /// <c>HKCU\SOFTWARE\Epic Games\Unreal Engine\Builds</c>: each value's name is a build's id (a GUID, usually in
    /// braces) and its data the engine's folder, as UnrealVersionSelector registers source builds.
    /// </summary>
    IReadOnlyDictionary<string, string> SourceBuilds();
}

public sealed class NoUnrealEngineRegistry : IUnrealEngineRegistry
{
    public static NoUnrealEngineRegistry Instance { get; } = new();

    public string? FindLauncherInstall(string version) => null;

    public IReadOnlyDictionary<string, string> SourceBuilds() => new Dictionary<string, string>();
}

/// <summary>
/// Finds the engine a project uses from its <c>EngineAssociation</c> (DESIGN.md §18, "Finding the engine"):
/// <list type="bullet">
/// <item>a folder the user chose for the project comes first;</item>
/// <item>empty: an engine in a parent folder (<c>Engine/Build/Build.version</c> next to the project or above it);</item>
/// <item>a version such as <c>5.4</c>: a launcher install, from <c>LauncherInstalled.dat</c>, then the registry;</item>
/// <item>anything else, usually a GUID: a build registered by UnrealVersionSelector, in the registry on Windows and
/// <c>Install.ini</c> on macOS and Linux.</item>
/// </list>
/// An engine only counts when its <c>Build.version</c> exists.
/// </summary>
public static class UnrealEngineLocator
{
    /// <summary>Where a chosen engine folder is kept in the project's memory.</summary>
    public const string EngineKey = "engine";

    /// <summary>How far up a parent-folder engine is looked for.</summary>
    public const int ParentLevels = 10;

    public static UnrealEngine? Find(UnrealProject project, ProjectToolContext context)
    {
        if (context.Memory.Get(project.Path, EngineKey) is { Length: > 0 } chosen && Engine(chosen, UnrealEngineKind.Chosen) is { } picked)
        {
            return picked;
        }
        var association = project.EngineAssociation;
        if (string.IsNullOrWhiteSpace(association))
        {
            return InParentFolder(project.Root);
        }
        if (UnrealAssociation.IsVersion(association))
        {
            return LauncherInstall(association, context);
        }
        return SourceBuild(association, context);
    }

    /// <summary>The folder above <paramref name="projectRoot"/> (or the folder itself) that has <c>Engine/Build/Build.version</c>.</summary>
    public static UnrealEngine? InParentFolder(string projectRoot)
    {
        var current = projectRoot;
        for (var level = 0; level <= ParentLevels && !string.IsNullOrEmpty(current); level++)
        {
            if (Engine(current, UnrealEngineKind.ParentFolder) is { } engine)
            {
                return engine;
            }
            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    public static UnrealEngine? LauncherInstall(string version, ProjectToolContext context)
    {
        var dat = context.OS switch
        {
            ToolOS.Windows when context.Paths.ProgramData is { } programData => Path.Combine(programData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat"),
            ToolOS.MacOS => Path.Combine(context.Paths.Home, "Library", "Application Support", "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat"),
            _ => null,
        };
        if (dat is not null && ReadLauncherInstalled(dat).GetValueOrDefault($"UE_{version}") is { } fromDat
            && Engine(fromDat, UnrealEngineKind.Launcher) is { } listed)
        {
            return listed;
        }
        if (context.OS == ToolOS.Windows && context.UnrealRegistry.FindLauncherInstall(version) is { } fromRegistry)
        {
            return Engine(fromRegistry, UnrealEngineKind.Launcher);
        }
        return null;
    }

    public static UnrealEngine? SourceBuild(string id, ProjectToolContext context)
    {
        IReadOnlyDictionary<string, string> builds = context.OS switch
        {
            ToolOS.Windows => context.UnrealRegistry.SourceBuilds(),
            ToolOS.MacOS => ReadInstallIni(Path.Combine(context.Paths.Home, "Library", "Application Support", "Epic", "UnrealEngine", "Install.ini")),
            _ => ReadInstallIni(Path.Combine(context.Paths.Home, ".config", "Epic", "UnrealEngine", "Install.ini")),
        };
        foreach (var (buildId, folder) in builds)
        {
            if (UnrealAssociation.SameId(buildId, id) && Engine(folder, UnrealEngineKind.SourceBuild) is { } engine)
            {
                return engine;
            }
        }
        return null;
    }

    /// <summary>
    /// An engine in <paramref name="folder"/>, when it has <c>Engine/Build/Build.version</c>. The <c>Engine</c> folder
    /// itself is accepted too, as people pick that as often as the folder above it.
    /// </summary>
    public static UnrealEngine? Engine(string folder, UnrealEngineKind kind)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(folder.Trim());
            if (!File.Exists(UnrealEngine.VersionFile(root))
                && string.Equals(Path.GetFileName(root), "Engine", StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(root) is { } parent && File.Exists(UnrealEngine.VersionFile(parent)))
            {
                root = parent;
            }
            var versionFile = UnrealEngine.VersionFile(root);
            return File.Exists(versionFile) ? new UnrealEngine(root, kind, UnrealBuildVersion.Read(versionFile)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>LauncherInstalled.dat</c>: JSON with an <c>InstallationList</c> of entries, each with an <c>AppName</c>
    /// (<c>UE_5.4</c>) and its <c>InstallLocation</c>. Returns install folders by app name.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadLauncherInstalled(string path) => ParseLauncherInstalled(LenientJson.ReadFile(path));

    public static IReadOnlyDictionary<string, string> ParseLauncherInstalled(System.Text.Json.Nodes.JsonNode? json)
    {
        var installs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in LenientJson.Objects(json, "InstallationList"))
        {
            if (LenientJson.String(entry, "AppName") is { Length: > 0 } app && LenientJson.String(entry, "InstallLocation") is { Length: > 0 } location)
            {
                installs.TryAdd(app, location);
            }
        }
        return installs;
    }

    /// <summary>
    /// <c>Install.ini</c>: its <c>[Installations]</c> section lists builds as <c>{GUID}=path</c>. Other sections,
    /// comments (<c>;</c> or <c>#</c>) and blank lines are skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadInstallIni(string path)
    {
        try
        {
            return File.Exists(path) ? ParseInstallIni(File.ReadAllText(path)) : new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static IReadOnlyDictionary<string, string> ParseInstallIni(string text)
    {
        var builds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inInstallations = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inInstallations = string.Equals(line[1..^1].Trim(), "Installations", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (inInstallations && equals > 0)
            {
                var value = line[(equals + 1)..].Trim().Trim('"');
                if (value.Length > 0)
                {
                    builds.TryAdd(line[..equals].Trim(), value);
                }
            }
        }
        return builds;
    }
}
