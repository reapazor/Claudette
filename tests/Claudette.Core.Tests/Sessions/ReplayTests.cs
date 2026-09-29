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
        var transport = new ReplayTransport(ProtocolFixture.Load("07-subagents"));
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
}
