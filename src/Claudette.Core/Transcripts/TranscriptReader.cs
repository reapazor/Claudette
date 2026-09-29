using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Transcripts;

/// <summary>One thing to show when replaying a transcript.</summary>
public abstract record TranscriptItem;

/// <summary>Something the user typed.</summary>
public sealed record TranscriptPrompt(string Text) : TranscriptItem;

/// <summary>An assistant message, or tool results, in the same form as the live stream.</summary>
public sealed record TranscriptMessage(ClaudeMessage Message) : TranscriptItem;

/// <summary>A local command's output, such as the note after a model change.</summary>
public sealed record TranscriptNote(string Text) : TranscriptItem;

/// <param name="StartedAt">The time of the transcript's first entry: when the session started.</param>
public sealed record Transcript(IReadOnlyList<TranscriptItem> Items, string? AiTitle, string? CustomTitle, DateTimeOffset? StartedAt = null)
{
    /// <summary>The name Claude Code would show: a custom name wins over the AI-generated title.</summary>
    public string? Title => CustomTitle ?? AiTitle;
}

/// <summary>
/// Reads Claude Code's session transcripts (<c>&lt;session-id&gt;.jsonl</c>) so a restored tab can show its earlier
/// conversation (DESIGN.md §9). The format is internal to Claude Code and changes between versions, so every entry is
/// read leniently and anything unrecognized is skipped (DESIGN.md §13, "Transcripts").
/// </summary>
public static class TranscriptReader
{
    private const string LocalCommandStart = "<local-command-stdout>";

    /// <summary>Finds a session's transcript under Claude Code's projects folder, whatever project it's in.</summary>
    public static string? Find(string projectsDirectory, string sessionId)
    {
        if (!Directory.Exists(projectsDirectory))
        {
            return null;
        }
        var fileName = $"{sessionId}.jsonl";
        foreach (var project in Directory.EnumerateDirectories(projectsDirectory))
        {
            var candidate = Path.Combine(project, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    public static async Task<Transcript> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        return Read(lines);
    }

    public static Transcript Read(IEnumerable<string> lines)
    {
        var items = new List<TranscriptItem>();
        string? aiTitle = null;
        string? customTitle = null;
        DateTimeOffset? startedAt = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            JsonObject? entry;
            try
            {
                entry = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            if (entry is null)
            {
                continue;
            }
            if (startedAt is null && DateTimeOffset.TryParse(entry.GetString("timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            {
                startedAt = time;
            }
            if (entry.GetBool("isSidechain") == true || entry.GetBool("isMeta") == true)
            {
                continue;
            }

            switch (entry.GetString("type"))
            {
                case "ai-title":
                    aiTitle = entry.GetString("aiTitle") ?? aiTitle;
                    break;
                case "custom-title":
                    customTitle = entry.GetString("customTitle") ?? customTitle;
                    break;
                case "assistant" when entry.GetObject("message") is { } message:
                    if (Parse(new JsonObject { ["type"] = "assistant", ["message"] = message.DeepClone() }) is { } assistant)
                    {
                        items.Add(new TranscriptMessage(assistant));
                    }
                    break;
                case "user" when entry.GetObject("message") is { } message:
                    ReadUser(entry, message, items);
                    break;
            }
        }
        return new Transcript(items, aiTitle, customTitle, startedAt);
    }

    private static void ReadUser(JsonObject entry, JsonObject message, List<TranscriptItem> items)
    {
        var content = message["content"];
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            AddText(text, items);
            return;
        }
        if (content is not JsonArray blocks)
        {
            return;
        }
        if (blocks.OfType<JsonObject>().Any(b => b.GetString("type") == "tool_result"))
        {
            // Transcripts call it toolUseResult; the live stream calls it tool_use_result.
            var user = new JsonObject
            {
                ["type"] = "user",
                ["message"] = message.DeepClone(),
                ["tool_use_result"] = (entry["toolUseResult"] ?? entry["tool_use_result"])?.DeepClone(),
            };
            if (Parse(user) is { } toolResults)
            {
                items.Add(new TranscriptMessage(toolResults));
            }
            return;
        }
        var joined = string.Join("\n", blocks.OfType<JsonObject>().Where(b => b.GetString("type") == "text").Select(b => b.GetString("text")));
        if (joined.Length > 0)
        {
            AddText(joined, items);
        }
    }

    private static void AddText(string text, List<TranscriptItem> items)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith(LocalCommandStart, StringComparison.Ordinal))
        {
            var output = trimmed[LocalCommandStart.Length..].Replace("</local-command-stdout>", "", StringComparison.Ordinal).Trim();
            if (output.Length > 0)
            {
                items.Add(new TranscriptNote(output));
            }
            return;
        }
        // Claude Code records an interrupt as user text; show it the way the live view does.
        if (trimmed.StartsWith("[Request interrupted by user", StringComparison.Ordinal))
        {
            items.Add(new TranscriptNote("Stopped."));
            return;
        }
        // Slash command echoes and injected reminders aren't things the user typed.
        if (trimmed.StartsWith("<command-", StringComparison.Ordinal) || trimmed.StartsWith("<system-reminder>", StringComparison.Ordinal))
        {
            return;
        }
        items.Add(new TranscriptPrompt(StripReminders(text)));
    }

    /// <summary>Claude Code prepends <c>&lt;system-reminder&gt;</c> blocks to some prompts; they aren't what the user typed.</summary>
    private static string StripReminders(string text)
    {
        const string open = "<system-reminder>";
        const string close = "</system-reminder>";
        while (text.IndexOf(open, StringComparison.Ordinal) is var start and >= 0
               && text.IndexOf(close, start, StringComparison.Ordinal) is var end and >= 0)
        {
            text = text.Remove(start, end + close.Length - start);
        }
        return text.Trim();
    }

    private static ClaudeMessage? Parse(JsonObject obj) =>
        MessageParser.TryParse(obj.ToJsonString(), out var message, out _) ? message : null;
}
