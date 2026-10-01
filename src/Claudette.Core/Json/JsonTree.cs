using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claudette.Core.Json;

/// <summary>
/// Parses JSON into a <see cref="JsonNode"/> tree that can be read without throwing. <see cref="JsonNode.Parse(string, JsonNodeOptions?, JsonDocumentOptions)"/>
/// accepts an object with a repeated key, then throws an <see cref="ArgumentException"/> the first time that object is
/// read. Claude Code, like JavaScript's <c>JSON.parse</c>, keeps the last value, so a hand-edited settings file with a
/// repeated key is valid to it; this keeps the last value too. Use it for every JSON Claudette reads.
/// </summary>
public static class JsonTree
{
    /// <summary>The parsed JSON (null for <c>null</c>). Throws a <see cref="JsonException"/> when it isn't JSON.</summary>
    public static JsonNode? Parse(string json, JsonDocumentOptions options = default)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: options with { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            // Not JSON, or a repeated key: read it again keeping each key's last value. Not JSON throws again.
            using var document = JsonDocument.Parse(json, options with { AllowDuplicateProperties = true });
            return FromElement(document.RootElement);
        }
    }

    /// <summary>The parsed JSON when it's an object; null when it's something else. Throws as <see cref="Parse"/> does.</summary>
    public static JsonObject? ParseObject(string json, JsonDocumentOptions options = default) => Parse(json, options) as JsonObject;

    private static JsonNode? FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => FromObject(element),
        JsonValueKind.Array => new JsonArray([.. element.EnumerateArray().Select(FromElement)]),
        JsonValueKind.Null => null,
        _ => JsonValue.Create(element.Clone()),
    };

    private static JsonObject FromObject(JsonElement element)
    {
        var obj = new JsonObject();
        foreach (var property in element.EnumerateObject())
        {
            // A repeated key replaces the earlier value.
            obj[property.Name] = FromElement(property.Value);
        }
        return obj;
    }
}
