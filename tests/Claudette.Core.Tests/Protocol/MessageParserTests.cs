using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Protocol;

public class MessageParserTests
{
    /// <summary>Every recording of every version.</summary>
    public static TheoryData<string, string> Fixtures() =>
        new(ProtocolFixture.AllVersions().SelectMany(version => ProtocolFixture.AllNames(version).Select(name => (version, name))));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_recorded_line_parses(string version, string fixture)
    {
        foreach (var line in ProtocolFixture.Load(fixture, version).OutputLines)
        {
            Assert.True(MessageParser.TryParse(line, out _, out var error), $"{version}/{fixture}: {error}\n{line}");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Recorded_message_types_are_all_known(string version, string fixture)
    {
        foreach (var line in ProtocolFixture.Load(fixture, version).OutputLines)
        {
            MessageParser.TryParse(line, out var message, out _);
            Assert.IsNotType<UnknownMessage>(message);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Recorded_fields_are_all_known_to_diagnostics(string version, string fixture)
    {
        // Diagnostics counts fields the tested version didn't have (DESIGN.md §16), so it must know all of these.
        var diagnostics = new ProtocolDiagnostics();
        foreach (var line in ProtocolFixture.Load(fixture, version).OutputLines)
        {
            MessageParser.TryParse(line, out var message, out _);
            diagnostics.RecordFields(message!);
        }

        Assert.Empty(diagnostics.Snapshot().UnknownFields);
    }

    [Theory]
    [InlineData("""{"type":"active_goal","value":null,"uuid":"u1","session_id":"s1"}""")]
    [InlineData("""{"type":"command_lifecycle","command_uuid":"bbbbbbbb-0000-4000-8000-000000000002","state":"queued","uuid":"u2","session_id":"s1"}""")]
    public void Types_with_no_use_are_skipped_without_counting_as_unknown(string line)
    {
        Assert.True(MessageParser.TryParse(line, out var message, out _));

        Assert.IsType<IgnoredMessage>(message);
    }

    [Fact]
    public void A_conversation_reset_carries_its_trigger_and_new_id()
    {
        Assert.True(MessageParser.TryParse("""{"type":"conversation_reset","trigger":"clear","new_conversation_id":"c2","session_id":"s1"}""", out var message, out _));

        var reset = Assert.IsType<ConversationResetMessage>(message);
        Assert.Equal("clear", reset.Trigger);
        Assert.Equal("c2", reset.NewConversationId);
    }

    [Fact]
    public void Reads_system_init()
    {
        var init = ParseFirst<SystemInitMessage>("01-mock-basic");

        Assert.Equal("claude-haiku-4-5", init.Model);
        Assert.Equal("default", init.PermissionMode);
        Assert.Equal("2.1.284", init.ClaudeCodeVersion);
        Assert.Contains("interrupt_receipt_v1", init.Capabilities);
        Assert.NotEmpty(init.SessionId);
    }

    [Fact]
    public void Reads_permission_request()
    {
        var request = ParseAll<ControlRequestMessage>("02-control-protocol").First(r => r.Subtype == "can_use_tool");

        Assert.Equal("Write", request.Request["tool_name"]!.GetValue<string>());
        Assert.NotEmpty(request.RequestId);
    }

    [Fact]
    public void Reads_edit_result_with_original_file()
    {
        var edit = ParseAll<UserMessage>("06-edit-original-file").Single(u => u.ToolUseResult?["oldString"] is not null);

        Assert.Equal("first line\nORIGINAL LINE\nlast line\n", edit.ToolUseResult!["originalFile"]!.GetValue<string>());
        Assert.IsType<ToolResultBlock>(Assert.Single(edit.Content));
    }

    [Fact]
    public void Reads_local_command_output()
    {
        var note = ParseAll<UserMessage>("02-control-protocol").Select(u => u.LocalCommandOutput).OfType<string>().Single();

        Assert.StartsWith("Set model to", note);
    }

    [Fact]
    public void Reads_text_deltas()
    {
        var deltas = ParseAll<StreamEventMessage>("02-control-protocol").Select(s => s.TextDelta).OfType<string>().ToArray();

        Assert.Contains("pong", string.Concat(deltas));
    }

    [Fact]
    public void Reads_interrupted_result()
    {
        var result = ParseAll<ResultMessage>("02-control-protocol").Last();

        Assert.True(result.IsError);
        Assert.Equal("error_during_execution", result.Subtype);
        Assert.Equal("aborted_streaming", result.TerminalReason);
    }

    [Fact]
    public void Reads_context_usage()
    {
        var response = ParseAll<ControlResponseMessage>("02-control-protocol").Single(r => r.Response?["percentage"] is not null);

        var usage = Claudette.Core.Sessions.ContextUsage.Parse(response.Response!);

        Assert.Equal(200_000, usage.MaxTokens);
        Assert.True(usage.TotalTokens > 0);
        Assert.Equal(2, usage.Percentage);
        Assert.Equal((167_000L, true), (usage.AutoCompactThreshold, usage.AutoCompactEnabled));

        // What fills it (DESIGN.md §6, "Per-tab context"), as /context shows it.
        Assert.Equal(
            [("System prompt", 1400L, ContextCategoryKind.Used), ("Skills", 1830L, ContextCategoryKind.Used), ("Free space", 196_770L, ContextCategoryKind.Free)],
            usage.Categories.Select(c => (c.Name, c.Tokens, c.Kind)));
        Assert.Empty(usage.MemoryFiles);
        Assert.Empty(usage.McpTools);
        Assert.Empty(usage.Agents);
        Assert.Equal((12, 12, 1830L), (usage.Skills!.Total, usage.Skills.Included, usage.Skills.Tokens));
        Assert.Equal(("dataviz", "built-in", 482L), (usage.Skills.Listed[0].Name, usage.Skills.Listed[0].Source, usage.Skills.Listed[0].Tokens));
        Assert.Equal(12, usage.Skills.Listed.Count);
        var messages = usage.Messages!;
        Assert.Equal((43L, 56L, 98_620L, 18L, 141L), (messages.ToolCallTokens, messages.ToolResultTokens, messages.AttachmentTokens, messages.AssistantTokens, messages.UserTokens));
        Assert.Equal(new ContextToolUse("Write", 43, 56), Assert.Single(messages.ByTool));
        Assert.Equal(new ContextAttachment("prompt_snapshot", 96_206), messages.Attachments[0]);
    }

    [Fact]
    public void Reads_authentication_failure()
    {
        var assistant = ParseAll<AssistantMessage>("signed-out").Single();

        Assert.Equal("authentication_failed", assistant.Error);
    }

    [Fact]
    public void Unknown_type_is_kept_not_rejected()
    {
        Assert.True(MessageParser.TryParse("""{"type":"brand_new_thing","x":1}""", out var message, out _));

        var unknown = Assert.IsType<UnknownMessage>(message);
        Assert.Equal("brand_new_thing", unknown.MessageType);
    }

    [Fact]
    public void Reads_tool_progress_for_a_subagent()
    {
        // A foreground subagent's heartbeat, as Claude Code 2.1.284 sent it after 30 seconds.
        Assert.True(MessageParser.TryParse(
            """{"type":"tool_progress","tool_use_id":"toolu_1-heartbeat-0","tool_name":"Agent","parent_tool_use_id":"toolu_1","elapsed_time_seconds":30,"heartbeat":true}""",
            out var heartbeat, out _));
        Assert.True(MessageParser.TryParse(
            """{"type":"tool_progress","tool_use_id":"toolu_1","tool_name":"Agent","parent_tool_use_id":"toolu_1","elapsed_time_seconds":4,"subagent_type":"Explore","subagent_retry":{"agent_id":"a1","attempt":2,"max_retries":10,"retry_delay_ms":4000,"error_status":529,"error_category":"overloaded"}}""",
            out var retry, out _));

        var beat = Assert.IsType<ToolProgressMessage>(heartbeat);
        Assert.Equal("Agent", beat.ToolName);
        Assert.Equal("toolu_1", beat.ParentToolUseId);
        Assert.Equal(30, beat.ElapsedSeconds);
        Assert.True(beat.IsHeartbeat);
        var retrying = Assert.IsType<ToolProgressMessage>(retry);
        Assert.False(retrying.IsHeartbeat);
        Assert.Equal("overloaded", retrying.Raw.GetObject("subagent_retry")?.GetString("error_category"));
    }

    [Fact]
    public void Unknown_fields_and_wrong_types_are_ignored()
    {
        Assert.True(MessageParser.TryParse("""{"type":"result","subtype":"success","is_error":"yes","result":42,"extra":{"a":1}}""", out var message, out _));

        var result = Assert.IsType<ResultMessage>(message);
        Assert.False(result.IsError);
        Assert.Null(result.Result);
    }

    [Fact]
    public void A_repeated_key_reads_as_its_last_value()
    {
        Assert.True(MessageParser.TryParse("""{"type":"result","subtype":"error","subtype":"success","result":"a","result":"b"}""", out var message, out var error), error);

        var result = Assert.IsType<ResultMessage>(message);
        Assert.Equal("b", result.Result);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    public void Bad_lines_are_reported_not_thrown(string line)
    {
        Assert.False(MessageParser.TryParse(line, out _, out var error));
        Assert.NotEmpty(error);
    }

    private static T ParseFirst<T>(string fixture) where T : ClaudeMessage => ParseAll<T>(fixture).First();

    private static IEnumerable<T> ParseAll<T>(string fixture) where T : ClaudeMessage =>
        ProtocolFixture.Load(fixture).OutputLines
            .Select(l => MessageParser.TryParse(l, out var m, out _) ? m : null)
            .OfType<T>();
}
