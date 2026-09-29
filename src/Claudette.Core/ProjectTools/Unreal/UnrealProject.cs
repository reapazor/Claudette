using System.Globalization;
using System.Text.RegularExpressions;

namespace Claudette.Core.ProjectTools.Unreal;

/// <summary>
/// A <c>.uproject</c> file (DESIGN.md §18, "Project tools"), read tolerantly: comments, trailing commas and unknown
/// fields are fine.
/// </summary>
/// <param name="EngineAssociation">
/// Which engine it uses: empty for an engine in a parent folder, a version such as <c>5.4</c> for a launcher install,
/// or a GUID for a source build registered by UnrealVersionSelector.
/// </param>
/// <param name="EditorTarget">From <c>Source/*Editor.Target.cs</c>, else <c>&lt;Name&gt;Editor</c>.</param>
/// <param name="HasCode">It has C++ code: a <c>Source</c> folder with targets, or modules.</param>
public sealed record UnrealProject(string Path, string Name, string Root, string? EngineAssociation, string EditorTarget, bool HasCode, IReadOnlyList<string> Modules, IReadOnlyList<string> EnabledPlugins)
{
    /// <summary>Reads a <c>.uproject</c>. A file that isn't JSON still gives a project, with no association.</summary>
    public static UnrealProject Read(string path)
    {
        var json = LenientJson.ReadFile(path);
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var root = System.IO.Path.GetDirectoryName(path) ?? ".";
        var association = LenientJson.String(json, "EngineAssociation")?.Trim();
        var modules = LenientJson.Objects(json, "Modules").Select(m => LenientJson.String(m, "Name")).OfType<string>().Where(n => n.Length > 0).ToArray();
        var plugins = LenientJson.Objects(json, "Plugins")
            .Where(p => p["Enabled"] is not System.Text.Json.Nodes.JsonValue enabled || !enabled.TryGetValue<bool>(out var on) || on)
            .Select(p => LenientJson.String(p, "Name"))
            .OfType<string>()
            .ToArray();
        var targets = EditorTargets(root);
        var target = targets.FirstOrDefault(t => string.Equals(t, $"{name}Editor", StringComparison.OrdinalIgnoreCase)) ?? targets.FirstOrDefault() ?? $"{name}Editor";
        return new UnrealProject(path, name, root, string.IsNullOrEmpty(association) ? null : association, target, targets.Count > 0 || modules.Length > 0, modules, plugins);
    }

    /// <summary>The editor targets in <c>Source/</c>: <c>NightOwlEditor.Target.cs</c> gives <c>NightOwlEditor</c>.</summary>
    public static IReadOnlyList<string> EditorTargets(string root)
    {
        var source = System.IO.Path.Combine(root, "Source");
        return Directory.Exists(source)
            ? ProjectSearch.FilesIn(source, "*Editor.Target.cs").Select(f => System.IO.Path.GetFileName(f)[..^".Target.cs".Length]).ToArray()
            : [];
    }
}

/// <summary>How an engine was found, which is also how it's described.</summary>
public enum UnrealEngineKind
{
    /// <summary>Installed by the Epic Games launcher (a version association).</summary>
    Launcher,

    /// <summary>A build registered by UnrealVersionSelector (a GUID association).</summary>
    SourceBuild,

    /// <summary>An engine in a parent folder of the project (an empty association): the "native" layout.</summary>
    ParentFolder,

    /// <summary>A folder the user chose in the chip menu, remembered for the project.</summary>
    Chosen,
}

/// <summary><c>Engine/Build/Build.version</c>: the engine's version.</summary>
public sealed record UnrealBuildVersion(int Major, int Minor, int Patch, string? BranchName)
{
    /// <summary>"5.4".</summary>
    public string Short => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");

    /// <summary>"5.4.2".</summary>
    public string Full => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>Reads <c>Build.version</c>; null when it's missing or has no major version.</summary>
    public static UnrealBuildVersion? Read(string path) => Parse(LenientJson.ReadFile(path));

    public static UnrealBuildVersion? Parse(System.Text.Json.Nodes.JsonNode? json) =>
        LenientJson.Int(json, "MajorVersion") is { } major
            ? new UnrealBuildVersion(major, LenientJson.Int(json, "MinorVersion") ?? 0, LenientJson.Int(json, "PatchVersion") ?? 0, LenientJson.String(json, "BranchName"))
            : null;
}

/// <summary>An engine a project can use: the folder that holds <c>Engine/</c>.</summary>
public sealed record UnrealEngine(string Root, UnrealEngineKind Kind, UnrealBuildVersion? Version)
{
    /// <summary>Unreal Engine 4 calls its editor <c>UE4Editor</c>; 5 calls it <c>UnrealEditor</c>.</summary>
    public bool IsUE4 => Version?.Major == 4;

    public string EditorName => IsUE4 ? "UE4Editor" : "UnrealEditor";

    /// <summary><c>Engine/Build/Build.version</c> under <paramref name="root"/>.</summary>
    public static string VersionFile(string root) => System.IO.Path.Combine(root, "Engine", "Build", "Build.version");

    /// <summary>An installed build (the launcher's, or one made with BuildGraph) has <c>Engine/Build/InstalledBuild.txt</c>.</summary>
    public bool IsInstalledBuild => File.Exists(System.IO.Path.Combine(Root, "Engine", "Build", "InstalledBuild.txt"));

    /// <summary>"launcher install", "source build", "engine in a parent folder", "chosen folder".</summary>
    public string KindText => Kind switch
    {
        UnrealEngineKind.Launcher => "launcher install",
        UnrealEngineKind.SourceBuild => IsInstalledBuild ? "installed build" : "source build",
        UnrealEngineKind.ParentFolder => "engine in a parent folder",
        _ => IsInstalledBuild ? "installed build, chosen by you" : "source build, chosen by you",
    };

    /// <summary>For Claude's note: "a launcher install" or "a source build".</summary>
    public string KindForNote => Kind == UnrealEngineKind.Launcher ? "a launcher install"
        : IsInstalledBuild ? "an installed build"
        : Kind == UnrealEngineKind.ParentFolder ? "a source build in a parent folder"
        : "a source build";
}

public static partial class UnrealAssociation
{
    /// <summary>A launcher version such as <c>5.4</c> or <c>4.27</c>.</summary>
    public static bool IsVersion(string association) => VersionPattern().IsMatch(association);

    /// <summary>A GUID with or without braces, in any case, reduced to compare: <c>A1B2…</c> without braces, upper case.</summary>
    public static string NormalizeId(string id) => id.Trim().TrimStart('{').TrimEnd('}').ToUpperInvariant();

    public static bool SameId(string a, string b) => NormalizeId(a) == NormalizeId(b);

    [GeneratedRegex(@"^\d+\.\d+(\.\d+)?$")]
    private static partial Regex VersionPattern();
}
