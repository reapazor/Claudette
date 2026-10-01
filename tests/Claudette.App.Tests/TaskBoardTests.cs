using System.Text.Json.Nodes;
using Claudette.App.Conversation;
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
