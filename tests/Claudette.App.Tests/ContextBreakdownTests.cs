using System.Text.Json.Nodes;
using Claudette.App.Controls;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Sessions;

namespace Claudette.App.Tests;

/// <summary>What fills a tab's context window, as its flyout shows it (DESIGN.md §6, "Per-tab context").</summary>
public class ContextBreakdownTests
{
    /// <summary>A session a while in, as /context would show it.</summary>
    private static JsonObject Usage() => JsonNode.Parse("""
        {
          "categories": [
            { "name": "System prompt", "tokens": 3100, "kind": "used" },
            { "name": "System tools", "tokens": 14200, "kind": "used" },
            { "name": "MCP tools", "tokens": 9800, "kind": "used" },
            { "name": "MCP tools (deferred)", "tokens": 12000, "kind": "deferred" },
            { "name": "Memory files", "tokens": 2400, "kind": "used" },
            { "name": "Skills", "tokens": 1800, "kind": "used" },
            { "name": "Messages", "tokens": 92700, "kind": "used" },
            { "name": "Free space", "tokens": 43000, "kind": "free" },
            { "name": "Autocompact buffer", "tokens": 33000, "kind": "buffer" }
          ],
          "totalTokens": 124000, "maxTokens": 200000, "percentage": 62,
          "memoryFiles": [
            { "path": "/work/nexus/CLAUDE.md", "type": "Project", "tokens": 1400 },
            { "path": "/home/me/.claude/CLAUDE.md", "type": "User", "tokens": 1000 }
          ],
          "mcpTools": [
            { "name": "mcp__github__get_me", "serverName": "github", "tokens": 6000 },
            { "name": "mcp__github__list_issues", "serverName": "github", "tokens": 3000, "isLoaded": false },
            { "name": "mcp__docs__read", "serverName": "docs", "tokens": 3800 }
          ],
          "agents": [],
          "skills": { "totalSkills": 3, "includedSkills": 2, "tokens": 1800,
                      "skillFrontmatter": [ { "name": "loop", "source": "built-in", "tokens": 600 }, { "name": "deploy", "source": "project", "tokens": 1200 } ] },
          "messageBreakdown": {
            "toolCallTokens": 8000, "toolResultTokens": 61000, "attachmentTokens": 9000, "assistantMessageTokens": 9700,
            "userMessageTokens": 5000, "redirectedContextTokens": 0, "unattributedTokens": 0,
            "toolCallsByType": [ { "name": "Read", "callTokens": 1000, "resultTokens": 40000 }, { "name": "Bash", "callTokens": 7000, "resultTokens": 21000 } ],
            "attachmentsByType": [ { "name": "prompt_snapshot", "tokens": 9000 } ]
          },
          "isAutoCompactEnabled": true, "autoCompactThreshold": 167000
        }
        """)!.AsObject();

    [Fact]
    public void Each_part_has_its_color_and_share_in_the_bars_order_with_the_tools_held_back_last()
    {
        var breakdown = ContextBreakdown.From(ContextUsage.Parse(Usage()));

        Assert.Equal(
            [
                ("Memory files", "2,400", "1%", "ContextMemoryBrush"),
                ("Skills", "1,800", "1%", "ContextSkillsBrush"),
                ("System prompt", "3,100", "2%", "ContextPromptBrush"),
                ("System tools", "14,200", "7%", "ContextToolsBrush"),
                ("MCP tools", "9,800", "5%", "ContextMcpBrush"),
                ("Messages", "92,700", "46%", "ContextMessagesBrush"),
                ("Free space", "43,000", "22%", "MeterTrackBrush"),
                ("Autocompact buffer", "33,000", "17%", "ContextBufferBrush"),
                ("MCP tools (deferred)", "12,000", "", null),
            ],
            breakdown.Rows.Select(r => (r.Name, r.Tokens, r.Percent, r.Brush)));
        Assert.Equal(breakdown.Rows.Take(8).Select(r => r.Name), breakdown.Segments.Select(s => s.Name));
        Assert.Equal("Messages: 92,700 tokens, 46%", breakdown.Segments[5].Tip);
        Assert.False(breakdown.Rows[^1].HasSwatch);
        Assert.NotNull(breakdown.Rows[^1].Tip);
        Assert.Null(breakdown.Note);
    }

    [Fact]
    public void The_larger_parts_open_to_what_is_inside_them()
    {
        var breakdown = ContextBreakdown.From(ContextUsage.Parse(Usage()));

        Assert.Equal(["Memory files (2)", "MCP tools (2 servers)", "Skills (2 of 3 listed)", "Messages"], breakdown.Sections.Select(s => s.Header));
        Assert.All(breakdown.Sections, s => Assert.False(s.IsExpanded));

        // A server's tokens are its tools in the window; the others are held back until they're needed.
        Assert.Equal([("github", "6,000", "2 tools, 1 not loaded"), ("docs", "3,800", "1 tool")],
            breakdown.Sections[1].Rows.Select(r => (r.Name, r.Tokens, r.Detail)));
        Assert.Equal([("deploy", "project"), ("loop", "built-in")], breakdown.Sections[2].Rows.Select(r => (r.Name, r.Detail)));

        var messages = breakdown.Sections[3].Rows;
        Assert.Equal(
            ["Tool results", "Claude's replies", "Attachments", "Tool calls", "Your messages", "Read", "Bash", "prompt_snapshot"],
            messages.Select(r => r.Name));
        Assert.Equal([null, null, null, null, null, "By tool", null, "Attachments"], messages.Select(r => r.GroupHeading));
        Assert.Equal("calls 1,000 · results 40,000", messages[5].Detail);
        Assert.True(messages[5].IsDetail);
    }

    [Fact]
    public void Without_a_buffer_row_the_space_past_the_auto_compact_threshold_is_the_buffer()
    {
        var usage = Usage();
        var categories = usage["categories"]!.AsArray();
        categories.RemoveAt(categories.Count - 1);
        // Free space then counts all the window left, as Claude Code 2.1.284 sends it.
        categories[^1]!["tokens"] = 76000;

        // Read back as Claude Code's line would be.
        var breakdown = ContextBreakdown.From(ContextUsage.Parse(JsonNode.Parse(usage.ToJsonString())!.AsObject()));

        Assert.Equal([("Free space", "43,000"), (ContextBreakdown.AutoCompactBuffer, "33,000")],
            breakdown.Rows.Where(r => r.Brush is "MeterTrackBrush" or "ContextBufferBrush").Select(r => (r.Name, r.Tokens)));
    }

    [Fact]
    public void A_part_this_version_doesnt_know_is_shown_after_the_others_and_a_small_one_still_has_a_share()
    {
        var usage = ContextUsage.Parse(JsonNode.Parse("""
            {
              "categories": [
                { "name": "Plugins", "tokens": 120, "kind": "used" },
                { "name": "System prompt", "tokens": 3100, "kind": "used" },
                { "name": "Free space", "tokens": 196780, "kind": "free" }
              ],
              "totalTokens": 3220, "maxTokens": 200000, "percentage": 2
            }
            """)!.AsObject());

        var breakdown = ContextBreakdown.From(usage);

        Assert.Equal([("System prompt", "2%"), ("Plugins", "<1%"), ("Free space", "98%")], breakdown.Rows.Select(r => (r.Name, r.Percent)));
        Assert.Equal(ContextBreakdown.OtherBrush, breakdown.Rows[1].Brush);
        Assert.Empty(breakdown.Sections);
    }

    [Fact]
    public void The_estimate_says_how_much_is_in_the_context_but_not_what()
    {
        var breakdown = ContextBreakdown.Estimated(50_000, 200_000);

        Assert.Equal([("In the context", "50,000", "25%"), ("Free space", "150,000", "75%")], breakdown.Rows.Select(r => (r.Name, r.Tokens, r.Percent)));
        Assert.Equal(2, breakdown.Segments.Count);
        Assert.NotNull(breakdown.Note);
        Assert.False(breakdown.HasSections);
    }

    [Fact]
    public void A_section_left_open_stays_open_when_the_breakdown_is_refreshed()
    {
        var before = ContextBreakdown.From(ContextUsage.Parse(Usage()));
        before.Sections.Single(s => s.Key == "messages").ToggleCommand.Execute(null);

        var after = ContextBreakdown.From(ContextUsage.Parse(Usage())).KeepingExpanded(before);

        Assert.Equal(["messages"], after.Sections.Where(s => s.IsExpanded).Select(s => s.Key));
    }

    [Fact]
    public async Task A_tab_has_the_breakdown_from_each_get_context_usage_reply()
    {
        await using var h = new TabTestHarness();
        h.Transport.Answers["get_context_usage"] = _ => Usage();

        var tab = await h.OpenTabAsync();

        await TabTestHarness.Eventually(() => tab.ContextBreakdown is not null, "the breakdown");
        Assert.Equal("Context 62%", tab.ContextText);
        Assert.Equal("Messages", tab.ContextBreakdown!.Rows[5].Name);
    }

    [Fact]
    public async Task Without_get_context_usage_the_breakdown_is_the_estimate()
    {
        await using var h = new TabTestHarness();
        h.Transport.Answers["get_context_usage"] = _ => throw new InvalidOperationException("Unknown control request: get_context_usage");
        var tab = await h.OpenTabAsync();

        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit("""{"type":"assistant","message":{"id":"m1","model":"claude-opus-5-5","content":[{"type":"text","text":"a"}],"usage":{"input_tokens":3000,"output_tokens":100,"cache_read_input_tokens":46900}}}""");
        h.Transport.Emit("""{"type":"result","subtype":"success","is_error":false,"session_id":"s1","modelUsage":{"claude-opus-5-5":{"inputTokens":49900,"outputTokens":100,"contextWindow":200000}}}""");

        await TabTestHarness.Eventually(() => tab.ContextBreakdown is not null, "the estimate");
        Assert.Equal("In the context", tab.ContextBreakdown!.Rows[0].Name);
        Assert.NotNull(tab.ContextBreakdown.Note);
    }

    [Fact]
    public void The_bar_puts_a_gap_between_parts_and_keeps_a_sliver_of_a_small_one()
    {
        // 10 tokens of 1,000 across 202 pixels would be 2 pixels wide: it takes 3 from the others.
        var layout = ContextBar.Layout([10, 0, 990], 202);

        Assert.Equal(2, layout.Count);
        Assert.Equal((0, 0.0, ContextBar.MinSegmentWidth), layout[0]);
        Assert.Equal(2, layout[1].Index);
        Assert.Equal(ContextBar.MinSegmentWidth + ContextBar.Gap, layout[1].X, precision: 6);
        Assert.Equal(202, layout[1].X + layout[1].Width, precision: 6);
        Assert.Empty(ContextBar.Layout([0, 0], 100));
    }
}
