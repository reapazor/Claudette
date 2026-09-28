using System.Text.Json.Nodes;

namespace Claudette.Core.Protocol;

/// <summary>One block of a message's content. <see cref="Raw"/> keeps the original JSON.</summary>
public abstract record ContentBlock(string Type, JsonObject Raw);

public sealed record TextBlock(string Text, JsonObject Raw) : ContentBlock("text", Raw);

public sealed record ThinkingBlock(string Thinking, JsonObject Raw) : ContentBlock("thinking", Raw);

public sealed record ToolUseBlock(string Id, string Name, JsonObject Input, JsonObject Raw) : ContentBlock("tool_use", Raw);

public sealed record ToolResultBlock(string ToolUseId, string Text, bool IsError, JsonObject Raw) : ContentBlock("tool_result", Raw);

/// <summary>A block type Claudette doesn't know yet.</summary>
public sealed record UnknownBlock(string Type, JsonObject Raw) : ContentBlock(Type, Raw);

internal static class ContentBlockParser
{
    /// <summary>Reads message content, which is either a plain string or an array of blocks.</summary>
    public static IReadOnlyList<ContentBlock> Parse(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return [new TextBlock(text, new JsonObject { ["type"] = "text", ["text"] = text })];
        }
        if (content is not JsonArray array)
        {
            return [];
        }

        var blocks = new List<ContentBlock>(array.Count);
        foreach (var item in array.OfType<JsonObject>())
        {
            var type = item.GetString("type") ?? "";
            blocks.Add(type switch
            {
                "text" => new TextBlock(item.GetString("text") ?? "", item),
                "thinking" => new ThinkingBlock(item.GetString("thinking") ?? "", item),
                "tool_use" => new ToolUseBlock(item.GetString("id") ?? "", item.GetString("name") ?? "", item.GetObject("input") ?? [], item),
                "tool_result" => new ToolResultBlock(item.GetString("tool_use_id") ?? "", ToolResultText(item["content"]), item.GetBool("is_error") ?? false, item),
                _ => new UnknownBlock(type, item),
            });
        }
        return blocks;
    }

    private static string ToolResultText(JsonNode? content) => content switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray array => string.Join("\n", array.OfType<JsonObject>().Select(b => b.GetString("text")).OfType<string>()),
        _ => "",
    };
}
