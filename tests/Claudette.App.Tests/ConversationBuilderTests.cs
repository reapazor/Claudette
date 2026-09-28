using System.Collections.ObjectModel;
using Claudette.App.Conversation;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.App.Tests;

public class ConversationBuilderTests
{
    private readonly ObservableCollection<ConversationItem> _items = [];
    private readonly ConversationBuilder _builder;

    public ConversationBuilderTests()
    {
        _builder = new ConversationBuilder(_items);
    }

    [Fact]
    public void Streamed_text_is_not_duplicated_by_the_complete_message()
    {
        _builder.AddUserMessage("hi");
        Apply("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"po"}}}""");
        Apply("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"ng"}}}""");
        Apply("""{"type":"assistant","message":{"content":[{"type":"text","text":"pong"}]}}""");

        var text = Assert.IsType<AssistantTextItem>(_items[1]);
        Assert.Equal("pong", text.Text);
        Assert.False(text.IsStreaming);
        Assert.Equal(2, _items.Count);
    }

    [Fact]
    public void Text_without_deltas_comes_from_the_complete_message()
    {
        Apply("""{"type":"assistant","message":{"content":[{"type":"text","text":"whole"}]}}""");

        Assert.Equal("whole", Assert.IsType<AssistantTextItem>(Assert.Single(_items)).Text);
    }

    [Fact]
    public void Each_streamed_block_gets_its_own_item()
    {
        Apply("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"one"}}}""");
        Apply("""{"type":"assistant","message":{"content":[{"type":"text","text":"one"}]}}""");
        Apply("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"two"}}}""");
        Apply("""{"type":"assistant","message":{"content":[{"type":"text","text":"two"}]}}""");

        Assert.Equal(["one", "two"], _items.OfType<AssistantTextItem>().Select(t => t.Text));
    }

    [Fact]
    public void Tool_results_complete_their_tool_call()
    {
        Apply("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"src/app.cs"}}]}}""");
        Apply("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"line one\nline two","is_error":false}]}}""");

        var tool = Assert.IsType<ToolUseItem>(Assert.Single(_items));
        Assert.Equal("Read", tool.Name);
        Assert.Equal("src/app.cs", tool.Summary);
        Assert.Equal("line one", tool.ResultSummary);
        Assert.True(tool.IsComplete);
        Assert.False(tool.IsError);
    }

    [Fact]
    public void Subagent_traffic_is_not_shown_yet()
    {
        Apply("""{"type":"stream_event","parent_tool_use_id":"task1","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"inner"}}}""");
        Apply("""{"type":"assistant","parent_tool_use_id":"task1","message":{"content":[{"type":"text","text":"inner"}]}}""");

        Assert.Empty(_items);
    }

    [Fact]
    public void An_interrupted_turn_adds_a_stopped_note()
    {
        Apply("""{"type":"result","subtype":"error_during_execution","is_error":true,"terminal_reason":"aborted_streaming"}""");

        var note = Assert.IsType<NoteItem>(Assert.Single(_items));
        Assert.Equal("Stopped.", note.Text);
        Assert.Equal(NoteKind.Warning, note.Kind);
    }

    [Fact]
    public void A_failed_turn_shows_its_error()
    {
        Apply("""{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login","terminal_reason":"api_error"}""");

        var note = Assert.IsType<NoteItem>(Assert.Single(_items));
        Assert.Equal(NoteKind.Error, note.Kind);
    }

    [Fact]
    public void Model_change_notes_are_shown_without_backticks()
    {
        Apply("""{"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model to `sonnet`</local-command-stdout>"}}""");

        Assert.Equal("Set model to sonnet", Assert.IsType<NoteItem>(Assert.Single(_items)).Text);
    }

    private void Apply(string line)
    {
        Assert.True(MessageParser.TryParse(line, out var message, out var error), error);
        SessionEvent sessionEvent = message switch
        {
            StreamEventMessage { TextDelta: { } text } s => new TextDelta(text, s.ParentToolUseId),
            AssistantMessage a => new AssistantMessageReceived(a),
            UserMessage { LocalCommandOutput: { } output } => new LocalCommandOutputReceived(output),
            UserMessage u => new ToolResultsReceived(u),
            ResultMessage r => new TurnCompleted(r),
            _ => throw new InvalidOperationException($"No event for {message.GetType().Name}"),
        };
        _builder.Apply(sessionEvent);
    }
}
