using System.Text.Json.Nodes;

namespace Claudette.App.Conversation;

/// <summary>
/// What a running tool call is doing, in a few words for the working line (DESIGN.md §5, "Working line"):
/// "Running dotnet test", "Reading TabView.axaml", "Searching for auth".
/// </summary>
public static class ToolActivity
{
    /// <summary>Longer phrases are cut with an ellipsis, so the line stays on one row.</summary>
    public const int MaxLength = 60;

    /// <summary>One call.</summary>
    public static string Describe(string name, JsonObject input)
    {
        var text = name switch
        {
            "Bash" or "PowerShell" => Command(input) is { } command ? $"Running {command}" : "Running a command",
            "Read" => File(input) is { } file ? $"Reading {file}" : "Reading a file",
            "Edit" or "MultiEdit" or "NotebookEdit" => File(input) is { } file ? $"Editing {file}" : "Editing a file",
            "Write" => File(input) is { } file ? $"Writing {file}" : "Writing a file",
            "Grep" => Str(input, "pattern") is { } pattern ? $"Searching for {pattern}" : "Searching",
            "Glob" => Str(input, "pattern") is { } pattern ? $"Finding {pattern}" : "Finding files",
            "WebFetch" => Str(input, "url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"Fetching {uri.Host}" : "Fetching a page",
            "WebSearch" => Str(input, "query") is { } query ? $"Searching the web for {query}" : "Searching the web",
            "Agent" or "Task" => Str(input, "description") is { } description ? $"Running an agent: {description}" : "Running an agent",
            "TodoWrite" => "Updating the to-do list",
            "Skill" => Str(input, "skill") is { } skill ? $"Using the {skill} skill" : "Using a skill",
            "BashOutput" or "Monitor" => "Checking on a background command",
            _ when name.StartsWith("mcp__", StringComparison.Ordinal) && name.Split("__") is [_, var server, var tool, ..] => $"Using {tool} ({server})",
            _ => $"Using {name}",
        };
        return Cut(text);
    }

    /// <summary>
    /// Several calls at once, newest last: the newest, and how many others are running. Agents are counted together,
    /// since a fan-out starts several at once.
    /// </summary>
    public static string? Describe(IReadOnlyList<(string Name, JsonObject Input)> running)
    {
        if (running.Count == 0)
        {
            return null;
        }
        var agents = running.Count(r => r.Name is "Agent" or "Task");
        if (agents > 1 && agents == running.Count)
        {
            return $"Running {agents} agents";
        }
        var (name, input) = running[^1];
        var newest = Describe(name, input);
        return running.Count > 1 ? Cut(newest, $" and {running.Count - 1} more") : newest;
    }

    /// <summary>
    /// The running calls in full, one per line, for the working line's tooltip: a command as it was given, a file's
    /// whole path. Null when none is running.
    /// </summary>
    public static string? Details(IReadOnlyList<(string Name, JsonObject Input)> running)
    {
        if (running.Count == 0)
        {
            return null;
        }
        return string.Join("\n", running.Select(r =>
        {
            var full = r.Name is "Bash" or "PowerShell" && r.Input["command"] is JsonValue value && value.TryGetValue<string>(out var command)
                ? command.Trim()
                : ToolUseItem.Summarize(r.Name, r.Input, int.MaxValue);
            return full.Length > 0 ? $"{r.Name}: {full}" : r.Name;
        }));
    }

    /// <summary>The command's first line.</summary>
    private static string? Command(JsonObject input) =>
        input["command"] is JsonValue value && value.TryGetValue<string>(out var command)
            && command.Trim().ReplaceLineEndings("\n").Split('\n', 2)[0].Trim() is { Length: > 0 } line
            ? line
            : null;

    /// <summary>Just the file's name: the full path is on its card.</summary>
    private static string? File(JsonObject input) =>
        (Str(input, "file_path") ?? Str(input, "notebook_path")) is { } path && Path.GetFileName(path.Replace('\\', '/').TrimEnd('/')) is { Length: > 0 } name
            ? name
            : null;

    private static string? Str(JsonObject input, string key) =>
        input[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.ReplaceLineEndings(" ") : null;

    private static string Cut(string text, string suffix = "")
    {
        var room = MaxLength - suffix.Length;
        return (text.Length > room ? text[..(room - 1)].TrimEnd() + "…" : text) + suffix;
    }
}
