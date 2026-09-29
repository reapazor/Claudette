using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claudette.Usage;

/// <summary>
/// Lenient readers for usage JSON: a missing or wrongly typed field reads as null instead of throwing. The same idea
/// as Core's internal <c>JsonNodeExtensions</c>, plus numbers built in code and timestamps in either ISO or epoch form.
/// </summary>
internal static class UsageJson
{
    public static string? GetString(this JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static bool? GetBool(this JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : null;

    public static double? GetDouble(this JsonObject obj, string name) => obj[name] is JsonValue value ? AsDouble(value) : null;

    public static JsonObject? GetObject(this JsonObject obj, string name) => obj[name] as JsonObject;

    public static JsonArray? GetArray(this JsonObject obj, string name) => obj[name] as JsonArray;

    /// <summary>An ISO 8601 string, or a number of epoch seconds (or milliseconds, if it's that large).</summary>
    public static DateTimeOffset? GetTime(this JsonObject obj, string name) => obj[name] switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => ParseIso(value.GetValue<string>()),
        JsonValue value when AsDouble(value) is { } number => FromEpoch(number),
        _ => null,
    };

    public static double? AsDouble(JsonValue value)
    {
        if (value.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }
        // A value built in code (JsonValue.Create(13)) only converts to its own type, so fall back to its text.
        if (!value.TryGetValue<double>(out var number)
            && !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return null;
        }
        return double.IsFinite(number) ? number : null;
    }

    public static DateTimeOffset? FromEpoch(double value)
    {
        const double MaxSeconds = 253_402_300_799; // 9999-12-31
        var seconds = value > 100_000_000_000 ? value / 1000 : value;
        return seconds is >= 0 and <= MaxSeconds ? RoundToSecond(DateTimeOffset.UnixEpoch.AddSeconds(seconds)) : null;
    }

    public static DateTimeOffset? ParseIso(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? RoundToSecond(time) : null;

    /// <summary>
    /// <c>get_usage</c> reports resets to the microsecond (<c>02:19:59.953628</c>) and <c>rate_limit_event</c> to the
    /// second (<c>02:20:00</c>). Rounding makes the two agree, so they identify the same window.
    /// </summary>
    public static DateTimeOffset RoundToSecond(DateTimeOffset time)
    {
        var ticks = time.UtcTicks;
        if (ticks > DateTimeOffset.MaxValue.UtcTicks - TimeSpan.TicksPerSecond)
        {
            return time.ToUniversalTime();
        }
        return new DateTimeOffset((ticks + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }
}
