using Claudette.Core.Protocol;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Protocol;

public class MessageParserTests
{
    public static TheoryData<string> Fixtures() => new(ProtocolFixture.AllNames());

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_recorded_line_parses(string fixture)
    {
        foreach (var line in ProtocolFixture.Load(fixture).OutputLines)
        {
            Assert.True(MessageParser.TryParse(line, out _, out var error), $"{fixture}: {error}\n{line}");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Recorded_message_types_are_all_known(string fixture)
    {
        foreach (var line in ProtocolFixture.Load(fixture).OutputLines)
        {
            MessageParser.TryParse(line, out var message, out _);
            Assert.IsNotType<UnknownMessage>(message);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Recorded_fields_are_all_known_to_diagnostics(string fixture)
    {
        // Diagnostics counts fields the tested version didn't have (DESIGN.md §16), so it must know all of these.
        var diagnostics = new ProtocolDiagnostics();
        foreach (var line in ProtocolFixture.Load(fixture).OutputLines)
        {
            MessageParser.TryParse(line, out var message, out _);
            diagnostics.RecordFields(message!);
        }

        Assert.Empty(diagnostics.Snapshot().UnknownFields);
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
