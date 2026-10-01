using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Claudette.Core.Json;
using Claudette.Core.Protocol;

namespace Claudette.Core.Transcripts;

/// <summary>One thing to show when replaying a transcript.</summary>
public abstract record TranscriptItem
{
    /// <summary>The entry's <c>timestamp</c>: when it was sent (DESIGN.md §5). Null when the entry has none.</summary>
    public DateTimeOffset? Time { get; init; }

    /// <summary>The entry's <c>uuid</c>: where a resume can stop (DESIGN.md §5, "Rewind and branch").</summary>
    public string? Uuid { get; init; }

    /// <summary>
    /// The entry before it in the conversation (<c>parentUuid</c>). For a prompt, resuming there leaves out the prompt
    /// and everything after it; null for the first.
    /// </summary>
    public string? ParentUuid { get; init; }
}

/// <summary>Something the user typed.</summary>
public sealed record TranscriptPrompt(string Text) : TranscriptItem
{
    /// <summary>Images attached to the prompt (DESIGN.md §5, "Attachments"), as Claude Code stored them.</summary>
    public IReadOnlyList<MessageImage> Images { get; init; } = [];
}

/// <summary>
/// An assistant message, or tool results, in the same form as the live stream. A subagent's messages carry the
/// <c>parent_tool_use_id</c> of the <c>Agent</c> call that started it, as they do live.
/// </summary>
public sealed record TranscriptMessage(ClaudeMessage Message) : TranscriptItem;

/// <summary>A local command's output, such as the note after a model change.</summary>
public sealed record TranscriptNote(string Text) : TranscriptItem;

/// <summary>
/// A background task ended, as Claude Code told the model (<c>&lt;task-notification&gt;</c>): for example a background
/// subagent finishing, with the report it handed back (DESIGN.md §18, "Agent map").
/// </summary>
public sealed record TranscriptTaskNotification(
    string? TaskId,
    string? ToolUseId,
    string? Status,
    string? Summary,
    string? Result,
    long? Tokens,
    int? ToolUses,
    long? DurationMs) : TranscriptItem;

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
    private const string TaskNotificationStart = "<task-notification>";

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

    /// <summary>
    /// Reads a transcript, with its subagents' transcripts from <c>&lt;session-id&gt;/subagents/</c> beside it when
    /// there are any, merged in time order so each subagent's traffic follows the <c>Agent</c> call that started it.
    /// </summary>
    public static Task<Transcript> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        ReadAsync(path, endAt: null, cancellationToken);

    /// <summary>
    /// Reads a transcript as <see cref="ReadAsync(string, CancellationToken)"/> does, up to and including the entry
    /// <paramref name="endAt"/> (its <c>uuid</c>): what a resume with <c>--resume-session-at</c> keeps (DESIGN.md §5,
    /// "Rewind and branch"). Subagents' entries after it are left out too. Null, or an entry it doesn't have, reads it all.
    /// </summary>
    public static async Task<Transcript> ReadAsync(string path, string? endAt, CancellationToken cancellationToken = default)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var main = ReadEntries(lines, parentToolUseId: null, endAt);
        var streams = new List<List<Entry>> { main.Entries };
        var subagents = await ReadSubagentsAsync(path, cancellationToken).ConfigureAwait(false);
        if (main.Ended)
        {
            // An entry without a time follows the one before it.
            subagents = subagents.Select(stream => stream.TakeWhile(e => e.Time is null || main.EndTime is null || e.Time <= main.EndTime).ToList()).ToList();
        }
        streams.AddRange(subagents);
        return new Transcript(Merge(streams), main.AiTitle, main.CustomTitle, main.StartedAt);
    }

    public static Transcript Read(IEnumerable<string> lines)
    {
        var read = ReadEntries(lines, parentToolUseId: null);
        return new Transcript(read.Entries.Select(e => e.Item).ToArray(), read.AiTitle, read.CustomTitle, read.StartedAt);
    }

    /// <summary>An item with the time of the entry it came from, which the item carries too.</summary>
    private sealed record Entry
    {
        public Entry(DateTimeOffset? time, TranscriptItem item)
        {
            Time = time;
            Item = item with { Time = time };
        }

        public DateTimeOffset? Time { get; }

        public TranscriptItem Item { get; }
    }

    /// <param name="Ended">The entry to end at was found: <paramref name="EndTime"/> is its time.</param>
    private sealed record ReadResult(List<Entry> Entries, string? AiTitle, string? CustomTitle, DateTimeOffset? StartedAt, bool Ended = false, DateTimeOffset? EndTime = null);

    /// <param name="parentToolUseId">Set for a subagent's own transcript, whose entries are all marked as a sidechain.</param>
    /// <param name="endAt">The last entry to read items from; titles are still read from the rest.</param>
    private static ReadResult ReadEntries(IEnumerable<string> lines, string? parentToolUseId, string? endAt = null)
    {
        var items = new List<Entry>();
        string? aiTitle = null;
        string? customTitle = null;
        DateTimeOffset? startedAt = null;
        var ended = false;
        DateTimeOffset? endTime = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            JsonObject? entry;
            try
            {
                entry = JsonTree.ParseObject(line);
            }
            catch (JsonException)
            {
                continue;
            }
            if (entry is null)
            {
                continue;
            }
            var time = entry.GetString("timestamp") is { } stamp
                && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;
            startedAt ??= time;
            // The point can be an entry that shows nothing, such as an attachment: the items end there all the same.
            var endsHere = !ended && endAt is not null && entry.GetString("uuid") == endAt;
            if (parentToolUseId is null && entry.GetBool("isSidechain") == true || entry.GetBool("isMeta") == true)
            {
                (ended, endTime) = endsHere ? (true, time) : (ended, endTime);
                continue;
            }

            var before = items.Count;
            switch (entry.GetString("type"))
            {
                case "ai-title":
                    aiTitle = entry.GetString("aiTitle") ?? aiTitle;
                    break;
                case "custom-title":
                    customTitle = entry.GetString("customTitle") ?? customTitle;
                    break;
                case not null when ended:
                    break;
                case "assistant" when entry.GetObject("message") is { } message:
                    if (Parse(new JsonObject { ["type"] = "assistant", ["message"] = message.DeepClone(), ["parent_tool_use_id"] = parentToolUseId, ["uuid"] = entry.GetString("uuid") }) is { } assistant)
                    {
                        items.Add(new Entry(time, new TranscriptMessage(assistant)));
                    }
                    break;
                case "user" when entry.GetObject("message") is { } message:
                    ReadUser(entry, message, parentToolUseId, time, items);
                    break;
            }
            if (items.Count > before && (entry.GetString("uuid") is not null || entry.GetString("parentUuid") is not null))
            {
                var item = items[^1];
                items[^1] = new Entry(item.Time, item.Item with { Uuid = entry.GetString("uuid"), ParentUuid = entry.GetString("parentUuid") });
            }
            if (endsHere)
            {
                ended = true;
                endTime = time;
            }
        }
        return new ReadResult(items, aiTitle, customTitle, startedAt, ended, endTime);
    }

    private static void ReadUser(JsonObject entry, JsonObject message, string? parentToolUseId, DateTimeOffset? time, List<Entry> items)
    {
        var content = message["content"];
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (parentToolUseId is null)
            {
                AddText(text, time, items);
            }
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
                ["uuid"] = entry.GetString("uuid"),
                ["message"] = message.DeepClone(),
                ["tool_use_result"] = (entry["toolUseResult"] ?? entry["tool_use_result"])?.DeepClone(),
                ["parent_tool_use_id"] = parentToolUseId,
            };
            if (Parse(user) is { } toolResults)
            {
                items.Add(new Entry(time, new TranscriptMessage(toolResults)));
            }
            return;
        }
        // A subagent's text from the "user" side is its prompt, which its Agent call already shows.
        if (parentToolUseId is not null || TranscriptPrompts.IsCommandEcho(blocks))
        {
            return;
        }
        var joined = string.Join("\n", blocks.OfType<JsonObject>().Where(b => b.GetString("type") == "text").Select(b => b.GetString("text")));
        // Attached images are stored inline, as base64 image blocks.
        var images = blocks.OfType<JsonObject>()
            .Where(b => b.GetString("type") == "image" && b.GetObject("source")?.GetString("type") == "base64")
            .Select(b => MessageImage.FromBase64(b.GetObject("source")!.GetString("media_type"), b.GetObject("source")!.GetString("data")))
            .OfType<MessageImage>()
            .ToArray();
        if (joined.Length > 0 || images.Length > 0)
        {
            AddText(joined, time, items, images);
        }
    }

    private static void AddText(string text, DateTimeOffset? time, List<Entry> items, IReadOnlyList<MessageImage>? images = null)
    {
        if (images is { Count: > 0 } && text.Trim().Length == 0)
        {
            items.Add(new Entry(time, new TranscriptPrompt("") { Images = images }));
            return;
        }
        var trimmed = text.Trim();
        if (trimmed.StartsWith(LocalCommandStart, StringComparison.Ordinal))
        {
            var output = trimmed[LocalCommandStart.Length..].Replace("</local-command-stdout>", "", StringComparison.Ordinal).Trim();
            if (output.Length > 0)
            {
                items.Add(new Entry(time, new TranscriptNote(output)));
            }
            return;
        }
        // A background task's result, handed to the model as a user turn; the user didn't type it.
        if (trimmed.StartsWith(TaskNotificationStart, StringComparison.Ordinal))
        {
            items.Add(new Entry(time, ParseTaskNotification(trimmed)));
            return;
        }
        // Claude Code records an interrupt as user text; show it the way the live view does.
        if (trimmed.StartsWith("[Request interrupted by user", StringComparison.Ordinal))
        {
            items.Add(new Entry(time, new TranscriptNote("Stopped.")));
            return;
        }
        // Slash command echoes and injected reminders aren't things the user typed.
        if (trimmed.StartsWith("<command-", StringComparison.Ordinal) || trimmed.StartsWith("<system-reminder>", StringComparison.Ordinal))
        {
            return;
        }
        items.Add(new Entry(time, new TranscriptPrompt(StripReminders(text)) { Images = images ?? [] }));
    }

    /// <summary>Reads the tags of a <c>&lt;task-notification&gt;</c>; any that are missing read as null.</summary>
    private static TranscriptTaskNotification ParseTaskNotification(string text)
    {
        string? Tag(string name) =>
            Regex.Match(text, $"<{name}>(.*?)</{name}>", RegexOptions.Singleline) is { Success: true } match ? match.Groups[1].Value.Trim() : null;
        long? Number(string name) => long.TryParse(Tag(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        return new TranscriptTaskNotification(
            Tag("task-id"),
            Tag("tool-use-id"),
            Tag("status"),
            Tag("summary"),
            Tag("result"),
            Number("subagent_tokens") ?? Number("total_tokens"),
            (int?)Number("tool_uses"),
            Number("duration_ms"));
    }

    /// <summary>
    /// The subagents' own transcripts: <c>&lt;session-id&gt;/subagents/agent-&lt;id&gt;.jsonl</c>, each with a
    /// <c>.meta.json</c> naming the <c>Agent</c> call that started it (<c>toolUseId</c>). One without it is skipped.
    /// </summary>
    private static async Task<List<List<Entry>>> ReadSubagentsAsync(string transcriptPath, CancellationToken cancellationToken)
    {
        var streams = new List<List<Entry>>();
        var folder = Path.Combine(Path.GetDirectoryName(transcriptPath) ?? "", Path.GetFileNameWithoutExtension(transcriptPath), "subagents");
        if (!Directory.Exists(folder))
        {
            return streams;
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*.jsonl").Order(StringComparer.Ordinal))
        {
            try
            {
                var meta = Path.ChangeExtension(file, ".meta.json");
                if (!File.Exists(meta)
                    || JsonTree.Parse(await File.ReadAllTextAsync(meta, cancellationToken).ConfigureAwait(false)) is not JsonObject metadata
                    || metadata.GetString("toolUseId") is not { Length: > 0 } toolUseId)
                {
                    continue;
                }
                var lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
                streams.Add(ReadEntries(lines, toolUseId).Entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A subagent's detail is optional; the main transcript still replays.
            }
        }
        return streams;
    }

    /// <summary>
    /// Merges the transcripts by time, keeping each one's own order. An entry without a time takes the one before it;
    /// ties go to the earlier stream, so the main transcript comes first.
    /// </summary>
    private static List<TranscriptItem> Merge(List<List<Entry>> streams)
    {
        if (streams.Count == 1)
        {
            return streams[0].Select(e => e.Item).ToList();
        }
        var times = streams.Select(stream =>
        {
            var last = DateTimeOffset.MinValue;
            return stream.Select(e => last = e.Time ?? last).ToArray();
        }).ToArray();
        var positions = new int[streams.Count];
        var merged = new List<TranscriptItem>(streams.Sum(s => s.Count));
        while (true)
        {
            var next = -1;
            for (var i = 0; i < streams.Count; i++)
            {
                if (positions[i] < streams[i].Count && (next < 0 || times[i][positions[i]] < times[next][positions[next]]))
                {
                    next = i;
                }
            }
            if (next < 0)
            {
                return merged;
            }
            merged.Add(streams[next][positions[next]++].Item);
        }
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
