using System.Text.Json.Nodes;
using Claudette.Core.Sessions;

namespace Claudette.Core.Tests.Sessions;

/// <summary>What fills a session's context window, from <c>get_context_usage</c> (DESIGN.md §6, "Per-tab context").</summary>
public class ContextUsageTests
{
    [Fact]
    public void Each_part_of_the_window_is_read_with_what_is_inside_it()
    {
        var usage = ContextUsage.Parse(JsonNode.Parse("""
            {
              "categories": [
                { "name": "System prompt", "tokens": 3100, "color": "promptBorder", "kind": "used" },
                { "name": "MCP tools", "tokens": 9800, "color": "cyan_FOR_SUBAGENTS_ONLY", "kind": "used" },
                { "name": "MCP tools (deferred)", "tokens": 12000, "color": "inactive", "isDeferred": true, "kind": "deferred" },
                { "name": "Free space", "tokens": 43000, "color": "promptBorder", "kind": "free" },
                { "name": "Autocompact buffer", "tokens": 33000, "color": "inactive", "kind": "buffer" }
              ],
              "totalTokens": 124000, "maxTokens": 200000, "percentage": 62,
              "memoryFiles": [ { "path": "/work/nexus/CLAUDE.md", "type": "Project", "tokens": 2400 } ],
              "mcpTools": [
                { "name": "mcp__github__get_me", "serverName": "github", "tokens": 400 },
                { "name": "mcp__github__list_issues", "serverName": "github", "tokens": 900, "isLoaded": false }
              ],
              "agents": [ { "agentType": "reviewer", "source": "projectSettings", "tokens": 300 } ],
              "isAutoCompactEnabled": true, "autoCompactThreshold": 167000
            }
            """)!.AsObject());

        Assert.Equal(
            [ContextCategoryKind.Used, ContextCategoryKind.Used, ContextCategoryKind.Deferred, ContextCategoryKind.Free, ContextCategoryKind.Buffer],
            usage.Categories.Select(c => c.Kind));
        Assert.Equal(new ContextMemoryFile("/work/nexus/CLAUDE.md", "Project", 2400), Assert.Single(usage.MemoryFiles));
        Assert.Equal([new ContextMcpTool("mcp__github__get_me", "github", 400, true), new ContextMcpTool("mcp__github__list_issues", "github", 900, false)], usage.McpTools);
        Assert.Equal(new ContextAgent("reviewer", "projectSettings", 300), Assert.Single(usage.Agents));
        Assert.Null(usage.Skills);
        Assert.Null(usage.Messages);
    }

    [Theory]
    // Claude Code before it sent a kind: deferred tools were marked, and the other rows known by name.
    [InlineData("""{ "name": "System tools (deferred)", "tokens": 1, "isDeferred": true }""", ContextCategoryKind.Deferred)]
    [InlineData("""{ "name": "Free space", "tokens": 1 }""", ContextCategoryKind.Free)]
    [InlineData("""{ "name": "Autocompact buffer", "tokens": 1 }""", ContextCategoryKind.Buffer)]
    [InlineData("""{ "name": "Messages", "tokens": 1 }""", ContextCategoryKind.Used)]
    // A kind a later Claude Code adds.
    [InlineData("""{ "name": "Something new", "tokens": 1, "kind": "reserved" }""", ContextCategoryKind.Unknown)]
    public void A_rows_kind_is_read_or_worked_out(string category, ContextCategoryKind kind)
    {
        var usage = ContextUsage.Parse(new JsonObject { ["categories"] = new JsonArray(JsonNode.Parse(category)) });

        Assert.Equal(kind, Assert.Single(usage.Categories).Kind);
    }

    [Fact]
    public void Rows_it_cant_read_are_skipped()
    {
        var usage = ContextUsage.Parse(JsonNode.Parse("""
            {
              "categories": [ { "tokens": 5 }, "text", { "name": "Messages", "tokens": "many" } ],
              "memoryFiles": [ { "tokens": 5 } ],
              "skills": { "skillFrontmatter": [ { "name": "loop" }, 3 ] },
              "messageBreakdown": { "toolCallsByType": [ { "callTokens": 3 } ] }
            }
            """)!.AsObject());

        Assert.Equal(("Messages", 0L), (Assert.Single(usage.Categories).Name, usage.Categories[0].Tokens));
        Assert.Empty(usage.MemoryFiles);
        Assert.Equal(new ContextSkill("loop", null, 0), Assert.Single(usage.Skills!.Listed));
        Assert.Empty(usage.Messages!.ByTool);
    }
}
