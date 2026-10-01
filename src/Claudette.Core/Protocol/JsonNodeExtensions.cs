using System.Globalization;
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

    /// <summary>A finite number, however the value was made: parsed, or built in code.</summary>
    public static double? GetDouble(this JsonObject obj, string name) => obj[name] is JsonValue value ? AsDouble(value) : null;

    /// <summary>A whole number that fits in a <see cref="long"/>; null for anything else, a fraction included.</summary>
    public static long? GetLong(this JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
        && long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>
    /// A number as a <see cref="double"/>, or null when it isn't one or isn't finite. A value parsed from JSON converts
    /// to any number type, but one built in code (<c>JsonValue.Create(13)</c>) only to its own, so its text is read.
    /// </summary>
    public static double? AsDouble(this JsonValue value)
    {
        if (value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }
        if (!value.TryGetValue<double>(out var number)
            && !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return null;
        }
        return double.IsFinite(number) ? number : null;
    }

    /// <summary>A string, or a number's JSON text (<c>"7"</c> for <c>7</c>): an id that may come either way.</summary>
    public static string? GetStringOrNumber(this JsonObject obj, string name) => obj[name] switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.ToJsonString(),
        _ => null,
    };

    public static JsonObject? GetObject(this JsonObject obj, string name) => obj[name] as JsonObject;

    public static JsonArray? GetArray(this JsonObject obj, string name) => obj[name] as JsonArray;

    public static IReadOnlyList<string> GetStringList(this JsonObject obj, string name) =>
        obj.GetArray(name)?
            .OfType<JsonValue>()
            .Where(v => v.GetValueKind() == JsonValueKind.String)
            .Select(v => v.GetValue<string>())
            .ToArray() ?? [];
}
