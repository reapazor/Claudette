using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claudette.Core.Protocol;

/// <summary>Lenient readers for protocol JSON: a missing or wrongly typed field reads as null instead of throwing.</summary>
public static class JsonNodeExtensions
{
    public static string? GetString(this JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static bool? GetBool(this JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : null;

    public static double? GetDouble(this JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;

    public static JsonObject? GetObject(this JsonObject obj, string name) => obj[name] as JsonObject;

    public static JsonArray? GetArray(this JsonObject obj, string name) => obj[name] as JsonArray;

    public static IReadOnlyList<string> GetStringList(this JsonObject obj, string name) =>
        obj.GetArray(name)?
            .OfType<JsonValue>()
            .Where(v => v.GetValueKind() == JsonValueKind.String)
            .Select(v => v.GetValue<string>())
            .ToArray() ?? [];
}
