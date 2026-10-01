using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;

namespace Claudette.App.Tests;

/// <summary>The tab's find bar (DESIGN.md §5, "Find"): the match it moves to is opened if need be and brought into view.</summary>
public sealed class FindTests
{
    private static void EmitBash(TabTestHarness h, string id, string command, string output)
    {
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = command } }) },
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = output }) },
        });
    }

    [Fact]
    public async Task A_match_only_in_a_collapsed_cards_output_opens_it_and_is_brought_into_view()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        EmitBash(h, "t1", "make vulkan", "error: VULKAN_SDK not set");
        EmitBash(h, "t2", "make metal", "ok");
        await TabTestHarness.Eventually(() => tab.IsSettled && tab.Items.OfType<ToolUseItem>().Count() == 2, "the cards");
        var scrolled = new List<ConversationItem>();
        tab.ScrollToRequested += scrolled.Add;
        var (vulkan, metal) = (tab.Items.OfType<ToolUseItem>().First(), tab.Items.OfType<ToolUseItem>().Last());

        tab.OpenFindCommand.Execute(null);
        tab.Find.Query = "VULKAN_SDK";

        Assert.Same(vulkan, tab.Find.Current);
        Assert.True(vulkan.IsExpanded);
        Assert.Equal([vulkan], scrolled);

        // The command is in the card's header: nothing to open.
        tab.Find.Query = "make metal";
        Assert.Same(metal, tab.Find.Current);
        Assert.False(metal.IsExpanded);
        Assert.Same(metal, scrolled[^1]);
    }
}
