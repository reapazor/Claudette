using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.App.Tests;

/// <summary>The tasks Claude is working through, for the side panel's Tasks page (DESIGN.md §5, "Tasks").</summary>
public class TaskBoardTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-01T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private readonly TodoList _list;

    public TaskBoardTests() => _list = new TodoList { Time = _time };

    private static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private void Create(string toolUseId, string id, string subject, string? description = null)
    {
        var input = new JsonObject { ["subject"] = subject };
        if (description is not null)
        {
            input["description"] = description;
        }
        _list.ApplyToolUse(toolUseId, "TaskCreate", input);
        _list.ApplyToolResult(toolUseId, $"Task #{id} created", new JsonObject { ["task"] = new JsonObject { ["id"] = id, ["subject"] = subject } });
    }

    [Fact]
    public void An_error_result_which_is_only_text_changes_nothing()
    {
        _list.ApplyToolUse("t1", "TaskCreate", new JsonObject { ["subject"] = "Write tests" });

        // Claude Code gives a failed call's details as its error text.
        _list.ApplyToolResult("t1", "<tool_use_error>No such task</tool_use_error>", JsonValue.Create("Error: No such task"));
        _list.ApplyToolResult("t2", "Error", JsonValue.Create("Error"));
        _list.ApplyToolResult("t3", "", new JsonObject { ["task"] = "not an object" });

        var item = Assert.Single(_list.Items);
        Assert.Null(item.Id);
    }

    [Fact]
    public void A_restored_tab_dates_its_tasks_by_the_transcript_not_the_restore()
    {
        var builder = new ConversationBuilder([], _list) { Time = _time };
        var written = DateTimeOffset.Parse("2026-09-30T16:20:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(MessageParser.TryParse("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"TodoWrite","input":{"todos":[{"content":"Fix it","status":"in_progress","activeForm":"Fixing it"}]}}]}}""", out var message, out _));

        builder.Replay(new AssistantMessageReceived((AssistantMessage)message!), written);

        Assert.Equal(written, _list.Items.Single().CreatedAt);
        Assert.Equal(written, _list.Items.Single().StartedAt);
    }

    [Fact]
    public void Tasks_keep_their_description_owner_and_times_and_count_toward_the_badge()
    {
        Create("t1", "1", "Read the build script", "Find where the shipping config is chosen.");
        Create("t2", "2", "Fix the config");
        Assert.Equal("0 of 2", _list.Badge);

        _time.Advance(TimeSpan.FromMinutes(1));
        _list.ApplyToolUse("t3", "TaskUpdate", Json("""{"taskId":"1","status":"in_progress","owner":"explorer"}"""));
        Assert.Same(_list.Items[0], _list.Current);
        _time.Advance(TimeSpan.FromMinutes(4));
        _list.ApplyToolUse("t4", "TaskUpdate", Json("""{"taskId":"1","status":"completed"}"""));

        var first = _list.Items[0];
        Assert.Equal("#1", first.NumberText);
        Assert.Equal("Find where the shipping config is chosen.", first.Description);
        Assert.Equal("explorer", first.Owner);
        Assert.Equal("Took 4m", first.TimeText);
        // One line under the task: who's on it, then its time, with nothing in front when nobody is.
        Assert.Equal("explorer · Took 4m", first.DetailText);
        Assert.StartsWith("Added ", _list.Items[1].DetailText, StringComparison.Ordinal);
        Assert.Equal("1 of 2", _list.Badge);
        Assert.Null(_list.Current);
    }

    [Fact]
    public void A_task_waits_on_its_blockers_until_theyre_done()
    {
        Create("t1", "1", "Write the migration");
        Create("t2", "2", "Run the migration");
        _list.ApplyToolUse("t3", "TaskUpdate", Json("""{"taskId":"2","addBlockedBy":["1"]}"""));

        Assert.Equal("Waiting on #1", _list.Items[1].BlockedText);

        _list.ApplyToolUse("t4", "TaskUpdate", Json("""{"taskId":"1","status":"completed"}"""));
        Assert.Null(_list.Items[1].BlockedText);
    }

    [Fact]
    public void A_task_list_result_fills_in_what_the_calls_didnt_show()
    {
        Create("t1", "1", "Known");

        _list.ApplyToolResult("t9", "", Json("""{"tasks":[{"id":"1","subject":"Known","status":"in_progress","blockedBy":[]},{"id":"2","subject":"From a subagent","status":"pending","owner":"helper","blockedBy":["1"]}]}"""));

        Assert.Equal(["Known", "From a subagent"], _list.Items.Select(i => i.Content));
        Assert.True(_list.Items[0].IsActive);
        Assert.Equal("helper", _list.Items[1].Owner);
        Assert.Equal("Waiting on #1", _list.Items[1].BlockedText);
    }

    [Fact]
    public void An_approved_plan_heads_the_list_until_its_cleared()
    {
        _list.SetPlan("  1. Read\n2. Fix  ");

        Assert.Equal("1. Read\n2. Fix", _list.Plan);
        Assert.True(_list.HasAnything);
        Assert.Equal(_time.GetUtcNow(), _list.PlanApprovedAt);

        _list.Clear();
        Assert.False(_list.HasAnything);
    }

    [Fact]
    public void A_rewritten_todo_list_keeps_the_times_of_items_that_stayed()
    {
        _list.ApplyToolUse("w1", "TodoWrite", Json("""{"todos":[{"content":"A","status":"in_progress"},{"content":"B","status":"pending"}]}"""));
        var started = _list.Items[0].StartedAt;
        _time.Advance(TimeSpan.FromMinutes(2));

        _list.ApplyToolUse("w2", "TodoWrite", Json("""{"todos":[{"content":"A","status":"completed"},{"content":"B","status":"in_progress"}]}"""));

        Assert.Equal(started, _list.Items[0].StartedAt);
        Assert.Equal("Took 2m", _list.Items[0].TimeText);
        Assert.Equal(_time.GetUtcNow(), _list.Items[1].StartedAt);
    }
}
