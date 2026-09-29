using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claudette.Core.ProjectTools;

/// <summary>
/// Reads the JSON files engines write, which people also edit by hand: comments and trailing commas are allowed, and
/// unknown fields are ignored.
/// </summary>
public static class LenientJson
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The parsed JSON, or null when it isn't JSON.</summary>
    public static JsonNode? Parse(string text)
    {
        try
        {
            // A byte order mark, as some editors write.
            return JsonNode.Parse(text.TrimStart('﻿'), documentOptions: Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The file's JSON, or null when it's missing, unreadable or not JSON.</summary>
    public static JsonNode? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A string property, or null when it's missing or not a string.</summary>
    public static string? String(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) && value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A number property, or null. Numbers written as strings count too.</summary>
    public static int? Int(JsonNode? node, string name)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out var value) || value is not JsonValue v)
        {
            return null;
        }
        if (v.TryGetValue<int>(out var i))
        {
            return i;
        }
        if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
        {
            return (int)d;
        }
        return v.TryGetValue<string>(out var s) && int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>The objects in an array property; none when it's missing or not an array.</summary>
    public static IEnumerable<JsonObject> Objects(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) && value is JsonArray array ? array.OfType<JsonObject>() : [];
}
