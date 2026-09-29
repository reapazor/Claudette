using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Transcripts;

/// <summary>
/// Decides which user entries in a transcript are things the user actually typed, with the same rules as
/// <see cref="TranscriptReader"/>: tool results, slash command echoes, injected reminders, local command output and
/// interrupt markers aren't prompts, and reminders prepended to a prompt are removed (DESIGN.md §9, "History").
/// </summary>
internal static class TranscriptPrompts
{
    /// <summary>The prompt in a user entry's <c>message</c>, or null when it isn't a real prompt.</summary>
    public static string? FromMessage(JsonObject message)
    {
        var content = message["content"];
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return FromText(text);
        }
        if (content is not JsonArray blocks)
        {
            return null;
        }
        if (blocks.OfType<JsonObject>().Any(b => b.GetString("type") == "tool_result"))
        {
            return null;
        }
        var joined = string.Join("\n", blocks.OfType<JsonObject>().Where(b => b.GetString("type") == "text").Select(b => b.GetString("text")));
        return joined.Length > 0 ? FromText(joined) : null;
    }

    /// <summary>The prompt in a user entry's text, or null when it isn't one.</summary>
    public static string? FromText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("<local-command-stdout>", StringComparison.Ordinal)
            || trimmed.StartsWith("[Request interrupted by user", StringComparison.Ordinal)
            || trimmed.StartsWith("<command-", StringComparison.Ordinal)
            || trimmed.StartsWith("<system-reminder>", StringComparison.Ordinal))
        {
            return null;
        }
        var stripped = StripReminders(text);
        return stripped.Length > 0 ? stripped : null;
    }

    /// <summary>Claude Code prepends <c>&lt;system-reminder&gt;</c> blocks to some prompts; they aren't what the user typed.</summary>
    public static string StripReminders(string text)
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
}
