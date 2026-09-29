using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.Core.Tests.Sessions;

/// <summary>Per-call usage from assistant messages (DESIGN.md §6, "Data source" and "Per-tab context").</summary>
public class CallUsageTests
{
    [Fact]
    public void Calls_are_counted_once_by_message_id_with_their_latest_figures()
    {
        var usage = new CallUsage();

        Assert.True(usage.Add(Assistant("msg_1", input: 1000, output: 1)));
        Assert.False(usage.Add(Assistant("msg_1", input: 1000, output: 1)));
        Assert.True(usage.Add(Assistant("msg_1", input: 1000, output: 50)));
        Assert.True(usage.Add(Assistant("msg_2", input: 1100, output: 10, cacheRead: 900, parent: "toolu_agent")));

        Assert.Equal(1050 + 2010, usage.TurnTokens);
        // The subagent's call isn't the main agent's context.
        Assert.Equal(1050, usage.ContextTokens);
        Assert.False(usage.Add((AssistantMessage)Parse("""{"type":"assistant","message":{"id":"msg_3","content":[]}}""")));
    }

    [Fact]
    public void The_context_estimate_needs_the_window_from_a_result()
    {
        var usage = new CallUsage();
        usage.Add(Assistant("msg_1", input: 40_000, output: 10_000, cacheWrite: 50_000));
        Assert.Null(usage.ContextPercentage);

        usage.TurnEnded(Result("""{"claude-haiku-4-5":{"inputTokens":1000,"contextWindow":200000}}"""));

        Assert.Equal(0, usage.TurnTokens);
        Assert.Equal(200_000, usage.ContextWindow);
        Assert.Equal(50, usage.ContextPercentage);

        usage.ContextReset();
        Assert.Null(usage.ContextPercentage);
    }

    private static AssistantMessage Assistant(string id, long input, long output, long cacheWrite = 0, long cacheRead = 0, string? parent = null) =>
        (AssistantMessage)Parse(new JsonObject
        {
            ["type"] = "assistant",
            ["parent_tool_use_id"] = parent,
            ["message"] = new JsonObject
            {
                ["id"] = id,
                ["model"] = "claude-haiku-4-5",
                ["content"] = new JsonArray(),
                ["usage"] = new JsonObject
                {
                    ["input_tokens"] = input,
                    ["output_tokens"] = output,
                    ["cache_creation_input_tokens"] = cacheWrite,
                    ["cache_read_input_tokens"] = cacheRead,
                },
            },
        }.ToJsonString());

    private static ResultMessage Result(string modelUsage) =>
        (ResultMessage)Parse($$"""{"type":"result","subtype":"success","is_error":false,"session_id":"s","modelUsage":{{modelUsage}}}""");

    private static ClaudeMessage Parse(string line) =>
        MessageParser.TryParse(line, out var message, out var error) ? message : throw new InvalidOperationException(error);
}
