using System.Text.Json.Nodes;
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
    public void Claude_Codes_notices_are_notes_at_their_level()
    {
        // As the Agent SDK documents system/informational: a hook's message to the user, and a fallback warning.
        Apply("""{"type":"system","subtype":"informational","content":"PostToolUse:Bash says: Formatted 3 files","level":"info","tool_use_id":"t1","uuid":"i-1","session_id":"s"}""");
        Apply("""{"type":"system","subtype":"informational","content":"  Switched to Sonnet: the context window is now 200K tokens.\n","level":"warning","uuid":"i-2","session_id":"s"}""");
        Apply("""{"type":"system","subtype":"informational","content":"A tip.","level":"someday","uuid":"i-3","session_id":"s"}""");
        Apply("""{"type":"system","subtype":"informational","content":"  ","level":"warning","uuid":"i-4","session_id":"s"}""");

        var notes = _items.OfType<NoteItem>().Select(n => (n.Text, n.Kind)).ToList();
        Assert.Equal([
            ("PostToolUse:Bash says: Formatted 3 files", NoteKind.Info),
            ("Switched to Sonnet: the context window is now 200K tokens.", NoteKind.Warning),
            ("A tip.", NoteKind.Info),
        ], notes);
    }

    [Fact]
    public void A_bash_card_says_what_git_did_and_how_the_command_ended()
    {
        string Result(string id, JsonObject toolUseResult) => new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = "done" }) },
            ["tool_use_result"] = toolUseResult,
        }.ToJsonString();
        Apply("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"b1","name":"Bash","input":{"command":"git commit -am fix && git push && gh pr create"}},{"type":"tool_use","id":"b2","name":"Bash","input":{"command":"npm test"}},{"type":"tool_use","id":"b3","name":"Bash","input":{"command":"npm run dev","run_in_background":true}}]}}""");

        // As the Agent SDK documents Bash's output.
        Apply(Result("b1", JsonNode.Parse("""
            {"stdout":"[main 1a2b3c4] fix","stderr":"","interrupted":false,
             "gitOperation":{"commit":{"sha":"1a2b3c4d5e6f","kind":"committed","branch":"main"},"push":{"branch":"main"},
                             "branch":{"ref":"origin/main","action":"rebased"},"pr":{"number":42,"url":"https://github.com/o/r/pull/42","action":"created"}}}
            """)!.AsObject()));
        Apply(Result("b2", new JsonObject { ["stdout"] = "", ["stderr"] = "", ["interrupted"] = false, ["backgroundTaskId"] = "bash_1", ["timedOutAfterMs"] = 120000 }));
        Apply(Result("b3", new JsonObject { ["stdout"] = "", ["stderr"] = "", ["interrupted"] = false, ["backgroundTaskId"] = "bash_2" }));

        var cards = _items.OfType<ToolUseItem>().ToList();
        Assert.Equal(["Committed 1a2b3c4 on main", "Pushed main", "Rebased onto origin/main", "Opened PR #42"], cards[0].GitChips.Select(c => c.Text));
        Assert.Equal("https://github.com/o/r/pull/42", cards[0].GitChips[^1].Url);
        Assert.Equal("Reached its 2m 00s time limit; carries on in the background", cards[1].ResultSummary);
        Assert.Empty(cards[1].GitChips);
        Assert.Equal("Running in the background", cards[2].ResultSummary);
    }

    [Fact]
    public void Git_chips_name_only_what_they_know_and_link_only_to_web_addresses()
    {
        var chips = GitChips.From(JsonNode.Parse("""
            {"commit":{"sha":"abc","kind":"amended"},"pr":{"number":7,"url":"javascript:alert(1)","action":"something-new"},"push":{}}
            """)!.AsObject());

        Assert.Equal(["Amended abc", "PR #7"], chips.Select(c => c.Text));
        Assert.Null(chips[1].Url);
        Assert.Empty(GitChips.From(null));
    }

    // ---- Rewind and branch points (DESIGN.md §5) ------------------------------------------------------------------

    [Fact]
    public void A_prompt_gets_its_id_when_echoed_and_knows_where_to_resume_before_it()
    {
        var first = _builder.AddUserMessage("one");
        Apply("""{"type":"user","uuid":"u-1","isReplay":true,"message":{"role":"user","content":"one"}}""");
        Apply("""{"type":"assistant","uuid":"a-1","message":{"content":[{"type":"text","text":"done one"}]}}""");
        var second = _builder.AddUserMessage("two");
        var third = _builder.AddUserMessage("three");
        // Queued prompts are echoed in the order they're taken.
        Apply("""{"type":"user","uuid":"u-2","isReplay":true,"message":{"role":"user","content":[{"type":"text","text":"two"}]}}""");
        Apply("""{"type":"user","uuid":"u-3","isReplay":true,"message":{"role":"user","content":"three"}}""");

        Assert.Equal(("u-1", (string?)null), (first.Uuid, first.ResumeAt));
        Assert.Equal(("u-2", "a-1"), (second.Uuid, second.ResumeAt));
        // Taken after "two", so it follows it, whatever was last when it was sent.
        Assert.Equal(("u-3", "u-2"), (third.Uuid, third.ResumeAt));
        Assert.True(second.CanRestoreFiles);
    }

    [Fact]
    public void A_cleared_conversation_starts_without_a_resume_point()
    {
        Apply("""{"type":"assistant","uuid":"a-1","message":{"content":[{"type":"text","text":"before"}]}}""");
        _builder.Clear();

        Assert.Null(_builder.AddUserMessage("after").ResumeAt);
    }

    // ---- Hook runs (DESIGN.md §5) -------------------------------------------------------------------------------

    private static string Hook(string subtype, string id, string? outcome = null, int? exitCode = null, string? output = null, string? stdout = null, string? stderr = null)
    {
        var message = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "system", ["subtype"] = subtype, ["hook_id"] = id, ["hook_name"] = "PreToolUse:Bash", ["hook_event"] = "PreToolUse",
            ["session_id"] = "s", ["uuid"] = "x",
        };
        if (outcome is not null)
        {
            message["outcome"] = outcome;
        }
        if (exitCode is not null)
        {
            message["exit_code"] = exitCode;
        }
        message["output"] = output;
        message["stdout"] = stdout;
        message["stderr"] = stderr;
        return message.ToJsonString();
    }

    [Fact]
    public void A_hook_that_succeeds_quietly_shows_nothing_by_default()
    {
        Apply(Hook("hook_started", "h1"));
        Apply(Hook("hook_response", "h1", "success", 0, "", "", ""));

        Assert.Empty(_items);
    }

    [Fact]
    public void A_hook_that_fails_shows_its_output_open()
    {
        Apply(Hook("hook_started", "h1"));
        Apply(Hook("hook_response", "h1", "error", 2, stdout: "", stderr: "lint failed: 3 errors\n"));

        var run = Assert.IsType<HookRunItem>(Assert.Single(_items));
        Assert.Equal("PreToolUse hook (PreToolUse:Bash)", run.Title);
        Assert.True(run.IsFailed);
        Assert.Equal("failed (exit code 2)", run.StatusText);
        Assert.Equal("lint failed: 3 errors", run.Output);
        Assert.True(run.IsExpanded);
    }

    [Fact]
    public void Every_run_shows_when_asked_and_progress_fills_it_in()
    {
        _builder.ShowAllHookRuns = true;
        Apply(Hook("hook_started", "h1"));
        var run = Assert.IsType<HookRunItem>(Assert.Single(_items));
        Assert.True(run.IsRunning);

        Apply(Hook("hook_progress", "h1", output: "step 1", stdout: "step 1\n", stderr: ""));
        Assert.Equal("step 1", run.Output);
        Apply(Hook("hook_response", "h1", "success", 0, "step 1\nstep 2", "", ""));

        Assert.Single(_items);
        Assert.Equal(HookRunState.Succeeded, run.State);
        Assert.Equal("step 1\nstep 2", run.Output);
        Assert.False(run.IsExpanded);
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
    public void Long_thinking_streams_in_without_copying_it_for_every_piece()
    {
        _builder.Apply(new ThinkingDelta("a", null));
        var thinking = Assert.IsType<ThinkingItem>(_items[0]);
        var (changes, shown) = (0, 0);
        thinking.PropertyChanged += (_, e) =>
        {
            changes += e.PropertyName == nameof(ThinkingItem.Text) ? 1 : 0;
            shown += e.PropertyName == nameof(ThinkingItem.ShownText) ? 1 : 0;
        };

        for (var i = 0; i < 2000; i++)
        {
            _builder.Apply(new ThinkingDelta("bc", null));
        }

        // Collapsed, the view has nothing to show; the text is said to change as it grows by an eighth, not every piece.
        Assert.Equal(0, shown);
        Assert.Null(thinking.ShownText);
        Assert.InRange(changes, 5, 40);
        Assert.Equal(4001, thinking.Text.Length);
        Assert.Same(thinking.Text, thinking.Text);
        Assert.True(thinking.HasText);

        // Expanded, it shows all of it so far, and grows from there.
        thinking.IsExpanded = true;
        Assert.Equal(1, shown);
        Assert.Equal(4001, thinking.ShownText!.Length);
        for (var i = 0; i < 1000; i++)
        {
            _builder.Apply(new ThinkingDelta("de", null));
        }
        Assert.InRange(shown, 2, 10);
        // Once it's done, the view has every last piece.
        Apply("""{"type":"assistant","message":{"content":[{"type":"text","text":"Done"}]}}""");
        Assert.False(thinking.IsStreaming);
        Assert.Equal(6001, thinking.ShownText!.Length);
    }

    [Fact]
    public void A_tool_card_keeps_its_input_but_not_the_message_it_came_in()
    {
        Apply("""{"type":"assistant","message":{"content":[{"type":"text","text":"big reply"},{"type":"tool_use","id":"r1","name":"Read","input":{"file_path":"a.cs"}}]}}""");

        var read = Assert.IsType<ToolUseItem>(_items[1]);
        Assert.Null(read.Input.Parent);
        Assert.Equal("a.cs", read.Input["file_path"]!.GetValue<string>());
    }

    [Fact]
    public void A_long_output_shows_its_start_until_Show_all()
    {
        Apply("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"g1","name":"Grep","input":{"pattern":"x"}}]}}""");
        var grep = Assert.IsType<ToolUseItem>(Assert.Single(_items));
        var output = new string('x', ToolUseItem.ShownOutputLimit + 5000);

        grep.ApplyResult(output, isError: false, toolUseResult: null);

        Assert.True(grep.IsOutputCut);
        Assert.Equal(ToolUseItem.ShownOutputLimit, grep.ShownOutput!.Length);
        Assert.Equal("Show all (25 KB)", grep.ShowAllOutputText);
        grep.ShowAllOutputCommand.Execute(null);
        Assert.False(grep.IsOutputCut);
        Assert.Equal(output, grep.ShownOutput);
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
    public void Structured_results_with_unexpected_types_are_read_leniently()
    {
        // Claude Code changing a field's type must not throw out of the event (CLAUDE.md, "Parse tolerantly").
        Apply("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"b1","name":"Bash","input":{"command":"sleep 5"}},{"type":"tool_use","id":"w1","name":"Write","input":{"file_path":"a.cs","content":"x"}}]}}""");
        Apply("""{"type":"user","tool_use_result":{"stdout":"done","stderr":"","interrupted":"yes"},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"b1","content":"done"}]}}""");
        Apply("""{"type":"user","tool_use_result":{"type":7,"content":"x"},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"w1","content":"File created"}]}}""");

        var tools = _items.OfType<ToolUseItem>().ToList();
        Assert.Equal("done", tools[0].ResultSummary);
        Assert.True(tools[1].IsComplete);
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

    [Fact]
    public void A_subagents_tasks_and_a_task_list_reach_the_tasks_but_its_own_todo_list_stays_in_its_group()
    {
        var items = new ObservableCollection<ConversationItem>();
        var tasks = new TodoList();
        var builder = new ConversationBuilder(items, tasks);
        Apply(builder, """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"a1","name":"Agent","input":{"description":"Helper"}}]}}""");
        Apply(builder, """{"type":"assistant","parent_tool_use_id":"a1","message":{"content":[{"type":"tool_use","id":"t1","name":"TaskCreate","input":{"subject":"From the helper"}}]}}""");
        Apply(builder, """{"type":"user","parent_tool_use_id":"a1","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"Task #1 created"}]},"tool_use_result":{"task":{"id":"1","subject":"From the helper"}}}""");
        Apply(builder, """{"type":"assistant","parent_tool_use_id":"a1","message":{"content":[{"type":"tool_use","id":"w1","name":"TodoWrite","input":{"todos":[{"content":"Helper's own","status":"pending"}]}}]}}""");
        Apply(builder, """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"l1","name":"TaskGet","input":{"taskId":"1"}}]}}""");
        Apply(builder, """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"l1","content":"…"}]},"tool_use_result":{"task":{"id":"1","subject":"From the helper","status":"in_progress","owner":"helper"}}}""");

        var task = Assert.Single(tasks.Items);
        Assert.Equal(("1", "From the helper", "helper", true), (task.Id, task.Content, task.Owner, task.IsActive));
        var group = Assert.IsType<SubagentItem>(Assert.Single(items));
        Assert.Equal("TodoWrite", Assert.IsType<ToolUseItem>(Assert.Single(group.Items)).Name);
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
            UserMessage { IsReplay: true } u => new PromptReplayed(u),
            UserMessage u => new ToolResultsReceived(u),
            ResultMessage r => new TurnCompleted(r),
            SystemMessage s => new SystemNotice(s),
            _ => throw new InvalidOperationException($"No event for {message.GetType().Name}"),
        };
        builder.Apply(sessionEvent);
    }
}
