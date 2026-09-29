using Claudette.Core.Protocol;
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
