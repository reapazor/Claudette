using Claudette.Core.Sessions;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Sessions;

/// <summary>Drives <see cref="ClaudeSession"/> through recorded real Claude Code traffic.</summary>
public class ReplayTests
{
    [Fact]
    public async Task Simple_turn()
    {
        var transport = new ReplayTransport(ProtocolFixture.Load("01-mock-basic"));
        await using var session = new ClaudeSession(transport, TimeProvider.System);

        var init = await session.InitializeAsync(TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.NotEmpty(init.Models);
        Assert.Contains(seen, e => e is TurnStarted);
        Assert.Equal("pong", done.Result.Result);
        Assert.Equal("claude-haiku-4-5", session.Model);
        Assert.Equal("2.1.284", session.ClaudeCodeVersion);
        Assert.Equal(0, session.UnknownMessageCount);
        Assert.Equal(0, session.ProtocolErrorCount);
    }

    [Fact]
    public async Task Edit_turn_reports_tool_use_and_original_file()
    {
        var transport = new ReplayTransport(ProtocolFixture.Load("06-edit-original-file"));
        await using var session = new ClaudeSession(transport, TimeProvider.System);

        await session.InitializeAsync(TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("EDIT_FILE e.txt", TestContext.Current.CancellationToken);
        var (_, seen) = await session.ReadUntilAsync<TurnCompleted>();

        var toolNames = seen.OfType<AssistantMessageReceived>()
            .SelectMany(a => a.Message.Content.OfType<Claudette.Core.Protocol.ToolUseBlock>())
            .Select(t => t.Name);
        Assert.Equal(["Read", "Edit"], toolNames);
        Assert.Contains(seen.OfType<ToolResultsReceived>(), r => r.Message.ToolUseResult?["originalFile"] is not null);
    }

    [Fact]
    public async Task Subagent_traffic_is_tagged_with_the_agent_call_that_started_it()
    {
        // SUBAGENTS against the mock Messages API: two subagents in parallel, one asking for permission, the other
        // starting a nested one (DESIGN.md §18).
        var transport = new ReplayTransport(ProtocolFixture.Load("12-subagents"));
        await using var session = new ClaudeSession(transport, TimeProvider.System);

        await session.InitializeAsync(TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("SUBAGENTS", TestContext.Current.CancellationToken);
        var (requested, before) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Allow();
        var (_, after) = await session.ReadUntilAsync<TurnCompleted>();
        var seen = before.Concat(after).ToArray();

        var agentCalls = seen.OfType<AssistantMessageReceived>()
            .SelectMany(a => a.Message.Content.OfType<Claudette.Core.Protocol.ToolUseBlock>().Select(t => (Parent: a.Message.ParentToolUseId, Call: t)))
            .Where(c => c.Call.Name == "Agent")
            .ToArray();
        Assert.Equal([(null, "Touch a marker file"), (null, "Delegate a deeper look"), ("toolu_mock_2", "Search deeper")],
            agentCalls.Select(c => (c.Parent, c.Call.Input["description"]!.GetValue<string>())));
        // Each subagent's task, tied to its Agent call; the prompt comes from inside the subagent.
        var tasks = seen.OfType<SystemNotice>().Where(n => n.Message.Subtype == "task_started").Select(n => n.Message.Raw).ToArray();
        Assert.Equal(["toolu_mock_1", "toolu_mock_2", "toolu_mock_4"], tasks.Select(t => t["tool_use_id"]!.GetValue<string>()));
        Assert.Equal(tasks[0]["task_id"]!.GetValue<string>(), requested.Request.AgentId);
        Assert.Contains(seen.OfType<SystemNotice>(), n => n.Message.Subtype == "task_notification");
        Assert.Contains(seen.OfType<ToolResultsReceived>(), r => r.Message.ParentToolUseId == "toolu_mock_2");
        Assert.Equal(0, session.UnknownMessageCount);
    }

    [Fact]
    public async Task Signed_out_turn_reports_authentication_required()
    {
        var transport = new ReplayTransport(ProtocolFixture.Load("signed-out"));
        await using var session = new ClaudeSession(transport, TimeProvider.System);

        await session.InitializeAsync(TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Contains(seen, e => e is AuthenticationRequired);
        Assert.True(done.Result.IsError);
    }

    [Fact]
    public async Task A_denied_permission_goes_back_as_a_tool_error()
    {
        var transport = new ReplayTransport(ProtocolFixture.Load("07-permission-denied"));
        await using var session = new ClaudeSession(transport, TimeProvider.System);

        await session.InitializeAsync(TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("RUN_BASH touch denied.txt", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Deny("Not in this folder, please.");
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        var denial = transport.Sent.Last();
        Assert.Equal("deny", denial["response"]?["response"]?["behavior"]?.GetValue<string>());
        Assert.Contains(seen.OfType<ToolResultsReceived>(), r => r.Message.Content.OfType<Claudette.Core.Protocol.ToolResultBlock>().Any(b => b.IsError));
        Assert.False(done.Result.IsError);
        Assert.Equal(0, session.UnknownMessageCount);
    }

    [Fact]
    public async Task An_interrupt_ends_the_turn_with_a_result()
    {
        var transport = new ReplayTransport(ProtocolFixture.Load("08-interrupt"));
        await using var session = new ClaudeSession(transport, TimeProvider.System);

        await session.InitializeAsync(TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("SLOW", TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TextDelta>();
        await session.InterruptAsync(TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("aborted_streaming", done.Result.TerminalReason);
        Assert.True(done.Result.IsError);
    }

    [Fact]
    public async Task Clear_resets_the_conversation_and_carries_on()
    {
        var transport = new ReplayTransport(ProtocolFixture.Load("09-clear"));
        await using var session = new ClaudeSession(transport, TimeProvider.System);

        await session.InitializeAsync(TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (first, _) = await session.ReadUntilAsync<TurnCompleted>();
        await session.SendUserMessageAsync("/clear", TestContext.Current.CancellationToken);
        var (reset, _) = await session.ReadUntilAsync<ConversationReset>();
        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (second, _) = await session.ReadUntilAsync<TurnCompleted>(t => t.Result.Result == "pong");

        Assert.Equal("clear", reset.Trigger);
        Assert.NotEqual(first.Result.SessionId, second.Result.SessionId);
        Assert.Equal(0, session.UnknownMessageCount);
    }

    [Fact]
    public async Task Compaction_marks_the_boundary_and_reports_auto_compaction()
    {
        var transport = new ReplayTransport(ProtocolFixture.Load("10-compact"));
        await using var session = new ClaudeSession(transport, TimeProvider.System);

        await session.InitializeAsync(TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (_, firstTurn) = await session.ReadUntilAsync<TurnCompleted>();
        await session.SendUserMessageAsync("/compact", TestContext.Current.CancellationToken);
        var (boundary, _) = await session.ReadUntilAsync<SystemNotice>(n => n.Message.Subtype == "compact_boundary");

        Assert.Equal("manual", boundary.Message.Raw["compact_metadata"]?["trigger"]?.GetValue<string>());
        var autocompact = firstTurn.OfType<AutocompactStateChanged>().First().State;
        Assert.True(autocompact.Enabled);
        Assert.True(autocompact.Threshold < autocompact.EffectiveWindow);
    }

    [Fact]
    public async Task Api_retries_are_reported_before_the_turn_succeeds()
    {
        var transport = new ReplayTransport(ProtocolFixture.Load("11-api-retry"));
        await using var session = new ClaudeSession(transport, TimeProvider.System);

        await session.InitializeAsync(TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("API_ERROR", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        var retries = seen.OfType<SystemNotice>().Where(n => n.Message.Subtype == "api_retry").ToArray();
        Assert.Equal(2, retries.Length);
        Assert.Equal(529, retries[0].Message.Raw["error_status"]?.GetValue<int>());
        Assert.Equal("pong", done.Result.Result);
    }
}
