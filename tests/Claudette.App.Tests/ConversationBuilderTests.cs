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
    public void Traffic_for_an_unknown_subagent_is_dropped()
    {
        Apply("""{"type":"stream_event","parent_tool_use_id":"task1","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"inner"}}}""");
        Apply("""{"type":"assistant","parent_tool_use_id":"task1","message":{"content":[{"type":"text","text":"inner"}]}}""");

        Assert.Empty(_items);
    }

    [Fact]
    public void Subagent_traffic_goes_into_its_group_even_when_nested()
    {
        Apply("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"a1","name":"Agent","input":{"description":"Find tests","prompt":"Look for tests","subagent_type":"Explore"}}]}}""");
        Apply("""{"type":"assistant","parent_tool_use_id":"a1","message":{"content":[{"type":"tool_use","id":"t1","name":"Grep","input":{"pattern":"Fact"}}]}}""");
        Apply("""{"type":"assistant","parent_tool_use_id":"a1","message":{"content":[{"type":"tool_use","id":"a2","name":"Agent","input":{"description":"Deeper"}}]}}""");
        Apply("""{"type":"assistant","parent_tool_use_id":"a2","message":{"content":[{"type":"text","text":"nested reply"}]}}""");
        Apply("""{"type":"user","parent_tool_use_id":"a1","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"3 files"}]}}""");
        Apply("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"a1","content":"Found them."}]}}""");

        var agent = Assert.IsType<SubagentItem>(Assert.Single(_items));
        Assert.Equal("Explore", agent.AgentType);
        Assert.Equal("Find tests", agent.Summary);
        Assert.True(agent.IsComplete);
        var grep = Assert.IsType<ToolUseItem>(agent.Items[0]);
        Assert.Equal("3 files", grep.ResultSummary);
        var nested = Assert.IsType<SubagentItem>(agent.Items[1]);
        Assert.Equal("nested reply", Assert.IsType<AssistantTextItem>(Assert.Single(nested.Items)).Text);
    }

    [Fact]
    public void Thinking_streams_into_a_collapsed_row()
    {
        _builder.Apply(new ThinkingDelta("Let me ", null));
        _builder.Apply(new ThinkingDelta("look.", null));
        Apply("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"Let me look."}]}}""");
        Apply("""{"type":"assistant","message":{"content":[{"type":"text","text":"Done"}]}}""");

        var thinking = Assert.IsType<ThinkingItem>(_items[0]);
        Assert.Equal("Let me look.", thinking.Text);
        Assert.False(thinking.IsStreaming);
        Assert.False(thinking.IsExpanded);
        Assert.Equal("Done", Assert.IsType<AssistantTextItem>(_items[1]).Text);
    }

    [Fact]
    public void Todo_write_fills_the_pinned_list_instead_of_a_card()
    {
        var todos = new TodoList();
        var builder = new ConversationBuilder(_items, todos);

        Apply(builder, """
            {"type":"assistant","message":{"content":[{"type":"tool_use","id":"w1","name":"TodoWrite","input":{"todos":[
              {"content":"Write tests","activeForm":"Writing tests","status":"completed"},
              {"content":"Fix bug","activeForm":"Fixing the bug","status":"in_progress"}]}}]}}
            """);

        Assert.Empty(_items);
        Assert.Equal(["Write tests", "Fixing the bug"], todos.Items.Select(t => t.DisplayText));
        Assert.Equal("To-do · 1 of 2 done", todos.Summary);
        Assert.True(todos.IsExpanded);
    }

    [Fact]
    public void Task_tools_fill_the_pinned_list_too()
    {
        var todos = new TodoList();
        var builder = new ConversationBuilder(_items, todos);

        Apply(builder, """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"c1","name":"TaskCreate","input":{"subject":"Ship it","description":"d"}}]}}""");
        Apply(builder, """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"c1","content":"Task #7 created successfully: Ship it"}]}}""");
        Apply(builder, """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"u1","name":"TaskUpdate","input":{"taskId":"7","status":"completed"}}]}}""");

        var item = Assert.Single(todos.Items);
        Assert.True(item.IsDone);
        Assert.False(todos.IsExpanded);
    }

    [Fact]
    public void Retries_update_one_note_in_place()
    {
        Apply("""{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"retry_delay_ms":2000,"error_category":"overloaded"}""");
        Apply("""{"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,"retry_delay_ms":4000,"error_category":"overloaded"}""");

        var note = Assert.IsType<NoteItem>(Assert.Single(_items));
        Assert.Contains("attempt 2 of 10", note.Text, StringComparison.Ordinal);
        Assert.Contains("overloaded", note.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_completed_turn_gets_a_summary()
    {
        Apply("""{"type":"result","subtype":"success","is_error":false,"result":"ok","duration_ms":12300,"usage":{"input_tokens":10,"cache_read_input_tokens":4000,"output_tokens":312},"modelUsage":{"claude-opus-5-5":{}}}""");

        Assert.Equal("12.3s · 4k in · 312 out · claude-opus-5-5", Assert.IsType<TurnSummaryItem>(Assert.Single(_items)).Text);
    }

    [Fact]
    public void Bash_output_comes_from_the_structured_result()
    {
        Apply("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"b1","name":"Bash","input":{"command":"dotnet test"}}]}}""");
        Apply("""{"type":"user","tool_use_result":{"stdout":"Passed!\nTotal 8","stderr":"","interrupted":false},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"b1","content":"Passed!\nTotal 8"}]}}""");

        var bash = Assert.IsType<ToolUseItem>(Assert.Single(_items));
        Assert.Equal("dotnet test", bash.Command);
        Assert.Equal("Passed!\nTotal 8", bash.Output);
        Assert.Equal("Passed!", bash.ResultSummary);
    }

    [Fact]
    public void An_edit_shows_the_structured_patch()
    {
        Apply("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"e1","name":"Edit","input":{"file_path":"a.cs","old_string":"x","new_string":"y"}}]}}""");
        var edit = Assert.IsType<ToolUseItem>(Assert.Single(_items));
        Assert.Equal("+1 −1", edit.DiffStats);

        Apply("""{"type":"user","tool_use_result":{"structuredPatch":[{"oldStart":1,"oldLines":2,"newStart":1,"newLines":3,"lines":[" a","-x","+y","+z"]}]},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"e1","content":"The file a.cs has been updated."}]}}""");

        Assert.Equal("+2 −1", edit.DiffStats);
        Assert.Null(edit.ResultSummary);
    }

    [Fact]
    public void Clear_empties_the_conversation()
    {
        var todos = new TodoList();
        var builder = new ConversationBuilder(_items, todos);
        builder.AddUserMessage("hi");

        builder.Apply(new ConversationReset("clear"));

        Assert.Equal("Conversation cleared.", Assert.IsType<NoteItem>(Assert.Single(_items)).Text);
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
    public void An_error_sent_as_the_reply_too_is_shown_once()
    {
        Apply("""{"type":"assistant","message":{"model":"<synthetic>","content":[{"type":"text","text":"Not logged in · Please run /login"}]},"error":"authentication_failed"}""");
        Apply("""{"type":"result","subtype":"success","is_error":true,"result":"Not logged in · Please run /login","terminal_reason":"api_error"}""");

        var note = Assert.IsType<NoteItem>(Assert.Single(_items));
        Assert.Equal(NoteKind.Error, note.Kind);
        Assert.Equal("Not logged in · Please run /login", note.Text);
    }

    [Fact]
    public void Model_change_notes_are_shown_without_backticks()
    {
        Apply("""{"type":"user","message":{"role":"user","content":"<local-command-stdout>Set model to `sonnet`</local-command-stdout>"}}""");

        Assert.Equal("Set model to sonnet", Assert.IsType<NoteItem>(Assert.Single(_items)).Text);
    }

    private void Apply(string line) => Apply(_builder, line);

    private static void Apply(ConversationBuilder builder, string line)
    {
        Assert.True(MessageParser.TryParse(line.ReplaceLineEndings(" "), out var message, out var error), error);
        SessionEvent sessionEvent = message switch
        {
            StreamEventMessage { TextDelta: { } text } s => new TextDelta(text, s.ParentToolUseId),
            AssistantMessage a => new AssistantMessageReceived(a),
            UserMessage { LocalCommandOutput: { } output } => new LocalCommandOutputReceived(output),
            UserMessage u => new ToolResultsReceived(u),
            ResultMessage r => new TurnCompleted(r),
            SystemMessage s => new SystemNotice(s),
            _ => throw new InvalidOperationException($"No event for {message.GetType().Name}"),
        };
        builder.Apply(sessionEvent);
    }
}
