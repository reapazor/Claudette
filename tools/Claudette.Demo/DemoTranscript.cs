using System.Globalization;
using System.Text.Json.Nodes;

namespace Claudette.Demo;

/// <summary>
/// Writes a session's transcript as Claude Code 2.1.284 does (DESIGN.md §9): a line per prompt, per block of Claude's
/// replies and per tool result, each pointing at the one before. Claudette reads it back when the tab is restored.
/// </summary>
internal sealed class DemoTranscript(string sessionId, string folder, string branch, DateTimeOffset start)
{
    private readonly List<string> _lines = [];
    private string? _parent;
    private DateTimeOffset _at = start;
    private int _ids;

    public string SessionId => sessionId;

    public DemoTranscript Prompt(string text)
    {
        text = Lf(text);
        Add(Entry("user", Uuid(), 30, new JsonObject { ["role"] = "user", ["content"] = text }));
        return this;
    }

    public DemoTranscript Thinking(string text)
    {
        text = Lf(text);
        Reply(new JsonObject { ["type"] = "thinking", ["thinking"] = text, ["signature"] = "demo" });
        return this;
    }

    /// <param name="last">The turn's last reply, which ends it.</param>
    public DemoTranscript Text(string text, bool last = false)
    {
        text = Lf(text);
        Reply(new JsonObject { ["type"] = "text", ["text"] = text }, last ? "end_turn" : "tool_use");
        return this;
    }

    /// <summary>A tool call and its result. Returns the call's id, which marks a change as reviewed.</summary>
    public string Tool(string name, JsonObject input, string result, JsonObject toolUseResult)
    {
        var id = $"toolu_demo_{sessionId[..8]}_{++_ids:D3}";
        var source = Reply(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input });
        var line = Entry("user", Uuid(), 2, new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["tool_use_id"] = id, ["type"] = "tool_result", ["content"] = result }),
        });
        line["toolUseResult"] = toolUseResult;
        line["sourceToolAssistantUUID"] = source;
        Add(line);
        return id;
    }

    public DemoTranscript Read(string path, string content)
    {
        content = Lf(content);
        var lines = content.TrimEnd('\n').Split('\n');
        var numbered = string.Join('\n', lines.Select((l, i) => $"{i + 1,6}\t{l}"));
        Tool("Read", new JsonObject { ["file_path"] = path }, numbered, new JsonObject
        {
            ["type"] = "text",
            ["file"] = new JsonObject { ["filePath"] = path, ["content"] = content, ["numLines"] = lines.Length, ["startLine"] = 1, ["totalLines"] = lines.Length },
        });
        return this;
    }

    /// <summary>An Edit of <paramref name="path"/>, which held <paramref name="original"/>.</summary>
    public string Edit(string path, string original, string oldString, string newString)
    {
        (original, oldString, newString) = (Lf(original), Lf(oldString), Lf(newString));
        var line = original[..original.IndexOf(oldString, StringComparison.Ordinal)].Split('\n').Length;
        var (removed, added) = (oldString.Split('\n'), newString.Split('\n'));
        var patch = new JsonObject
        {
            ["oldStart"] = line,
            ["oldLines"] = removed.Length,
            ["newStart"] = line,
            ["newLines"] = added.Length,
            ["lines"] = new JsonArray([.. removed.Select(l => (JsonNode?)$"-{l}"), .. added.Select(l => (JsonNode?)$"+{l}")]),
        };
        return Tool("Edit", new JsonObject { ["file_path"] = path, ["old_string"] = oldString, ["new_string"] = newString },
            $"The file {path} has been updated successfully.",
            new JsonObject
            {
                ["filePath"] = path,
                ["oldString"] = oldString,
                ["newString"] = newString,
                ["originalFile"] = original,
                ["structuredPatch"] = new JsonArray(patch),
                ["userModified"] = false,
                ["replaceAll"] = false,
            });
    }

    /// <summary>A Write that creates <paramref name="path"/>.</summary>
    public string Create(string path, string content)
    {
        content = Lf(content);
        return Tool("Write", new JsonObject { ["file_path"] = path, ["content"] = content }, $"File created successfully at: {path}",
            new JsonObject { ["type"] = "create", ["filePath"] = path, ["content"] = content, ["structuredPatch"] = new JsonArray(), ["originalFile"] = null });
    }

    public DemoTranscript Bash(string command, string description, string output)
    {
        output = Lf(output);
        Tool("Bash", new JsonObject { ["command"] = command, ["description"] = description }, output,
            new JsonObject { ["stdout"] = output, ["stderr"] = "", ["interrupted"] = false, ["isImage"] = false });
        return this;
    }

    /// <summary>The title Claude Code gives the session.</summary>
    public DemoTranscript Title(string title)
    {
        _lines.Add(new JsonObject { ["type"] = "ai-title", ["aiTitle"] = title, ["sessionId"] = sessionId }.ToJsonString());
        return this;
    }

    /// <returns>The transcript's path.</returns>
    public string Save(string directory)
    {
        var path = Path.Combine(directory, $"{sessionId}.jsonl");
        File.WriteAllLines(path, _lines);
        return path;
    }

    private string Reply(JsonObject block, string stopReason = "tool_use")
    {
        var uuid = Uuid();
        Add(Entry("assistant", uuid, 4, new JsonObject
        {
            ["id"] = $"msg_demo_{uuid[^8..]}",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "claude-opus-5-5",
            ["content"] = new JsonArray(block),
            ["stop_reason"] = stopReason,
            ["usage"] = new JsonObject { ["input_tokens"] = 6, ["cache_creation_input_tokens"] = 1800, ["cache_read_input_tokens"] = 41000, ["output_tokens"] = 380 },
        }));
        return uuid;
    }

    private JsonObject Entry(string type, string uuid, int seconds, JsonObject message)
    {
        _at = _at.AddSeconds(seconds);
        return new JsonObject
        {
            ["parentUuid"] = _parent,
            ["isSidechain"] = false,
            ["type"] = type,
            ["message"] = message,
            ["uuid"] = uuid,
            ["timestamp"] = _at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["userType"] = "external",
            ["entrypoint"] = "sdk-cli",
            ["cwd"] = folder,
            ["sessionId"] = sessionId,
            ["version"] = "2.1.284",
            ["gitBranch"] = branch,
        };
    }

    private void Add(JsonObject line)
    {
        _lines.Add(line.ToJsonString());
        _parent = line["uuid"]!.GetValue<string>();
    }

    /// <summary>Files on disk have LF line endings, so their contents in the transcript do too, whatever the checkout's.</summary>
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    private string Uuid() => $"{sessionId[..8]}-0000-4000-8000-{++_ids:D12}";
}
