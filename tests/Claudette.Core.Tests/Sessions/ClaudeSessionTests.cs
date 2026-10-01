using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Sessions;

public class ClaudeSessionTests
{
    private readonly FakeTransport _transport = new();
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task Initialize_reads_models_and_becomes_idle()
    {
        await using var session = new ClaudeSession(_transport, _time);

        var result = await session.InitializeAsync(TestContext.Current.CancellationToken);

        var model = Assert.Single(result.Models);
        Assert.Equal("sonnet", model.Value);
        Assert.Equal(["low", "medium", "high"], model.SupportedEffortLevels);
        Assert.Equal("Claude Max", result.Account.SubscriptionType);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Equal("default", session.PermissionMode);
    }

    [Fact]
    public async Task Initialize_reads_which_models_support_auto_mode()
    {
        // Claude Code 2.1.284 sets supportsAutoMode on the models that support it and leaves it out for Haiku.
        _transport.InitializeResponse["models"] = new JsonArray(
            new JsonObject { ["value"] = "opus", ["displayName"] = "Opus", ["supportsAutoMode"] = true },
            new JsonObject { ["value"] = "haiku", ["displayName"] = "Haiku" });
        await using var session = new ClaudeSession(_transport, _time);

        var result = await session.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal([true, false], result.Models.Select(m => m.SupportsAutoMode));
    }

    [Fact]
    public async Task A_reply_that_arrives_before_the_write_finishes_still_ends_the_turn()
    {
        // A local command such as /cost can be answered before the send's continuation runs.
        await using var session = new ClaudeSession(_transport, _time);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        _transport.HoldSends = new TaskCompletionSource();

        var send = session.SendUserMessageAsync("/cost", TestContext.Current.CancellationToken).AsTask();
        Assert.Equal(SessionState.Working, session.State);
        _transport.Emit("""{"type":"result","subtype":"success","is_error":false,"result":"Total cost: $0.00"}""");
        await session.ReadUntilAsync<TurnCompleted>();
        _transport.HoldSends.SetResult();
        await send;

        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task A_send_that_fails_leaves_the_session_idle()
    {
        await using var session = new ClaudeSession(_transport, _time);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        _transport.HoldSends = new TaskCompletionSource();
        _transport.HoldSends.SetException(new IOException("The pipe is broken."));

        await Assert.ThrowsAsync<IOException>(() => session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Sending_a_message_writes_a_user_line_and_starts_working()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);

        var sent = await _transport.WaitForSentAsync(m => Type(m) == "user");
        Assert.Equal("hello", sent["message"]!["content"]!.GetValue<string>());
        Assert.Equal("user", sent["message"]!["role"]!.GetValue<string>());
        Assert.Equal(SessionState.Working, session.State);
    }

    [Fact]
    public async Task Streams_text_and_completes_the_turn()
    {
        await using var session = await StartAsync();
        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);

        _transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-sonnet-5-5","permissionMode":"default","capabilities":["interrupt_receipt_v1"]}""");
        _transport.Emit("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"po"}},"parent_tool_use_id":null}""");
        _transport.Emit("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"ng"}},"parent_tool_use_id":null}""");
        _transport.Emit("""{"type":"assistant","message":{"id":"m1","model":"claude-sonnet-5-5","content":[{"type":"text","text":"pong"}]},"parent_tool_use_id":null}""");
        _transport.Emit("""{"type":"result","subtype":"success","is_error":false,"result":"pong","terminal_reason":"completed","session_id":"s1"}""");

        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("pong", string.Concat(seen.OfType<TextDelta>().Select(d => d.Text)));
        Assert.Equal("pong", done.Result.Result);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Equal("s1", session.SessionId);
        Assert.Equal("claude-sonnet-5-5", session.Model);
        Assert.Equal(["interrupt_receipt_v1"], session.Capabilities);
    }

    [Fact]
    public async Task Allowing_a_permission_request_sends_the_answer()
    {
        await using var session = await StartAsync();
        _transport.Emit("""{"type":"control_request","request_id":"cli_1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"touch c.txt"},"tool_use_id":"t1","permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"touch c.txt"}],"behavior":"allow","destination":"localSettings"}]}}""");

        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        Assert.Equal("Bash", requested.Request.ToolName);
        Assert.Single(requested.Request.Suggestions);
        var rule = new JsonArray(new JsonObject
        {
            ["type"] = "addRules",
            ["rules"] = new JsonArray(new JsonObject { ["toolName"] = "Bash", ["ruleContent"] = "touch:*" }),
            ["behavior"] = "allow",
            ["destination"] = "localSettings",
        });
        requested.Request.Allow(updatedPermissions: rule);

        var answer = await _transport.WaitForSentAsync(m => Type(m) == "control_response");
        var response = answer["response"]!;
        Assert.Equal("success", response["subtype"]!.GetValue<string>());
        Assert.Equal("cli_1", response["request_id"]!.GetValue<string>());
        Assert.Equal("allow", response["response"]!["behavior"]!.GetValue<string>());
        Assert.Equal("touch c.txt", response["response"]!["updatedInput"]!["command"]!.GetValue<string>());
        Assert.Equal("touch:*", response["response"]!["updatedPermissions"]![0]!["rules"]![0]!["ruleContent"]!.GetValue<string>());
    }

    [Fact]
    public async Task Denying_a_permission_request_sends_the_message()
    {
        await using var session = await StartAsync();
        _transport.Emit("""{"type":"control_request","request_id":"cli_2","request":{"subtype":"can_use_tool","tool_name":"Write","input":{"file_path":"a.txt"}}}""");

        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Deny("Not that file");

        var answer = await _transport.WaitForSentAsync(m => Type(m) == "control_response");
        Assert.Equal("deny", answer["response"]!["response"]!["behavior"]!.GetValue<string>());
        Assert.Equal("Not that file", answer["response"]!["response"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_cancelled_permission_request_is_reported_and_never_answered()
    {
        await using var session = await StartAsync();
        _transport.Emit("""{"type":"control_request","request_id":"cli_3","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{}}}""");
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();

        _transport.Emit("""{"type":"control_cancel_request","request_id":"cli_3"}""");
        var (cancelled, _) = await session.ReadUntilAsync<PermissionCancelled>();
        requested.Request.Allow();

        Assert.Equal("cli_3", cancelled.RequestId);
        Assert.True(requested.Request.IsCancelled);
        Assert.DoesNotContain(_transport.Sent, m => Type(m) == "control_response");
    }

    [Fact]
    public async Task Interrupt_sends_a_control_request_and_waits_for_the_answer()
    {
        await using var session = await StartAsync();
        _transport.AutoRespond["interrupt"] = _ => new JsonObject { ["still_queued"] = new JsonArray() };

        await session.InterruptAsync(TestContext.Current.CancellationToken);

        Assert.Contains(_transport.Sent, m => Type(m) == "control_request" && m["request"]!["subtype"]!.GetValue<string>() == "interrupt");
    }

    [Fact]
    public async Task Control_requests_are_numbered_in_send_order()
    {
        await using var session = await StartAsync();
        _transport.AutoRespond["interrupt"] = _ => [];

        await session.InterruptAsync(TestContext.Current.CancellationToken);

        var ids = _transport.Sent.Where(m => Type(m) == "control_request").Select(m => m["request_id"]!.GetValue<string>());
        Assert.Equal(["req_1", "req_2"], ids);
    }

    [Fact]
    public async Task Model_effort_and_permission_mode_changes_use_the_expected_requests()
    {
        await using var session = await StartAsync();
        foreach (var subtype in new[] { "set_model", "apply_flag_settings", "set_permission_mode" })
        {
            _transport.AutoRespond[subtype] = _ => [];
        }

        await session.SetModelAsync("sonnet", TestContext.Current.CancellationToken);
        await session.SetEffortAsync("low", TestContext.Current.CancellationToken);
        await session.SetPermissionModeAsync("acceptEdits", TestContext.Current.CancellationToken);

        var requests = _transport.Sent.Where(m => Type(m) == "control_request").Select(m => m["request"]!).ToArray();
        Assert.Equal("sonnet", requests.Single(r => Subtype(r) == "set_model")["model"]!.GetValue<string>());
        Assert.Equal("low", requests.Single(r => Subtype(r) == "apply_flag_settings")["settings"]!["effortLevel"]!.GetValue<string>());
        Assert.Equal("acceptEdits", requests.Single(r => Subtype(r) == "set_permission_mode")["mode"]!.GetValue<string>());
        Assert.Equal("acceptEdits", session.PermissionMode);
    }

    [Fact]
    public async Task An_error_response_throws()
    {
        await using var session = await StartAsync();
        _transport.AutoRespond["set_model"] = _ => null;
        var pending = session.SetModelAsync("nope", TestContext.Current.CancellationToken);

        var sent = await _transport.WaitForSentAsync(m => Type(m) == "control_request" && m["request"]!["subtype"]!.GetValue<string>() == "set_model");
        _transport.EmitJson(OutgoingMessages.ControlError(sent["request_id"]!.GetValue<string>(), "unknown model"));

        var ex = await Assert.ThrowsAsync<ControlRequestException>(() => pending);
        Assert.Equal("set_model", ex.Subtype);
        Assert.Equal("unknown model", ex.Error);
    }

    [Fact]
    public async Task A_request_without_an_answer_times_out()
    {
        await using var session = await StartAsync();
        _transport.AutoRespond["interrupt"] = _ => null;
        var pending = session.InterruptAsync(TestContext.Current.CancellationToken);
        await _transport.WaitForSentAsync(m => Type(m) == "control_request" && m["request"]!["subtype"]!.GetValue<string>() == "interrupt");

        _time.Advance(ClaudeSession.DefaultControlTimeout + TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => pending);
    }

    [Fact]
    public async Task Exiting_fails_waiting_requests_and_ends_the_event_stream()
    {
        await using var session = await StartAsync();
        _transport.AutoRespond["interrupt"] = _ => null;
        var pending = session.InterruptAsync(TestContext.Current.CancellationToken);
        await _transport.WaitForSentAsync(m => Type(m) == "control_request" && m["request"]!["subtype"]!.GetValue<string>() == "interrupt");

        _transport.Exit(1, "boom");

        var ex = await Assert.ThrowsAsync<ClaudeSessionExitedException>(() => pending);
        Assert.Equal(1, ex.Exit.ExitCode);
        var (exited, _) = await session.ReadUntilAsync<SessionExited>();
        Assert.Equal("boom", exited.Exit.StandardErrorTail);
        await session.Completion;
        Assert.Equal(SessionState.Exited, session.State);
    }

    [Fact]
    public async Task Bad_and_unknown_lines_are_skipped_and_counted()
    {
        await using var session = await StartAsync();

        _transport.Emit("this is not json");
        _transport.Emit("""{"type":"something_new"}""");
        _transport.Emit("""{"type":"result","subtype":"success","is_error":false,"result":"still here"}""");

        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();
        Assert.Equal("still here", done.Result.Result);
        Assert.Contains(seen, e => e is ProtocolError);
        Assert.Contains(seen, e => e is UnrecognizedMessage { MessageType: "something_new" });
        Assert.Equal(1, session.ProtocolErrorCount);
        Assert.Equal(1, session.UnknownMessageCount);
    }

    [Fact]
    public async Task Authentication_failure_is_reported()
    {
        await using var session = await StartAsync();

        _transport.Emit("""{"type":"assistant","message":{"model":"<synthetic>","content":[{"type":"text","text":"Not logged in · Please run /login"}]},"error":"authentication_failed"}""");

        var (required, _) = await session.ReadUntilAsync<AuthenticationRequired>();
        Assert.Equal("Not logged in · Please run /login", required.Detail);
    }

    [Fact]
    public async Task An_organization_that_isnt_allowed_needs_a_sign_in_too()
    {
        await using var session = await StartAsync();

        _transport.Emit("""{"type":"assistant","message":{"model":"<synthetic>","content":[{"type":"text","text":"Your organization isn't allowed"}]},"error":"oauth_org_not_allowed"}""");

        var (required, _) = await session.ReadUntilAsync<AuthenticationRequired>();
        Assert.Equal("Your organization isn't allowed", required.Detail);
    }

    [Fact]
    public async Task Only_a_failed_auth_status_needs_a_sign_in()
    {
        await using var session = await StartAsync();

        // Progress from a cloud credential helper, then its failure.
        _transport.Emit("""{"type":"auth_status","isAuthenticating":true,"output":["Refreshing AWS credentials"],"uuid":"u1","session_id":"s"}""");
        _transport.Emit("""{"type":"auth_status","isAuthenticating":false,"output":[],"error":"awsAuthRefresh failed","uuid":"u2","session_id":"s"}""");

        var (required, seen) = await session.ReadUntilAsync<AuthenticationRequired>();
        Assert.Equal("awsAuthRefresh failed", required.Detail);
        Assert.Single(seen.OfType<AuthenticationRequired>());
    }

    [Fact]
    public async Task Other_assistant_errors_dont_need_a_sign_in()
    {
        await using var session = await StartAsync();

        _transport.Emit("""{"type":"assistant","message":{"model":"<synthetic>","content":[{"type":"text","text":"Rate limited"}]},"error":"rate_limit"}""");
        _transport.Emit("""{"type":"result","subtype":"success","is_error":true,"result":"Rate limited"}""");

        var (_, seen) = await session.ReadUntilAsync<TurnCompleted>();
        Assert.DoesNotContain(seen, e => e is AuthenticationRequired);
    }

    [Fact]
    public async Task Unsupported_control_requests_get_an_error_answer()
    {
        await using var session = await StartAsync();

        _transport.Emit("""{"type":"control_request","request_id":"cli_9","request":{"subtype":"hook_callback","callback_id":"x"}}""");

        var answer = await _transport.WaitForSentAsync(m => Type(m) == "control_response");
        Assert.Equal("error", answer["response"]!["subtype"]!.GetValue<string>());
        Assert.Equal("cli_9", answer["response"]!["request_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Local_command_output_is_its_own_event()
    {
        await using var session = await StartAsync();

        _transport.Emit("""{"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model to `sonnet`</local-command-stdout>"}}""");

        var (note, _) = await session.ReadUntilAsync<LocalCommandOutputReceived>();
        Assert.Equal("Set model to `sonnet`", note.Text);
    }

    [Fact]
    public async Task Stop_closes_input_and_waits_for_exit()
    {
        var session = await StartAsync();

        await session.StopAsync(TimeSpan.FromSeconds(5));

        Assert.True(_transport.InputClosed);
        Assert.Equal(SessionState.Exited, session.State);
    }

    private async Task<ClaudeSession> StartAsync()
    {
        var session = new ClaudeSession(_transport, _time);
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        return session;
    }

    private static string? Type(JsonObject message) => message["type"]?.GetValue<string>();

    private static string? Subtype(JsonNode request) => request["subtype"]?.GetValue<string>();
}
