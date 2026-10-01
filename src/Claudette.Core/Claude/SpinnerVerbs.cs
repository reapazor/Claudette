using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Json;

namespace Claudette.Core.Claude;

/// <summary>
/// The verbs of the working line (DESIGN.md §5, "Working line"): Claudette's own list, changed by Claude Code's
/// documented <c>spinnerVerbs</c> setting, so verbs a user added for the terminal show here too.
/// </summary>
public static class SpinnerVerbs
{
    /// <summary>Claudette's own verbs. Claude Code doesn't publish its built-in list, so this one is Claudette's.</summary>
    public static IReadOnlyList<string> BuiltIn { get; } =
    [
        "Accomplishing", "Architecting", "Baking", "Brewing", "Calibrating", "Canoodling", "Churning", "Claudetting",
        "Cogitating", "Composing", "Concocting", "Conjuring", "Contemplating", "Cooking", "Crafting", "Crunching",
        "Deliberating", "Dillydallying", "Doodling", "Elucidating", "Fermenting", "Fiddling", "Finagling", "Flibbertigibbeting",
        "Forging", "Frolicking", "Gallivanting", "Germinating", "Hatching", "Herding", "Honking", "Hullaballooing",
        "Ideating", "Imagining", "Incubating", "Inferring", "Jiving", "Kneading", "Lollygagging", "Manifesting",
        "Marinating", "Meandering", "Moseying", "Mulling", "Musing", "Noodling", "Percolating", "Perusing",
        "Pondering", "Puttering", "Puzzling", "Reticulating", "Ruminating", "Schlepping", "Shimmying", "Simmering",
        "Smooshing", "Spelunking", "Stewing", "Synthesizing", "Thinking", "Tinkering", "Transmuting", "Unfurling",
        "Unravelling", "Vibing", "Whatchamacalliting", "Whirring", "Wibbling", "Wrangling", "Zigzagging",
    ];

    /// <summary>
    /// Where Claude Code looks for settings for a session in <paramref name="projectFolder"/>, most specific first:
    /// the project's local and shared settings, then the user's. Managed settings aren't read.
    /// </summary>
    /// <param name="configDirectory">Claude Code's config folder (<c>claude auth status</c>'s <c>configDirectory</c>); null skips the user's file.</param>
    public static IReadOnlyList<string> SettingsFiles(string? configDirectory, string? projectFolder)
    {
        var files = new List<string>();
        if (projectFolder is not null)
        {
            files.Add(Path.Combine(projectFolder, ".claude", "settings.local.json"));
            files.Add(Path.Combine(projectFolder, ".claude", "settings.json"));
        }
        if (configDirectory is not null)
        {
            files.Add(Path.Combine(configDirectory, "settings.json"));
        }
        return files;
    }

    /// <summary>
    /// The verbs to show: the built-in list, with the <c>spinnerVerbs</c> of the most specific file that sets it.
    /// <c>"append"</c> adds its verbs, <c>"replace"</c> shows only its verbs (an empty list keeps the built-in ones).
    /// A file that's missing, unreadable or not valid JSON is skipped, as is a value Claudette doesn't understand.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<string> settingsFiles)
    {
        foreach (var file in settingsFiles)
        {
            if (Read(file) is not { } setting)
            {
                continue;
            }
            var (mode, verbs) = setting;
            return mode switch
            {
                "replace" when verbs.Count > 0 => verbs,
                "replace" => BuiltIn,
                _ => [.. BuiltIn, .. verbs.Where(v => !BuiltIn.Contains(v, StringComparer.OrdinalIgnoreCase))],
            };
        }
        return BuiltIn;
    }

    /// <summary>The file's <c>spinnerVerbs</c>, or null when it has none Claudette can use.</summary>
    private static (string Mode, IReadOnlyList<string> Verbs)? Read(string file)
    {
        try
        {
            if (!File.Exists(file)
                || JsonTree.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    is not JsonObject root
                || root["spinnerVerbs"] is not JsonObject setting)
            {
                return null;
            }
            var mode = setting["mode"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "append";
            if (mode is not ("append" or "replace"))
            {
                return null;
            }
            var verbs = (setting["verbs"] as JsonArray ?? [])
                .Select(v => v is JsonValue item && item.TryGetValue<string>(out var verb) ? verb.Trim().TrimEnd('…', '.').Trim() : "")
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return (mode, verbs);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
