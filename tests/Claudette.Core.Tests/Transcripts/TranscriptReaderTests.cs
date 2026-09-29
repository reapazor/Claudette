using Claudette.Core.Protocol;
using Claudette.Core.Tests.Support;
using Claudette.Core.Transcripts;

namespace Claudette.Core.Tests.Transcripts;

public class TranscriptReaderTests
{
    private static Transcript Load(string name = "tools-and-titles") =>
        TranscriptReader.Read(File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "transcripts", "2.1.284", $"{name}.jsonl")));

    [Fact]
    public void Reads_prompts_assistant_messages_and_tool_results_in_order()
    {
        var transcript = Load();

        var kinds = transcript.Items.Select(i => i switch
        {
            TranscriptPrompt p => $"prompt:{p.Text.Split(' ')[0]}",
            TranscriptMessage { Message: AssistantMessage a } => $"assistant:{a.Content[0].Type}",
            TranscriptMessage { Message: UserMessage } => "results",
            _ => i.GetType().Name,
        });
        Assert.Equal(
            ["prompt:WRITE_FILE", "assistant:tool_use", "results", "assistant:text", "prompt:RUN_BASH", "assistant:tool_use", "results", "assistant:text"],
            kinds);
    }

    [Fact]
    public void Tool_results_carry_the_structured_result()
    {
        var results = Load().Items.OfType<TranscriptMessage>().Select(m => m.Message).OfType<UserMessage>().First();

        Assert.Equal("create", results.ToolUseResult?["type"]?.GetValue<string>());
    }

    [Fact]
    public void Reads_the_titles()
    {
        var transcript = Load();

        Assert.Equal("Mock session title", transcript.AiTitle);
        Assert.Equal("Renamed by host", transcript.CustomTitle);
        Assert.Equal("Renamed by host", transcript.Title);
    }

    [Fact]
    public void Prompts_have_injected_reminders_removed()
    {
        var prompts = Load().Items.OfType<TranscriptPrompt>().Select(p => p.Text);

        Assert.All(prompts, p => Assert.DoesNotContain("<system-reminder>", p, StringComparison.Ordinal));
    }

    [Fact]
    public void Bad_and_unknown_lines_are_skipped()
    {
        var transcript = TranscriptReader.Read(
        [
            "not json",
            """{"type":"brand-new-entry","x":1}""",
            """{"type":"user","message":{"role":"user","content":"hello"}}""",
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"hidden"}}""",
            """{"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model to sonnet</local-command-stdout>"}}""",
            """{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"subagent"}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""",
        ]);

        Assert.Equal("hello", Assert.IsType<TranscriptPrompt>(transcript.Items[0]).Text);
        Assert.Equal("Set model to sonnet", Assert.IsType<TranscriptNote>(transcript.Items[1]).Text);
        Assert.Equal("Stopped.", Assert.IsType<TranscriptNote>(transcript.Items[2]).Text);
        Assert.Equal(3, transcript.Items.Count);
    }

    [Fact]
    public void A_background_task_notification_is_read_as_one_not_as_a_prompt()
    {
        // As Claude Code 2.1.284 records a background subagent finishing: a user turn it gave the model.
        var notification = """
            <task-notification>
            <task-id>a18958</task-id>
            <tool-use-id>toolu_2</tool-use-id>
            <output-file>/tmp/tasks/a18958.output</output-file>
            <status>completed</status>
            <summary>Agent "Delegate deeper" finished</summary>
            <result>Found 3 matches.
            In src/.</result>
            <usage><subagent_tokens>1020</subagent_tokens><tool_uses>1</tool_uses><duration_ms>207</duration_ms></usage>
            </task-notification>
            """;
        var line = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "user",
            ["origin"] = new System.Text.Json.Nodes.JsonObject { ["kind"] = "task-notification" },
            ["message"] = new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = notification },
        }.ToJsonString();

        var item = Assert.IsType<TranscriptTaskNotification>(Assert.Single(TranscriptReader.Read([line]).Items));

        Assert.Equal("a18958", item.TaskId);
        Assert.Equal("toolu_2", item.ToolUseId);
        Assert.Equal("completed", item.Status);
        Assert.Equal("Found 3 matches.\nIn src/.", item.Result);
        Assert.Equal(1020, item.Tokens);
        Assert.Equal(1, item.ToolUses);
        Assert.Equal(207, item.DurationMs);
    }

    [Fact]
    public async Task Subagent_transcripts_are_merged_in_under_their_agent_call()
    {
        // The layout Claude Code 2.1.284 writes: <session>.jsonl, and <session>/subagents/agent-<id>.jsonl with a
        // .meta.json naming the Agent call. Nested subagents get their own file, pointing at the call in their parent's.
        using var folder = new TempFolder();
        var main = folder.Write("s1.jsonl", string.Join('\n',
            """{"type":"user","timestamp":"2026-09-29T05:00:00.000Z","message":{"role":"user","content":"Look into it"}}""",
            """{"type":"assistant","timestamp":"2026-09-29T05:00:01.000Z","message":{"content":[{"type":"tool_use","id":"toolu_a","name":"Agent","input":{"description":"Search","prompt":"Search for it"}}]}}""",
            """{"type":"user","timestamp":"2026-09-29T05:00:09.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_a","content":"x"}]},"toolUseResult":{"status":"completed","content":[{"type":"text","text":"Found it."}]}}""",
            """{"type":"assistant","timestamp":"2026-09-29T05:00:10.000Z","message":{"content":[{"type":"text","text":"All done."}]}}"""));
        folder.Write("s1/subagents/agent-a1.meta.json", """{"agentType":"general-purpose","description":"Search","toolUseId":"toolu_a","spawnDepth":1}""");
        folder.Write("s1/subagents/agent-a1.jsonl", string.Join('\n',
            """{"isSidechain":true,"type":"user","timestamp":"2026-09-29T05:00:01.500Z","message":{"role":"user","content":"Search for it"}}""",
            """{"isSidechain":true,"type":"assistant","timestamp":"2026-09-29T05:00:02.000Z","message":{"content":[{"type":"tool_use","id":"toolu_b","name":"Agent","input":{"description":"Deeper"}}]}}""",
            """{"isSidechain":true,"type":"user","timestamp":"2026-09-29T05:00:08.000Z","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_b","content":"deep"}]}}"""));
        folder.Write("s1/subagents/agent-a2.meta.json", """{"agentType":"Explore","description":"Deeper","toolUseId":"toolu_b","parentAgentId":"a1","spawnDepth":2}""");
        folder.Write("s1/subagents/agent-a2.jsonl",
            """{"isSidechain":true,"type":"assistant","timestamp":"2026-09-29T05:00:05.000Z","message":{"content":[{"type":"text","text":"Deep result"}]}}""");
        // Without its .meta.json there's no telling where a subagent belongs: skipped.
        folder.Write("s1/subagents/agent-a3.jsonl",
            """{"isSidechain":true,"type":"assistant","timestamp":"2026-09-29T05:00:03.000Z","message":{"content":[{"type":"text","text":"orphan"}]}}""");

        var transcript = await TranscriptReader.ReadAsync(main, TestContext.Current.CancellationToken);

        var order = transcript.Items.Select(i => i switch
        {
            TranscriptPrompt p => $"prompt {p.Text}",
            TranscriptMessage { Message: AssistantMessage a } => $"assistant@{a.ParentToolUseId ?? "main"} {a.Content[0].Type}",
            TranscriptMessage { Message: UserMessage u } => $"results@{u.ParentToolUseId ?? "main"}",
            _ => i.GetType().Name,
        });
        Assert.Equal(
        [
            "prompt Look into it",
            "assistant@main tool_use",
            "assistant@toolu_a tool_use",
            "assistant@toolu_b text",
            "results@toolu_a",
            "results@main",
            "assistant@main text",
        ], order);
    }

    [Fact]
    public void Finds_a_transcript_in_any_project_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"claudette-projects-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "C--work-app"));
            File.WriteAllText(Path.Combine(root, "C--work-app", "abc.jsonl"), "");

            Assert.Equal(Path.Combine(root, "C--work-app", "abc.jsonl"), TranscriptReader.Find(root, "abc"));
            Assert.Null(TranscriptReader.Find(root, "missing"));
            Assert.Null(TranscriptReader.Find(Path.Combine(root, "nope"), "abc"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
