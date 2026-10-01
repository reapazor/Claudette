using Claudette.Core.Sessions;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Sessions;

/// <summary>
/// Hook callbacks registered through <c>initialize</c> and called back with <c>hook_callback</c> (DESIGN.md §13, "Hook
/// callbacks"). The wire format is the one recorded against Claude Code 2.1.284.
/// </summary>
public class HookCallbackTests
{
    private const string PreToolUse = """
        {"type":"control_request","request_id":"ba5ef36e","request":{"subtype":"hook_callback","callback_id":"hook_0","input":{"session_id":"s1","transcript_path":"/t.jsonl","cwd":"/ws","permission_mode":"default","hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"p4 edit -c 12345 a.cpp","description":"Open a.cpp"},"tool_use_id":"toolu_1"},"tool_use_id":"toolu_1"}}
        """;

    private readonly FakeTransport _transport = new();
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task Without_hooks_initialize_sends_null()
    {
        await using var session = new ClaudeSession(_transport, _time);
        await session.InitializeAsync(TestContext.Current.CancellationToken);

        var initialize = await _transport.WaitForSentAsync(m => m["request"]?["subtype"]?.GetValue<string>() == "initialize");
        Assert.True(initialize["request"]!.AsObject().ContainsKey("hooks"));
        Assert.Null(initialize["request"]!["hooks"]);
    }

    [Fact]
    public async Task Hooks_are_registered_like_the_agent_sdk_does()
    {
        await using var session = new ClaudeSession(_transport, _time);
        await session.InitializeAsync(
        [
            new HookRegistration("PreToolUse", "Bash", (_, _) => Task.FromResult(HookOutputs.Continue()), TimeSpan.FromMinutes(5)),
            new HookRegistration("PreToolUse", null, (_, _) => Task.FromResult(HookOutputs.Continue())),
            new HookRegistration("PostToolUse", "Edit|Write", (_, _) => Task.FromResult(HookOutputs.Continue())),
        ], TestContext.Current.CancellationToken);

        var initialize = await _transport.WaitForSentAsync(m => m["request"]?["subtype"]?.GetValue<string>() == "initialize");
        Assert.Equal(
            """{"PreToolUse":[{"matcher":"Bash","hookCallbackIds":["hook_0"],"timeout":300},{"matcher":null,"hookCallbackIds":["hook_1"]}],"PostToolUse":[{"matcher":"Edit|Write","hookCallbackIds":["hook_2"]}]}""",
            initialize["request"]!["hooks"]!.ToJsonString());
    }

    [Fact]
    public async Task A_callback_gets_the_input_and_its_output_is_the_answer()
    {
        HookInput? received = null;
        await using var session = await StartAsync((input, _) =>
        {
            received = input;
            return Task.FromResult(HookOutputs.Continue());
        });

        _transport.Emit(PreToolUse);

        var answer = await _transport.WaitForSentAsync(m => m["type"]?.GetValue<string>() == "control_response");
        Assert.Equal("""{"type":"control_response","response":{"subtype":"success","request_id":"ba5ef36e","response":{"continue":true}}}""", answer.ToJsonString());
        Assert.Equal("PreToolUse", received!.EventName);
        Assert.Equal("Bash", received.ToolName);
        Assert.Equal("p4 edit -c 12345 a.cpp", received.Command);
        Assert.Equal("toolu_1", received.ToolUseId);
    }

    [Fact]
    public async Task A_slow_callback_doesnt_hold_up_other_messages()
    {
        var release = new TaskCompletionSource();
        await using var session = await StartAsync(async (_, _) =>
        {
            await release.Task;
            return HookOutputs.Continue();
        });

        _transport.Emit(PreToolUse);
        _transport.Emit("""{"type":"system","subtype":"status","status":null}""");
        await session.ReadUntilAsync<SystemNotice>();
        Assert.DoesNotContain(_transport.Sent, m => m["type"]?.GetValue<string>() == "control_response");

        release.SetResult();
        await _transport.WaitForSentAsync(m => m["type"]?.GetValue<string>() == "control_response");
    }

    [Fact]
    public async Task An_unknown_or_failing_callback_gets_an_error_answer()
    {
        await using var session = await StartAsync((_, _) => throw new InvalidOperationException("broken"));

        _transport.Emit(PreToolUse);
        var failed = await _transport.WaitForSentAsync(m => m["response"]?["request_id"]?.GetValue<string>() == "ba5ef36e");
        _transport.Emit("""{"type":"control_request","request_id":"x2","request":{"subtype":"hook_callback","callback_id":"hook_9","input":{}}}""");
        var unknown = await _transport.WaitForSentAsync(m => m["response"]?["request_id"]?.GetValue<string>() == "x2");

        Assert.Equal("error", failed["response"]!["subtype"]!.GetValue<string>());
        Assert.Equal("broken", failed["response"]!["error"]!.GetValue<string>());
        Assert.Equal("error", unknown["response"]!["subtype"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_withdrawn_call_is_cancelled_and_not_answered()
    {
        var cancelled = new TaskCompletionSource();
        await using var session = await StartAsync(async (_, token) =>
        {
            await using (token.Register(() => cancelled.TrySetResult()))
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            return HookOutputs.Continue();
        });

        _transport.Emit(PreToolUse);
        _transport.Emit("""{"type":"control_cancel_request","request_id":"ba5ef36e"}""");

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // Anything sent after this would be the (wrong) answer; a later request proves the loop moved on.
        _transport.Emit("""{"type":"control_request","request_id":"x3","request":{"subtype":"hook_callback","callback_id":"hook_9","input":{}}}""");
        await _transport.WaitForSentAsync(m => m["response"]?["request_id"]?.GetValue<string>() == "x3");
        Assert.DoesNotContain(_transport.Sent, m => m["response"]?["request_id"]?.GetValue<string>() == "ba5ef36e");
    }

    [Fact]
    public async Task The_launch_options_carry_hooks_and_the_system_prompt_note()
    {
        var options = new ClaudeLaunchOptions
        {
            WorkingDirectory = "/ws",
            AppendSystemPrompt = "This folder is in a Perforce workspace.",
            Hooks = [new HookRegistration("PreToolUse", "Bash", (_, _) => Task.FromResult(HookOutputs.Continue()))],
        };

        var args = ClaudeArguments.ForStreamingSession(options);

        Assert.Equal("This folder is in a Perforce workspace.", args[args.ToList().IndexOf("--append-system-prompt") + 1]);
        Assert.DoesNotContain(args, a => a.Contains("hook", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("--append-system-prompt", ClaudeArguments.ForStreamingSession(options with { AppendSystemPrompt = null }));
        await Task.CompletedTask;
    }

    private async Task<ClaudeSession> StartAsync(HookCallback callback)
    {
        var session = new ClaudeSession(_transport, _time);
        await session.InitializeAsync([new HookRegistration("PreToolUse", "Bash", callback)], TestContext.Current.CancellationToken);
        return session;
    }
}
