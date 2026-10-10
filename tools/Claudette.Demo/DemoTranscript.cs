using System.Globalization;
using System.Text.Json.Nodes;
using Claudette.Core.Threads;

namespace Claudette.Demo;

/// <summary>
/// Writes a session's transcript as Claude Code 2.1.284 does (DESIGN.md §9): a line per prompt, per block of Claude's
/// replies and per tool result, each pointing at the one before, and each subagent's own transcript in
/// <c>&lt;session-id&gt;/subagents/</c> beside it. Claudette reads them back when the tab is restored.
/// </summary>
/// <param name="agentId">Set for a subagent's transcript, whose entries are all a sidechain.</param>
internal sealed class DemoTranscript(string sessionId, string folder, string branch, DateTimeOffset start, string? agentId = null)
{
    private readonly List<string> _lines = [];
    private readonly List<(string AgentId, JsonObject Meta, DemoTranscript Transcript)> _subagents = [];
    private readonly string _prefix = (agentId ?? sessionId)[..8];
    private string? _parent;
    private DateTimeOffset _at = start;
    private int _ids;

    public string SessionId => sessionId;

    /// <summary>When the last entry was written.</summary>
    public DateTimeOffset At => _at;

    /// <summary>Time passing before the next entry, as it does while Claude works.</summary>
    public DemoTranscript Wait(int minutes)
    {
        _at = _at.AddMinutes(minutes);
        return this;
    }

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
    /// <param name="toolUseResult">The result's details; a failed call's are its text.</param>
    public string Tool(string name, JsonObject input, string result, JsonNode toolUseResult, bool isError = false)
    {
        var (id, source) = Call(name, input);
        Result(id, source, result, toolUseResult, isError);
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

    /// <summary>A Grep listing the files that match.</summary>
    public DemoTranscript Grep(string pattern, string path, params string[] files)
    {
        Tool("Grep", new JsonObject { ["pattern"] = pattern, ["path"] = path, ["output_mode"] = "files_with_matches" },
            $"Found {files.Length} file{(files.Length == 1 ? "" : "s")}\n{string.Join('\n', files)}",
            new JsonObject { ["mode"] = "files_with_matches", ["filenames"] = new JsonArray([.. files.Select(f => (JsonNode?)f)]), ["numFiles"] = files.Length });
        return this;
    }

    /// <summary>An Edit of <paramref name="path"/>, which held <paramref name="original"/>.</summary>
    public string Edit(string path, string original, string oldString, string newString)
    {
        (original, oldString, newString) = (Lf(original), Lf(oldString), Lf(newString));
        var line = original[..original.IndexOf(oldString, StringComparison.Ordinal)].Split('\n').Length;
        var (removed, added) = (oldString.Split('\n'), newString.Split('\n'));
        // The lines the old and new strings share at either end are context, as in Claude Code's own patches.
        var same = removed.Zip(added).TakeWhile(p => p.First == p.Second).Count();
        var sameAfter = removed.Skip(same).Reverse().Zip(added.Skip(same).Reverse()).TakeWhile(p => p.First == p.Second).Count();
        var patch = new JsonObject
        {
            ["oldStart"] = line,
            ["oldLines"] = removed.Length,
            ["newStart"] = line,
            ["newLines"] = added.Length,
            ["lines"] = new JsonArray(
            [
                .. removed.Take(same).Select(l => (JsonNode?)$" {l}"),
                .. removed[same..^sameAfter].Select(l => (JsonNode?)$"-{l}"),
                .. added[same..^sameAfter].Select(l => (JsonNode?)$"+{l}"),
                .. removed[^sameAfter..].Select(l => (JsonNode?)$" {l}"),
            ]),
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

    /// <summary>A plan put up for review with ExitPlanMode, approved, or sent back with <paramref name="feedback"/>.</summary>
    public DemoTranscript Plan(string plan, string? feedback = null)
    {
        plan = Lf(plan);
        if (feedback is null)
        {
            Tool("ExitPlanMode", new JsonObject(), $"User has approved your plan. You can now start coding.\n\n## Approved Plan:\n{plan}",
                new JsonObject { ["plan"] = plan, ["isAgent"] = false });
        }
        else
        {
            // Before 2.1.285 the call carried the plan, which is how a restored tab has one that was sent back.
            var answer = $"The user doesn't want to proceed with this tool use. The tool use was rejected. To tell you how to proceed, the user said:\n{feedback}";
            Tool("ExitPlanMode", new JsonObject { ["plan"] = plan }, answer, JsonValue.Create($"Error: {answer}"), isError: true);
        }
        return this;
    }

    /// <summary>A TaskCreate call, which Claude Code numbers <paramref name="id"/>.</summary>
    public DemoTranscript TaskCreate(string id, string subject, string description, string activeForm)
    {
        Tool("TaskCreate", new JsonObject { ["subject"] = subject, ["description"] = description, ["activeForm"] = activeForm },
            $"Task #{id} created successfully: {subject}", new JsonObject { ["task"] = new JsonObject { ["id"] = id, ["subject"] = subject } });
        return this;
    }

    /// <summary>A TaskUpdate call: the task's status, who has it, and the tasks it waits on.</summary>
    public DemoTranscript TaskUpdate(string id, string? status = null, string? owner = null, string[]? blockedBy = null)
    {
        var input = new JsonObject { ["taskId"] = id };
        var fields = new JsonArray();
        if (status is not null)
        {
            input["status"] = status;
            fields.Add("status");
        }
        if (owner is not null)
        {
            input["owner"] = owner;
            fields.Add("owner");
        }
        if (blockedBy is { Length: > 0 })
        {
            input["addBlockedBy"] = new JsonArray([.. blockedBy.Select(b => (JsonNode?)b)]);
            fields.Add("blockedBy");
        }
        Tool("TaskUpdate", input, $"Updated task #{id} {string.Join(", ", fields.Select(f => f!.GetValue<string>()))}",
            new JsonObject { ["success"] = true, ["taskId"] = id, ["updatedFields"] = fields });
        return this;
    }

    /// <summary>A thread's SendMessage to a sub-thread, which Claudette's hook delivered (DESIGN.md §18, "Threads").</summary>
    public DemoTranscript SendMessage(string to, string summary, string message)
    {
        var answer = ThreadMessages.Delivered(to, waits: false);
        Tool("SendMessage", new JsonObject { ["to"] = to, ["summary"] = summary, ["message"] = Lf(message) }, answer, JsonValue.Create($"Error: {answer}"), isError: true);
        return this;
    }

    /// <summary>
    /// Subagents started together, as parallel Agent calls: each does its work in a transcript of its own, alongside the
    /// others, then the main transcript gets their reports.
    /// </summary>
    public DemoTranscript Agents(params DemoAgent[] agents)
    {
        var calls = agents.Select(a =>
        {
            var (id, source) = Call("Agent", new JsonObject { ["description"] = a.Description, ["subagent_type"] = a.Type, ["prompt"] = Lf(a.Prompt) });
            return (Agent: a, Id: id, Source: source);
        }).ToList();
        var started = _at;
        var runs = calls.Select((c, i) =>
        {
            // Unique in its first characters, which its entries' ids start with.
            var agentId = $"a{i + 1:D2}{_prefix}e0c4d7b19";
            var run = new DemoTranscript(sessionId, folder, branch, started.AddSeconds(1 + i), agentId);
            run.Add(run.Entry("user", run.Uuid(), 0, new JsonObject { ["role"] = "user", ["content"] = Lf(c.Agent.Prompt) }));
            c.Agent.Work(run);
            run.Text(c.Agent.Report, last: true);
            _subagents.Add((agentId, new JsonObject
            {
                ["agentType"] = c.Agent.Type, ["description"] = c.Agent.Description, ["toolUseId"] = c.Id, ["spawnDepth"] = 1,
            }, run));
            return (Call: c, AgentId: agentId, Run: run);
        }).ToList();
        _at = runs.Max(r => r.Run.At);
        foreach (var (call, id, run) in runs)
        {
            var report = Lf(call.Agent.Report);
            Result(call.Id, call.Source, report, new JsonObject
            {
                ["status"] = "completed",
                ["prompt"] = Lf(call.Agent.Prompt),
                ["agentId"] = id,
                ["agentType"] = call.Agent.Type,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = report }),
                ["totalDurationMs"] = (long)(run.At - started).TotalMilliseconds,
                ["totalTokens"] = 18_000 + 9_000 * run._ids,
                ["totalToolUseCount"] = run._lines.Count / 2 - 1,
            });
        }
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
        if (_subagents.Count > 0)
        {
            var subagents = Path.Combine(directory, sessionId, "subagents");
            Directory.CreateDirectory(subagents);
            foreach (var (id, meta, run) in _subagents)
            {
                File.WriteAllLines(Path.Combine(subagents, $"agent-{id}.jsonl"), run._lines);
                File.WriteAllText(Path.Combine(subagents, $"agent-{id}.meta.json"), meta.ToJsonString());
            }
        }
        return path;
    }

    private (string Id, string Source) Call(string name, JsonObject input)
    {
        var id = $"toolu_demo_{_prefix}_{++_ids:D3}";
        return (id, Reply(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input }));
    }

    private void Result(string id, string source, string result, JsonNode toolUseResult, bool isError = false)
    {
        var block = new JsonObject { ["tool_use_id"] = id, ["type"] = "tool_result", ["content"] = result };
        if (isError)
        {
            block["is_error"] = true;
        }
        var line = Entry("user", Uuid(), 2, new JsonObject { ["role"] = "user", ["content"] = new JsonArray(block) });
        line["toolUseResult"] = toolUseResult;
        line["sourceToolAssistantUUID"] = source;
        Add(line);
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
        var entry = new JsonObject
        {
            ["parentUuid"] = _parent,
            ["isSidechain"] = agentId is not null,
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
        if (agentId is not null)
        {
            entry["agentId"] = agentId;
        }
        return entry;
    }

    private void Add(JsonObject line)
    {
        _lines.Add(line.ToJsonString());
        _parent = line["uuid"]!.GetValue<string>();
    }

    /// <summary>Files on disk have LF line endings, so their contents in the transcript do too, whatever the checkout's.</summary>
    private static string Lf(string text) => text.ReplaceLineEndings("\n");

    private string Uuid() => $"{_prefix}-0000-4000-8000-{++_ids:D12}";
}

/// <summary>A subagent for <see cref="DemoTranscript.Agents"/>: what it was asked, the work in its transcript, and its report.</summary>
internal sealed record DemoAgent(string Description, string Type, string Prompt, Action<DemoTranscript> Work, string Report);
