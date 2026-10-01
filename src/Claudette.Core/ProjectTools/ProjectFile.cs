using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claudette.Core.ProjectTools;

/// <summary>What a folder's project files hold, both merged: the shared file's entries, then the local file's.</summary>
/// <param name="Actions">The actions for this OS, in order. <see cref="ProjectFile.Read"/> also leaves out those whose <c>ifExists</c> is missing.</param>
/// <param name="Problems">Why entries (or a whole file) were skipped, such as "claudette.json: actions[2] has no command".</param>
public sealed record ProjectFileContents(IReadOnlyList<CustomProjectAction> Actions, IReadOnlyList<ProjectLink> Links, IReadOnlyList<string> Problems)
{
    public static ProjectFileContents Empty { get; } = new([], [], []);

    public bool IsEmpty => Actions.Count == 0 && Links.Count == 0 && Problems.Count == 0;
}

/// <summary>One entry of a file's <c>actions</c>, as the editor sees it: the JSON as written, and what it reads as.</summary>
/// <param name="Raw">The entry as it is in the file. Saving keeps its other fields, such as <c>os</c>.</param>
/// <param name="Action">What it reads as; null when it can't be read.</param>
/// <param name="Problem">Why it can't be read.</param>
/// <param name="ForThisOS">Its <c>os</c> includes this machine's.</param>
public sealed record ProjectFileEntry(JsonNode? Raw, CustomProjectAction? Action, string? Problem, bool ForThisOS);

/// <summary>One entry of a file's <c>links</c>, as the Links page of Settings sees it (DESIGN.md §18, "Links").</summary>
/// <param name="Raw">The entry as it is in the file. Saving keeps its other fields.</param>
/// <param name="Link">What it reads as; null when it can't be read.</param>
/// <param name="Problem">Why it can't be read.</param>
public sealed record ProjectFileLinkEntry(JsonNode? Raw, ProjectLink? Link, string? Problem)
{
    /// <summary>The name as written: empty when the entry has none, and the sidebar shows its address instead.</summary>
    public string GivenName => LenientJson.String(Raw, "name")?.Trim() ?? "";
}

/// <summary>
/// <c>claudette.json</c> and <c>claudette.local.json</c> in a tab's folder (DESIGN.md §18, "claudette.json"): the
/// folder's own actions and links. The shared file is committed with the project; the local one is the user's.
/// Both are read tolerantly: comments and trailing commas are fine, unknown fields are ignored, and a bad entry is
/// skipped with a reason rather than failing the file. A missing file means nothing.
/// <code>
/// {
///   "actions": [
///     { "name": "Run tests", "command": "dotnet test", "folder": "src", "mode": "output" },
///     { "name": "Open Grafana", "command": "start https://grafana", "mode": "launch", "os": ["windows"] },
///     { "name": "Play", "command": "Build\\Game.exe", "mode": "launch", "ifExists": "Build/Game.exe" }
///   ],
///   "links": [
///     { "name": "Pull request", "url": "https://github.com/org/repo/compare/{branch}?expand=1" }
///   ]
/// }
/// </code>
/// </summary>
public static class ProjectFile
{
    public const string SharedName = "claudette.json";

    public const string LocalName = "claudette.local.json";

    /// <summary>Indented, and without escaping characters such as <c>&amp;</c> that commands are full of.</summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string FileName(ProjectFileScope scope) => scope == ProjectFileScope.Shared ? SharedName : LocalName;

    public static string PathFor(string folder, ProjectFileScope scope) => Path.Combine(folder, FileName(scope));

    /// <summary>
    /// Both files of <paramref name="folder"/>, the shared one first. Actions whose <c>ifExists</c> paths aren't all
    /// there are left out; they're checked again each time the files are read.
    /// </summary>
    public static ProjectFileContents Read(string folder, ToolOS os)
    {
        var shared = Parse(ReadText(PathFor(folder, ProjectFileScope.Shared), out var sharedError), ProjectFileScope.Shared, os);
        var local = Parse(ReadText(PathFor(folder, ProjectFileScope.Local), out var localError), ProjectFileScope.Local, os);
        return new ProjectFileContents(
            [.. shared.Actions.Concat(local.Actions).Where(a => a.IsShownIn(folder))],
            [.. shared.Links, .. local.Links],
            [.. Error(SharedName, sharedError), .. shared.Problems, .. Error(LocalName, localError), .. local.Problems]);

        static IEnumerable<string> Error(string file, string? error) => error is null ? [] : [$"{file} couldn't be read: {error}"];
    }

    /// <summary>One file's text; null (no file) reads as nothing.</summary>
    public static ProjectFileContents Parse(string? text, ProjectFileScope scope, ToolOS os)
    {
        if (text is null)
        {
            return ProjectFileContents.Empty;
        }
        var file = FileName(scope);
        var problems = new List<string>();
        if (!TryParseRoot(text, out var root, out var error))
        {
            return new ProjectFileContents([], [], [$"{file} isn't valid JSON, so it was skipped: {error}"]);
        }
        var actions = new List<CustomProjectAction>();
        foreach (var entry in Entries(root, "actions", file, problems).Select((node, index) => ReadAction(node, index, scope, os)))
        {
            if (entry.Problem is { } problem)
            {
                problems.Add($"{file}: {problem}");
            }
            else if (entry.ForThisOS && entry.Action is { } action)
            {
                actions.Add(action);
            }
        }
        var links = new List<ProjectLink>();
        foreach (var entry in Entries(root, "links", file, problems).Select((node, index) => ReadLink(node, index, scope)))
        {
            if (entry.Link is { } link)
            {
                links.Add(link);
            }
            else
            {
                problems.Add($"{file}: {entry.Problem}");
            }
        }
        return new ProjectFileContents(actions, links, problems);
    }

    /// <summary>Every entry of one file's <c>actions</c>, for the editor, including ones for other OSes and ones it can't read.</summary>
    /// <exception cref="InvalidOperationException">The file exists but isn't JSON: it can't be edited here without losing it.</exception>
    public static IReadOnlyList<ProjectFileEntry> ReadEntries(string folder, ProjectFileScope scope, ToolOS os) =>
        ReadRoot(folder, scope) is { } root
            ? Entries(root, "actions", FileName(scope), []).Select((node, index) => ReadAction(node, index, scope, os)).ToArray()
            : [];

    /// <summary>Every entry of one file's <c>links</c>, for Settings' Links page, including ones it can't read.</summary>
    /// <exception cref="InvalidOperationException">The file exists but isn't JSON: it can't be edited here without losing it.</exception>
    public static IReadOnlyList<ProjectFileLinkEntry> ReadLinkEntries(string folder, ProjectFileScope scope) =>
        ReadRoot(folder, scope) is { } root
            ? Entries(root, "links", FileName(scope), []).Select((node, index) => ReadLink(node, index, scope)).ToArray()
            : [];

    /// <summary>
    /// Replaces one file's <c>actions</c> with <paramref name="entries"/>, keeping its other keys, and writes it
    /// indented. Comments in the file are lost: JSON can't keep them. Creates the file when there's none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file exists but isn't JSON.</exception>
    public static void WriteActions(string folder, ProjectFileScope scope, IEnumerable<JsonNode?> entries) => WriteList(folder, scope, "actions", entries);

    /// <summary>
    /// Replaces one file's <c>links</c> with <paramref name="entries"/>, keeping its other keys (its actions among them),
    /// and writes it indented, as <see cref="WriteActions"/> does. Creates the file when there's none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file exists but isn't JSON.</exception>
    public static void WriteLinks(string folder, ProjectFileScope scope, IEnumerable<JsonNode?> entries) => WriteList(folder, scope, "links", entries);

    private static void WriteList(string folder, ProjectFileScope scope, string key, IEnumerable<JsonNode?> entries)
    {
        var root = ReadRoot(folder, scope) ?? [];
        root[key] = new JsonArray([.. entries.Select(e => e?.DeepClone())]);
        File.WriteAllText(PathFor(folder, scope), root.ToJsonString(WriteOptions) + Environment.NewLine);
    }

    /// <summary>One file's root object, to edit; null when there's no file.</summary>
    /// <exception cref="InvalidOperationException">The file can't be read, or isn't JSON.</exception>
    private static JsonObject? ReadRoot(string folder, ProjectFileScope scope)
    {
        var text = ReadText(PathFor(folder, scope), out var error);
        if (error is not null)
        {
            throw new InvalidOperationException($"{FileName(scope)} couldn't be read: {error}");
        }
        if (text is null)
        {
            return null;
        }
        if (!TryParseRoot(text, out var root, out var parseError))
        {
            throw new InvalidOperationException($"{FileName(scope)} isn't valid JSON ({parseError}), so Claudette won't rewrite it. Fix it by hand first.");
        }
        return root;
    }

    /// <summary>An action as an entry of <c>actions</c>: <paramref name="raw"/>'s other fields kept, its own written over them.</summary>
    public static JsonObject ToJson(CustomProjectAction action, JsonNode? raw = null)
    {
        var json = raw is JsonObject existing ? (JsonObject)existing.DeepClone() : [];
        json["name"] = action.Name;
        json["command"] = action.Command;
        if (string.IsNullOrWhiteSpace(action.WorkingFolder))
        {
            json.Remove("folder");
        }
        else
        {
            json["folder"] = action.WorkingFolder.Trim();
        }
        switch (action.IfExists)
        {
            case null or []:
                json.Remove("ifExists");
                break;
            case [var one]:
                json["ifExists"] = one;
                break;
            case var several:
                json["ifExists"] = new JsonArray([.. several.Select(p => (JsonNode?)p)]);
                break;
        }
        json["mode"] = action.Mode == CustomActionMode.LaunchAndForget ? "launch" : "output";
        return json;
    }

    /// <summary>
    /// A link as an entry of <c>links</c>: <paramref name="raw"/>'s other fields kept, its name and address written over
    /// them. An empty name is left out, and the sidebar shows the address instead.
    /// </summary>
    public static JsonObject LinkToJson(string? name, string url, JsonNode? raw = null)
    {
        var json = raw is JsonObject existing ? (JsonObject)existing.DeepClone() : [];
        if (string.IsNullOrWhiteSpace(name))
        {
            json.Remove("name");
        }
        else
        {
            json["name"] = name.Trim();
        }
        json["url"] = url.Trim();
        return json;
    }

    /// <summary>Does an <c>os</c> list include <paramref name="os"/>? No list means every OS.</summary>
    public static bool AppliesTo(IReadOnlyList<string>? list, ToolOS os) =>
        list is null || list.Any(name => name.Trim().ToLowerInvariant() switch
        {
            "windows" or "win" or "win64" => os == ToolOS.Windows,
            "macos" or "mac" or "osx" or "darwin" => os == ToolOS.MacOS,
            "linux" => os == ToolOS.Linux,
            _ => false,
        });

    private static ProjectFileEntry ReadAction(JsonNode? node, int index, ProjectFileScope scope, ToolOS os)
    {
        var where = $"actions[{index}]";
        if (node is not JsonObject entry)
        {
            return new ProjectFileEntry(node, null, $"{where} isn't an object, so it was skipped.", true);
        }
        var name = LenientJson.String(entry, "name")?.Trim();
        var command = LenientJson.String(entry, "command")?.Trim();
        var forThisOS = true;
        IReadOnlyList<string>? osList = null;
        if (entry["os"] is JsonNode osNode)
        {
            osList = StringList(osNode);
            if (osList is null)
            {
                return new ProjectFileEntry(node, null, $"{where}: \"os\" should be a list such as [\"windows\", \"macos\"], so it was skipped.", true);
            }
            forThisOS = AppliesTo(osList, os);
        }
        if (string.IsNullOrEmpty(name))
        {
            return new ProjectFileEntry(node, null, $"{where} has no name, so it was skipped.", forThisOS);
        }
        if (string.IsNullOrEmpty(command))
        {
            return new ProjectFileEntry(node, null, $"{where} (\"{name}\") has no command, so it was skipped.", forThisOS);
        }
        var modeText = LenientJson.String(entry, "mode")?.Trim().ToLowerInvariant();
        CustomActionMode? mode = modeText switch
        {
            null or "" or "output" => CustomActionMode.RunWithOutput,
            "launch" => CustomActionMode.LaunchAndForget,
            _ => null,
        };
        if (mode is null)
        {
            return new ProjectFileEntry(node, null, $"{where} (\"{name}\") has mode \"{modeText}\"; it can be \"output\" or \"launch\", so it was skipped.", forThisOS);
        }
        IReadOnlyList<string>? ifExists = null;
        if (entry["ifExists"] is JsonNode existsNode)
        {
            if (StringList(existsNode) is not { } paths)
            {
                return new ProjectFileEntry(node, null, $"{where} (\"{name}\"): \"ifExists\" should be a path or a list of paths, such as \"Build/Game.exe\", so it was skipped.", forThisOS);
            }
            var given = paths.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            ifExists = given.Length > 0 ? given : null;
        }
        var folder = LenientJson.String(entry, "folder")?.Trim();
        var action = new CustomProjectAction
        {
            Id = $"{(scope == ProjectFileScope.Shared ? "shared" : "local")}:{index}",
            Scope = scope,
            Name = name,
            Command = command,
            WorkingFolder = string.IsNullOrEmpty(folder) ? null : folder,
            Mode = mode.Value,
            Os = osList,
            IfExists = ifExists,
        };
        return new ProjectFileEntry(node, action, null, forThisOS);
    }

    /// <summary>A string or a list of strings, such as <c>"os"</c>; null when it's neither. Other values in a list are ignored.</summary>
    private static IReadOnlyList<string>? StringList(JsonNode node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var one) => [one],
        JsonArray array => array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToArray(),
        _ => null,
    };

    private static ProjectFileLinkEntry ReadLink(JsonNode? node, int index, ProjectFileScope scope)
    {
        var where = $"links[{index}]";
        if (node is not JsonObject entry)
        {
            return new ProjectFileLinkEntry(node, null, $"{where} isn't an object, so it was skipped.");
        }
        var url = LenientJson.String(entry, "url")?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return new ProjectFileLinkEntry(node, null, $"{where} has no url, so it was skipped.");
        }
        var name = LenientJson.String(entry, "name")?.Trim();
        return new ProjectFileLinkEntry(node, new ProjectLink(string.IsNullOrEmpty(name) ? url : name, url, scope), null);
    }

    private static IEnumerable<JsonNode?> Entries(JsonObject root, string name, string file, List<string> problems)
    {
        if (!root.TryGetPropertyValue(name, out var node) || node is null)
        {
            return [];
        }
        if (node is JsonArray array)
        {
            return array;
        }
        problems.Add($"{file}: \"{name}\" should be a list, so it was skipped.");
        return [];
    }

    private static bool TryParseRoot(string text, out JsonObject root, out string? error)
    {
        root = [];
        error = null;
        if (text.Trim().TrimStart('﻿').Length == 0)
        {
            return true;
        }
        try
        {
            var node = JsonNode.Parse(text.TrimStart('﻿'), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node is JsonObject obj)
            {
                root = obj;
                return true;
            }
            error = "it should be an object: { \"actions\": [ … ], \"links\": [ … ] }";
            return false;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>A file's text; null when there's no file, with <paramref name="error"/> set when it can't be read.</summary>
    private static string? ReadText(string path, out string? error)
    {
        error = null;
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }
}
