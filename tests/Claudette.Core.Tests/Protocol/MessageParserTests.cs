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

    // ---- Typed views of system messages and fields -------------------------------------------------------------

    [Fact]
    public void Reads_a_hook_run()
    {
        // As Claude Code 2.1.284 sends them with --include-hook-events.
        var started = Parse<SystemMessage>("""{"type":"system","subtype":"hook_started","hook_id":"h1","hook_name":"PreToolUse:Bash","hook_event":"PreToolUse","uuid":"x","session_id":"s1"}""").HookRun;
        var progress = Parse<SystemMessage>("""{"type":"system","subtype":"hook_progress","hook_id":"h1","hook_name":"PreToolUse:Bash","hook_event":"PreToolUse","output":"step 1","stdout":"step 1\n","stderr":"","session_id":"s1"}""").HookRun;
        var response = Parse<SystemMessage>("""{"type":"system","subtype":"hook_response","hook_id":"h1","hook_name":"PreToolUse:Bash","hook_event":"PreToolUse","output":"","stdout":"","stderr":"Blocked: rm -rf","exit_code":2,"outcome":"error","uuid":"y","session_id":"s1"}""").HookRun;

        Assert.Equal(new HookRunNotice(HookRunStage.Started, "h1", "PreToolUse:Bash", "PreToolUse", null, null, null, null, HookOutcome.Unknown), started);
        Assert.Equal((HookRunStage.Progress, "step 1", "step 1\n", ""), (progress!.Stage, progress.Output, progress.Stdout, progress.Stderr));
        Assert.Equal((HookRunStage.Response, 2, HookOutcome.Error, "Blocked: rm -rf"), (response!.Stage, response.ExitCode, response.Outcome, response.Stderr));
    }

    [Theory]
    [InlineData("success", HookOutcome.Success)]
    [InlineData("cancelled", HookOutcome.Cancelled)]
    [InlineData("timed_out", HookOutcome.Unknown)]
    public void A_hook_outcome_Claudette_does_not_know_reads_as_unknown(string outcome, HookOutcome expected)
    {
        var hook = Parse<SystemMessage>($$"""{"type":"system","subtype":"hook_response","hook_id":"h1","outcome":"{{outcome}}"}""").HookRun;

        Assert.Equal(expected, hook!.Outcome);
    }

    [Theory]
    [InlineData("""{"type":"system","subtype":"hook_started"}""")]
    [InlineData("""{"type":"system","subtype":"hook_started","hook_id":""}""")]
    [InlineData("""{"type":"system","subtype":"hook_response","hook_id":7,"outcome":"error"}""")]
    public void A_hook_message_without_an_id_has_no_run(string line) =>
        Assert.Null(Parse<SystemMessage>(line).HookRun);

    [Fact]
    public void Odd_hook_fields_read_as_missing()
    {
        var hook = Parse<SystemMessage>("""{"type":"system","subtype":"hook_response","hook_id":"h1","hook_name":3,"hook_event":null,"output":["a"],"exit_code":"2","outcome":true,"extra":{}}""").HookRun;

        Assert.Equal(new HookRunNotice(HookRunStage.Response, "h1", null, null, null, null, null, null, HookOutcome.Unknown), hook);
    }

    [Fact]
    public void Reads_api_retry()
    {
        var retry = ParseAll<SystemMessage>("11-api-retry").Select(m => m.ApiRetry).OfType<ApiRetryNotice>().First();

        Assert.Equal(new ApiRetryNotice(1, 10, 567, 529, "overloaded"), retry);
    }

    [Fact]
    public void An_api_retry_prefers_the_error_category_and_reads_odd_fields_as_missing()
    {
        var categorized = Parse<SystemMessage>("""{"type":"system","subtype":"api_retry","attempt":2,"retry_delay_ms":4000.7,"error_status":null,"error_category":"rate_limit","error":"other"}""").ApiRetry;
        var bare = Parse<SystemMessage>("""{"type":"system","subtype":"api_retry","attempt":"2","max_retries":true,"error_category":5}""").ApiRetry;

        Assert.Equal(new ApiRetryNotice(2, null, 4000, null, "rate_limit"), categorized);
        Assert.Equal(new ApiRetryNotice(null, null, null, null, null), bare);
    }

    [Fact]
    public void Reads_compact_boundary()
    {
        var compacted = ParseAll<SystemMessage>("10-compact").Select(m => m.CompactBoundary).OfType<CompactBoundaryNotice>().Single();
        var automatic = Parse<SystemMessage>("""{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"auto"}}""").CompactBoundary;
        var bare = Parse<SystemMessage>("""{"type":"system","subtype":"compact_boundary","compact_metadata":"auto"}""").CompactBoundary;

        Assert.Equal(("manual", false), (compacted.Trigger, compacted.IsAutomatic));
        Assert.True(automatic!.IsAutomatic);
        Assert.Equal(((string?)null, false), (bare!.Trigger, bare.IsAutomatic));
    }

    [Fact]
    public void Reads_whether_Claude_Code_is_compacting_from_its_status()
    {
        var statuses = ParseAll<SystemMessage>("10-compact").Select(m => m.Status).OfType<StatusNotice>().ToList();
        // Recorded: requesting, then compacting, then done.
        Assert.Equal(["requesting", "compacting", null], statuses.Select(s => s.Status));
        Assert.Equal([false, true, false], statuses.Select(s => s.IsCompacting));
        var compacting = statuses[1];
        var done = Parse<SystemMessage>("""{"type":"system","subtype":"status","status":null,"session_id":"s"}""").Status;
        var mode = Parse<SystemMessage>("""{"type":"system","subtype":"status","permissionMode":"plan"}""").Status;
        var other = Parse<SystemMessage>("""{"type":"system","subtype":"status","status":"thinking-hard"}""").Status;

        Assert.True(compacting.IsCompacting);
        Assert.False(done!.IsCompacting);
        Assert.Equal(((string?)null, false), (mode!.Status, mode.IsCompacting));
        Assert.False(other!.IsCompacting);
        Assert.Null(Parse<SystemMessage>("""{"type":"system","subtype":"compact_boundary"}""").Status);
    }

    [Theory]
    [InlineData("info", NoticeLevel.Info)]
    [InlineData("notice", NoticeLevel.Notice)]
    [InlineData("suggestion", NoticeLevel.Suggestion)]
    [InlineData("warning", NoticeLevel.Warning)]
    [InlineData("someday", NoticeLevel.Unknown)]
    public void Reads_an_informational_notice(string level, NoticeLevel expected)
    {
        // As the Agent SDK documents system/informational: here, a hook's message to the user.
        var notice = Parse<SystemMessage>($$"""{"type":"system","subtype":"informational","content":"PostToolUse:Bash says: Formatted 3 files","level":"{{level}}","tool_use_id":"t1","uuid":"i-1","session_id":"s"}""").Informational;

        Assert.Equal(new InformationalNotice("PostToolUse:Bash says: Formatted 3 files", expected), notice);
    }

    [Fact]
    public void Odd_informational_fields_read_as_missing() =>
        Assert.Equal(new InformationalNotice(null, NoticeLevel.Unknown), Parse<SystemMessage>("""{"type":"system","subtype":"informational","content":["x"],"level":2}""").Informational);

    [Fact]
    public void Reads_permission_denied()
    {
        var withReason = Parse<SystemMessage>("""{"type":"system","subtype":"permission_denied","tool_name":"Bash","tool_use_id":"t1","decision_reason":"Matched a deny rule","message":"denied"}""").PermissionDenied;
        var withMessage = Parse<SystemMessage>("""{"type":"system","subtype":"permission_denied","tool_name":"Bash","decision_reason":null,"message":"denied"}""").PermissionDenied;
        var bare = Parse<SystemMessage>("""{"type":"system","subtype":"permission_denied","tool_name":{}}""").PermissionDenied;

        Assert.Equal(new PermissionDeniedNotice("Bash", "Matched a deny rule"), withReason);
        Assert.Equal(new PermissionDeniedNotice("Bash", "denied"), withMessage);
        Assert.Equal(new PermissionDeniedNotice(null, null), bare);
    }

    [Fact]
    public void Reads_elicitation_complete()
    {
        var complete = Parse<SystemMessage>("""{"type":"system","subtype":"elicitation_complete","mcp_server_name":"auth","elicitation_id":"el-1"}""").ElicitationComplete;

        Assert.Equal("el-1", complete!.ElicitationId);
        Assert.Null(Parse<SystemMessage>("""{"type":"system","subtype":"elicitation_complete","elicitation_id":1}""").ElicitationComplete);
    }

    [Fact]
    public void Other_system_messages_have_no_typed_views()
    {
        var others = ProtocolFixture.AllNames().SelectMany(ParseAll<SystemMessage>).Where(m => m.Subtype is not ("api_retry" or "compact_boundary")).ToArray();
        var retry = Parse<SystemMessage>("""{"type":"system","subtype":"api_retry","hook_id":"h1","elicitation_id":"el-1","tool_name":"Bash","content":"x"}""");

        Assert.Contains(others, m => m.Subtype == "status");
        Assert.All(others, m => Assert.True(
            m.HookRun is null && m.ApiRetry is null && m.CompactBoundary is null && m.Informational is null && m.PermissionDenied is null
            && m.ElicitationComplete is null, m.Subtype));
        // Each view goes by the subtype alone.
        Assert.Equal((false, true, false, false, false, false),
            (retry.HookRun is not null, retry.ApiRetry is not null, retry.CompactBoundary is not null, retry.Informational is not null,
                retry.PermissionDenied is not null, retry.ElicitationComplete is not null));
    }

    [Fact]
    public void Counts_the_mcp_servers_in_system_init()
    {
        Assert.Equal(0, ParseFirst<SystemInitMessage>("01-mock-basic").McpServerCount);
        Assert.Equal(2, Parse<SystemInitMessage>("""{"type":"system","subtype":"init","session_id":"s","mcp_servers":[{"name":"a","status":"connected"},{"name":"b","status":"failed"}]}""").McpServerCount);
        Assert.Equal(0, Parse<SystemInitMessage>("""{"type":"system","subtype":"init","session_id":"s","mcp_servers":{"a":1}}""").McpServerCount);
    }

    [Fact]
    public void Reads_an_assistant_messages_uuid_and_resume_reason()
    {
        var rerun = Parse<AssistantMessage>("""{"type":"assistant","uuid":"a-1","resume_reason":"interrupted_turn","message":{"content":[]}}""");
        var odd = Parse<AssistantMessage>("""{"type":"assistant","uuid":1,"resume_reason":{},"message":{"content":[]}}""");

        Assert.Equal(("a-1", "interrupted_turn"), (rerun.Uuid, rerun.ResumeReason));
        Assert.Equal(((string?)null, (string?)null), (odd.Uuid, odd.ResumeReason));
        Assert.Null(ParseFirst<AssistantMessage>("01-mock-basic").ResumeReason);
    }

    [Theory]
    [InlineData("""{"type":"user","isReplay":true,"message":{"role":"user","content":"one"}}""", "one")]
    [InlineData("""{"type":"user","isReplay":true,"message":{"role":"user","content":[{"type":"text","text":"two"},{"type":"image","source":{}},{"type":"text","text":"lines"}]}}""", "two\nlines")]
    [InlineData("""{"type":"user","isReplay":true,"message":{"role":"user","content":[{"type":"image","source":{}}]}}""", "")]
    [InlineData("""{"type":"user","isReplay":true,"message":{"role":"user","content":[{"type":"text"},{"type":"text","text":"after"}]}}""", "\nafter")]
    [InlineData("""{"type":"user","isReplay":true,"message":{"role":"user","content":42}}""", null)]
    [InlineData("""{"type":"user","isReplay":true,"message":{"role":"user"}}""", null)]
    [InlineData("""{"type":"user","isReplay":true}""", null)]
    public void Reads_a_replayed_prompts_text(string line, string? text) =>
        Assert.Equal(text, Parse<UserMessage>(line).PromptText);

    [Fact]
    public void Reads_which_calls_were_interrupted()
    {
        var results = Parse<UserMessage>("""{"type":"user","message":{"content":[]},"tool_result_meta":[{"id":"t1","non_execution_kind":"interrupted"},{"id":"t2","non_execution_kind":"permission-rule"},"odd"]}""");
        var denied = ParseAll<UserMessage>("07-permission-denied").Single(u => u.Raw["tool_result_meta"] is not null);

        Assert.True(results.WasInterrupted("t1"));
        Assert.False(results.WasInterrupted("t2"));
        Assert.False(results.WasInterrupted("t3"));
        Assert.False(denied.WasInterrupted("toolu_mock_1"));
        Assert.False(Parse<UserMessage>("""{"type":"user","message":{"content":[]},"tool_result_meta":{"id":"t1"}}""").WasInterrupted("t1"));
    }

    private static T Parse<T>(string line) where T : ClaudeMessage
    {
        Assert.True(MessageParser.TryParse(line, out var message, out var error), error);
        return Assert.IsType<T>(message);
    }

    private static T ParseFirst<T>(string fixture) where T : ClaudeMessage => ParseAll<T>(fixture).First();

    private static IEnumerable<T> ParseAll<T>(string fixture) where T : ClaudeMessage =>
        ProtocolFixture.Load(fixture).OutputLines
            .Select(l => MessageParser.TryParse(l, out var m, out _) ? m : null)
            .OfType<T>();
}
