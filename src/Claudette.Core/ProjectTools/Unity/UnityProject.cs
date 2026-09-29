using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Claudette.Core.ProjectTools.Unity;

/// <summary>
/// A Unity project (DESIGN.md §18, "Unity"): a folder with <c>ProjectSettings/ProjectVersion.txt</c> and <c>Assets/</c>.
/// </summary>
/// <param name="Name">The folder's name, which the C# solution is named after.</param>
/// <param name="EditorVersion"><c>m_EditorVersion</c>, such as <c>2022.3.20f1</c>; null when it can't be read.</param>
/// <param name="Revision">The changeset in <c>m_EditorVersionWithRevision</c>, when it's there.</param>
/// <param name="ProductName"><c>productName</c> in <c>ProjectSettings.asset</c>, for the player log's folder.</param>
/// <param name="CompanyName"><c>companyName</c> in <c>ProjectSettings.asset</c>.</param>
/// <param name="IdePackages">The IDE packages in <c>Packages/manifest.json</c>, such as <c>com.jetbrains.rider</c>.</param>
public sealed partial record UnityProject(string Root, string Name, string? EditorVersion, string? Revision, string? ProductName, string? CompanyName, IReadOnlyList<string> IdePackages)
{
    public const string RiderPackage = "com.jetbrains.rider";
    public const string VisualStudioPackage = "com.unity.ide.visualstudio";
    public const string VSCodePackage = "com.unity.ide.vscode";

    /// <summary>Is <paramref name="folder"/> a Unity project?</summary>
    public static bool IsProject(string folder) =>
        File.Exists(Path.Combine(folder, "ProjectSettings", "ProjectVersion.txt")) && Directory.Exists(Path.Combine(folder, "Assets"));

    public static UnityProject Read(string root)
    {
        var (version, revision) = ParseProjectVersion(ReadText(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt")) ?? "");
        var settings = ReadText(Path.Combine(root, "ProjectSettings", "ProjectSettings.asset")) ?? "";
        var manifest = LenientJson.ReadFile(Path.Combine(root, "Packages", "manifest.json"));
        var packages = manifest?["dependencies"] is JsonObject dependencies
            ? new[] { RiderPackage, VisualStudioPackage, VSCodePackage }.Where(dependencies.ContainsKey).ToArray()
            : [];
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        return new UnityProject(root, name, version, revision, AssetValue(settings, "productName"), AssetValue(settings, "companyName"), packages);
    }

    /// <summary><c>m_EditorVersion: 2022.3.20f1</c>, and the revision from <c>m_EditorVersionWithRevision: 2022.3.20f1 (61c2feb0970d)</c>.</summary>
    public static (string? Version, string? Revision) ParseProjectVersion(string text)
    {
        string? version = null;
        string? revision = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("m_EditorVersionWithRevision:", StringComparison.Ordinal))
            {
                var match = RevisionPattern().Match(line);
                if (match.Success)
                {
                    version ??= match.Groups[1].Value;
                    revision = match.Groups[2].Value;
                }
            }
            else if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
            {
                var value = line["m_EditorVersion:".Length..].Trim();
                if (value.Length > 0)
                {
                    version = value;
                }
            }
        }
        return (version, revision);
    }

    /// <summary>A top-level <c>key: value</c> of a Unity YAML asset such as <c>ProjectSettings.asset</c>.</summary>
    public static string? AssetValue(string yaml, string key)
    {
        var match = Regex.Match(yaml, $@"^\s*{Regex.Escape(key)}:[ \t]*(.*?)\s*$", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        if (!match.Success)
        {
            return null;
        }
        var value = match.Groups[1].Value.Trim().Trim('\'', '"');
        return value.Length > 0 ? value : null;
    }

    /// <summary>
    /// The method <b>Regenerate the C# solution</b> runs, from the IDE package the project has: Rider's, Visual Studio's,
    /// or Visual Studio's for a project with only the old VS Code package. Rider's when the user opens solutions with
    /// Rider and it has both. Null when it has none.
    /// </summary>
    public string? SyncMethod(bool preferRider)
    {
        var rider = IdePackages.Contains(RiderPackage) ? "Packages.Rider.Editor.RiderScriptEditor.SyncSolution" : null;
        var visualStudio = IdePackages.Contains(VisualStudioPackage) ? "Microsoft.Unity.VisualStudio.Editor.VisualStudioEditor.SyncAll" : null;
        return preferRider ? rider ?? visualStudio : visualStudio ?? rider;
    }

    /// <summary>
    /// An NUnit results file, as <c>-runTests</c> writes it: "11 passed, 1 failed, 2 skipped". Null when it can't be read.
    /// </summary>
    public static string? SummarizeTestResults(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var run = XDocument.Load(path).Root;
            if (run is null)
            {
                return null;
            }
            int Count(string name) => int.TryParse(run.Attribute(name)?.Value, out var n) ? n : 0;
            var parts = new List<string> { $"{Count("passed")} passed", $"{Count("failed")} failed" };
            if (Count("skipped") > 0)
            {
                parts.Add($"{Count("skipped")} skipped");
            }
            if (Count("inconclusive") > 0)
            {
                parts.Add($"{Count("inconclusive")} inconclusive");
            }
            return string.Join(", ", parts) + ".";
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^m_EditorVersionWithRevision:\s*(\S+)\s*\(([0-9a-fA-F]+)\)")]
    private static partial Regex RevisionPattern();
}
