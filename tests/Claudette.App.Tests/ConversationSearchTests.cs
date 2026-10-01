using System.Collections.ObjectModel;
using Claudette.App.Conversation;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.App.Tests;

/// <summary>Find in the conversation (DESIGN.md §5, "Find").</summary>
public class ConversationSearchTests
{
    private readonly ObservableCollection<ConversationItem> _items = [];
    private readonly ConversationBuilder _builder;
    private readonly ConversationSearch _search;

    public ConversationSearchTests()
    {
        _builder = new ConversationBuilder(_items);
        _search = new ConversationSearch(_items) { IsOpen = true };
    }

    private void Apply(string line)
    {
        Assert.True(MessageParser.TryParse(line, out var message, out var error), error);
        _builder.Apply(message switch
        {
            AssistantMessage a => new AssistantMessageReceived(a),
            UserMessage u => new ToolResultsReceived(u),
            _ => throw new InvalidOperationException(),
        });
    }

    [Fact]
    public void Matches_are_counted_and_the_newest_is_current_first()
    {
        _builder.AddUserMessage("Fix the Vulkan build");
        Apply("""{"type":"assistant","message":{"content":[{"type":"text","text":"Looking at the build."}]}}""");
        Apply("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"make vulkan"}}]}}""");
        Apply("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"error: VULKAN_SDK not set","is_error":true}]}}""");
        var moved = new List<ConversationItem>();
        _search.CurrentChanged += moved.Add;

        _search.Query = "vulkan";

        Assert.Equal(2, _search.Count);
        Assert.Equal("2 of 2", _search.CountText);
        Assert.IsType<ToolUseItem>(_search.Current);
        _search.PreviousCommand.Execute(null);
        Assert.IsType<UserMessageItem>(_search.Current);
        _search.NextCommand.Execute(null);
        _search.NextCommand.Execute(null);
        Assert.Equal("1 of 2", _search.CountText);
        Assert.Equal(4, moved.Count);
    }

    [Fact]
    public void Nothing_found_says_so_and_closing_clears_it()
    {
        _builder.AddUserMessage("hello");

        _search.Query = "absent";
        Assert.Equal("No matches", _search.CountText);
        Assert.Null(_search.Current);

        _search.CloseCommand.Execute(null);
        Assert.Equal("", _search.Query);
        Assert.Equal("", _search.CountText);
    }

    [Fact]
    public void New_items_join_the_matches_without_moving_the_current_one()
    {
        _builder.AddUserMessage("deploy one");
        _search.Query = "deploy";
        var first = _search.Current;

        _builder.AddUserMessage("deploy two");

        Assert.Equal(2, _search.Count);
        Assert.Same(first, _search.Current);
        Assert.Equal("1 of 2", _search.CountText);
    }

    [Fact]
    public void A_reply_that_streamed_in_a_match_is_found_when_the_next_item_comes()
    {
        _builder.AddUserMessage("deploy one");
        _search.Query = "rollback";
        Assert.Equal(0, _search.Count);

        // The reply's text arrives a piece at a time; it isn't an item added, so it's looked at again with the next.
        _builder.Apply(new TextDelta("Then a ", null));
        _builder.Apply(new TextDelta("rollback.", null));
        Assert.Equal(0, _search.Count);
        _builder.AddUserMessage("thanks");

        var reply = Assert.IsType<AssistantTextItem>(Assert.Single(_items, i => i is AssistantTextItem));
        Assert.Same(reply, _search.Current);
        Assert.Equal("1 of 1", _search.CountText);

        // Removing items (a /clear) searches everything again.
        _items.Clear();
        Assert.Equal(0, _search.Count);
        Assert.Equal("No matches", _search.CountText);
    }
}
