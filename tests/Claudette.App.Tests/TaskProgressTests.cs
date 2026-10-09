using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>
/// How far Claude's tasks have got (DESIGN.md §5, "Tasks"): the rows where tasks start, what each task did, the row at
/// a turn's end with Continue, the status line, the tab's row and its info card.
/// </summary>
public class TaskProgressTests
{
    private static string Init(string mode = "default") =>
        $$"""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"{{mode}}"}""";

    /// <summary>An assistant message with one block, from a call with an id and usage.</summary>
    private static string Call(string? parent, string messageId, JsonObject block, long input = 1000, long output = 200) => new JsonObject
    {
        ["type"] = "assistant",
        ["parent_tool_use_id"] = parent,
        ["message"] = new JsonObject
        {
            ["id"] = messageId,
            ["role"] = "assistant",
            ["content"] = new JsonArray(block),
            ["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output },
        },
    }.ToJsonString();

    private static JsonObject ToolBlock(string id, string name, JsonObject input) =>
        new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input };

    private static void Create(TabTestHarness h, string toolUseId, string id, string subject)
    {
        h.Transport.Emit(Wire.Tool(null, toolUseId, "TaskCreate", new JsonObject { ["subject"] = subject, ["activeForm"] = $"Doing {subject}" }));
        h.Transport.Emit(Wire.Result(toolUseId, $"Task #{id} created", toolUseResult: new JsonObject { ["task"] = new JsonObject { ["id"] = id, ["subject"] = subject } }));
    }

    private static void Update(TabTestHarness h, string toolUseId, string id, string status, string? parent = null) =>
        h.Transport.Emit(Wire.Tool(parent, toolUseId, "TaskUpdate", new JsonObject { ["taskId"] = id, ["status"] = status }));

    private static void Edit(TabTestHarness h, string toolUseId, string path, string? parent = null)
    {
        h.Transport.Emit(Wire.Tool(parent, toolUseId, "Edit", new JsonObject { ["file_path"] = path, ["old_string"] = "a", ["new_string"] = "b" }));
        h.Transport.Emit(Wire.Result(toolUseId, "ok", parent: parent, toolUseResult: new JsonObject { ["filePath"] = path, ["originalFile"] = "a", ["structuredPatch"] = new JsonArray() }));
    }

    [Fact]
    public async Task A_task_that_starts_gets_a_row_where_it_starts_which_follows_it_to_done()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Init());
        Create(h, "c1", "1", "Run the migration");
        Update(h, "u1", "1", "in_progress");
        await TabTestHarness.Eventually(() => tab.Items.OfType<TaskStartItem>().Any(), "the row");

        var row = tab.Items.OfType<TaskStartItem>().Single();
        Assert.Equal("#1 Run the migration", row.Todo.Title);
        Assert.StartsWith("Started ", row.TimeText, StringComparison.Ordinal);
        Assert.Same(row, tab.TodoList.Items.Single().Start);

        h.Time.Advance(TimeSpan.FromMinutes(4));
        Update(h, "u2", "1", "completed");
        await TabTestHarness.Eventually(() => row.Todo.IsDone, "the task done");
        Assert.Equal("Took 4m", row.TimeText);

        // Going back to it from the Tasks page scrolls the conversation there.
        ConversationItem? scrolled = null;
        tab.ScrollToRequested += item => scrolled = item;
        tab.GoToTaskCommand.Execute(tab.TodoList.Items.Single());
        Assert.Same(row, scrolled);
    }

    [Fact]
    public async Task A_task_that_starts_again_keeps_its_first_row_and_find_looks_through_them()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Init());
        Create(h, "c1", "1", "Run the migration");
        Update(h, "u1", "1", "in_progress");
        Update(h, "u2", "1", "pending");
        Update(h, "u3", "1", "in_progress");
        h.Transport.Emit(Wire.Text(null, "Back on it."));
        await TabTestHarness.Eventually(() => tab.Items.OfType<AssistantTextItem>().Any(), "the updates read");

        Assert.True(tab.TodoList.Items.Single().IsActive);

        Assert.Single(tab.Items.OfType<TaskStartItem>());
        Assert.Contains("#1 Run the migration", ConversationSearch.TextOf(tab.Items.OfType<TaskStartItem>().Single()));
    }

    [Fact]
    public async Task Files_and_tokens_count_toward_the_task_in_progress_and_a_subagents_toward_its_parents()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Init());
        Create(h, "c1", "1", "Fix the config");
        Create(h, "c2", "2", "Write the docs");

        // Before any task is in progress, work counts toward none.
        Edit(h, "e0", Path.Combine(h.WorkFolder, "before.cs"));
        Update(h, "u1", "1", "in_progress");
        h.Transport.Emit(Call(null, "m1", new JsonObject { ["type"] = "text", ["text"] = "Editing." }, input: 1000, output: 200));
        // The same call again, as Claude Code repeats it on each block: counted once.
        h.Transport.Emit(Call(null, "m1", ToolBlock("e1", "Edit", new JsonObject { ["file_path"] = Path.Combine(h.WorkFolder, "app.json"), ["old_string"] = "a", ["new_string"] = "b" }), input: 1000, output: 200));
        h.Transport.Emit(Wire.Result("e1", "ok", toolUseResult: new JsonObject { ["originalFile"] = "a" }));
        // A subagent working for the main agent's task.
        h.Transport.Emit(Wire.Agent("a1", "Help", "Edit the schema"));
        Edit(h, "e2", Path.Combine(h.WorkFolder, "schema.json"), parent: "a1");
        Edit(h, "e3", Path.Combine(h.WorkFolder, "app.json"));
        await TabTestHarness.Eventually(() => tab.TodoList.Items is [{ Files.Count: 2 }, ..], "the task's files");

        var task = tab.TodoList.Items[0];
        Assert.Equal(["app.json", "schema.json"], task.Files.Select(f => f.FileName));
        Assert.Equal("e1", task.Files[0].ToolUseId);
        Assert.Equal(1200, task.Tokens);
        Assert.Equal("· 2 files", task.FilesLinkText);
        Assert.Empty(tab.TodoList.Items[1].Files);

        // With two in progress, the main agent's work counts toward neither.
        Update(h, "u2", "2", "in_progress");
        Edit(h, "e4", Path.Combine(h.WorkFolder, "readme.md"));
        await TabTestHarness.Eventually(() => tab.Items.OfType<ToolUseItem>().Any(t => t.ToolUseId == "e4" && t.IsComplete), "the last change made");
        Assert.Equal(2, task.Files.Count);
        Assert.Empty(tab.TodoList.Items[1].Files);
    }

    [Fact]
    public async Task A_task_a_subagent_starts_is_its_own_and_its_row_is_in_its_group()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Init());
        Create(h, "c1", "1", "Main work");
        Create(h, "c2", "2", "Helper work");
        Update(h, "u1", "1", "in_progress");
        h.Transport.Emit(Wire.Agent("a1", "Help", "Do #2"));
        Update(h, "u2", "2", "in_progress", parent: "a1");
        Edit(h, "e1", Path.Combine(h.WorkFolder, "helper.cs"), parent: "a1");
        Edit(h, "e2", Path.Combine(h.WorkFolder, "main.cs"));
        await TabTestHarness.Eventually(() => tab.TodoList.Items is [{ Files.Count: 1 }, { Files.Count: 1 }], "each task's file");

        Assert.Equal("main.cs", tab.TodoList.Items[0].Files.Single().FileName);
        Assert.Equal("helper.cs", tab.TodoList.Items[1].Files.Single().FileName);
        var group = tab.Items.OfType<SubagentItem>().Single();
        Assert.Same(tab.TodoList.Items[1], group.Items.OfType<TaskStartItem>().Single().Todo);

        // Going to it opens the group.
        tab.GoToTaskCommand.Execute(tab.TodoList.Items[1]);
        Assert.True(group.IsExpanded);
    }

    [Fact]
    public async Task A_turn_that_changed_the_list_ends_with_where_it_stands_and_Continue_while_tasks_are_left()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Init());
        Create(h, "c1", "1", "Fix the config");
        Create(h, "c2", "2", "Write the docs");
        Update(h, "u1", "1", "in_progress");
        await TabTestHarness.Eventually(() => tab.TodoList.Current is not null, "the first task started");
        h.Time.Advance(TimeSpan.FromMinutes(3));
        Update(h, "u2", "1", "completed");
        h.Transport.Emit(Wire.TurnResult());
        await TabTestHarness.Eventually(() => tab.Items.OfType<TasksSummaryItem>().Any() && !tab.IsWorking, "the row at the turn's end");

        var summary = tab.Items.OfType<TasksSummaryItem>().Single();
        Assert.Equal("1 of 2 tasks left · Next: #2 Write the docs", summary.Text);
        Assert.True(summary.IsContinueOffered);
        Assert.IsType<TurnSummaryItem>(tab.Items[^1]);
        Assert.Same(summary, tab.Items[^2]);
        Assert.StartsWith("1 of 2 tasks left · Next: #2 Write the docs", h.Notifier.Last("TurnFinished:")!.Body, StringComparison.Ordinal);

        await tab.ContinueTasksCommand.ExecuteAsync(summary);
        Assert.False(summary.IsContinueOffered);
        Assert.Contains(TabViewModel.ContinueTasksMessage, h.Transport.SentUserTexts);
        Assert.Equal(TabViewModel.ContinueTasksMessage, tab.Items.OfType<UserMessageItem>().Last().Text);

        // The next turn finishes the list.
        h.Transport.Emit(Init());
        Update(h, "u3", "2", "in_progress");
        await TabTestHarness.Eventually(() => tab.TodoList.Current is not null, "the second task started");
        h.Time.Advance(TimeSpan.FromMinutes(2));
        Update(h, "u4", "2", "completed");
        h.Transport.Emit(Wire.TurnResult());
        await TabTestHarness.Eventually(() => tab.Items.OfType<TasksSummaryItem>().Count() == 2 && !tab.IsWorking, "the second turn's row");

        var done = tab.Items.OfType<TasksSummaryItem>().Last();
        Assert.Equal("All 2 tasks done · 5m", done.Text);
        Assert.False(done.IsContinueOffered);
        Assert.Equal("All 2 tasks done", tab.TodoList.StatusText);
    }

    [Fact]
    public async Task A_turn_that_didnt_change_the_list_has_no_such_row()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Init());
        Create(h, "c1", "1", "Fix the config");
        h.Transport.Emit(Wire.TurnResult());
        await TabTestHarness.Eventually(() => tab.Items.OfType<TasksSummaryItem>().Any() && !tab.IsWorking, "the first turn's row");

        // Only reading the list changes nothing.
        h.Transport.Emit(Init());
        h.Transport.Emit(Wire.Tool(null, "l1", "TaskList", new JsonObject()));
        h.Transport.EmitTurn("Just a question answered.");
        await TabTestHarness.Eventually(() => tab.Items.OfType<TurnSummaryItem>().Count() == 2, "the second turn's footer");

        Assert.Single(tab.Items.OfType<TasksSummaryItem>());
        Assert.False(tab.Items.OfType<TasksSummaryItem>().Single().IsContinueOffered);
    }

    [Fact]
    public async Task The_status_line_the_row_and_the_info_card_say_how_far_the_tasks_have_got()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Init());
        Create(h, "c1", "1", "Fix the config");
        Create(h, "c2", "2", "Write the docs");
        Update(h, "u1", "1", "in_progress");
        await TabTestHarness.Eventually(() => tab.TodoList.Current is not null && tab.Status == TabStatus.Working, "the task in progress");

        Assert.Equal("0 of 2 tasks · Doing Fix the config", tab.TodoList.StatusText);
        Assert.True(tab.ShowTaskProgressBadge);
        Assert.Equal("0/2", tab.TodoList.ProgressShort);
        Assert.Equal("0 of 2 tasks done · Now: Doing Fix the config", tab.TodoList.ProgressTip);
        Assert.Equal("Doing Fix the config", tab.RowDetail);
        Assert.Contains(new InfoRow("Tasks", "0 of 2 done · Now: Doing Fix the config"), tab.InfoRows);

        // Off in Settings: no badge, and the model is back on the row.
        h.Services.Settings.Appearance.ShowTaskProgressOnTabs = false;
        tab.OnSettingsChanged();
        Assert.False(tab.ShowTaskProgressBadge);
        Assert.Equal(tab.ModelBadge, tab.RowDetail);
        h.Services.Settings.Appearance.ShowTaskProgressOnTabs = true;
        tab.OnSettingsChanged();

        // Once the turn ends the row says the model again; the badge stays while tasks are left.
        h.Transport.Emit(Wire.TurnResult());
        await TabTestHarness.Eventually(() => !tab.IsWorking, "the turn's end");
        Assert.Equal(tab.ModelBadge, tab.RowDetail);
        Assert.True(tab.ShowTaskProgressBadge);

        // The badge opens the Tasks page on the tab.
        tab.ShowTasksFromRowCommand.Execute(null);
        Assert.Same(tab, h.Shell.SelectedTab);
        Assert.True(tab.IsSidePanelOpen);
        Assert.True(tab.IsTasksPage);
    }

    [Fact]
    public async Task A_reply_that_reads_like_the_plan_is_offered_as_one_when_Claude_starts_a_list()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Init());
        h.Transport.Emit(Wire.Text(null, "Here's the plan:\n\n1. Read the build script\n2. Fix the config\n3. Run the tests"));
        Create(h, "c1", "1", "Read the build script");
        await TabTestHarness.Eventually(() => tab.TodoList.HasSuggestion, "the suggestion");

        Assert.StartsWith("Use Claude's reply from ", tab.TodoList.SuggestionText, StringComparison.Ordinal);
        tab.UsePlanSuggestionCommand.Execute(null);
        Assert.False(tab.TodoList.HasSuggestion);
        Assert.Equal(PlanSource.Reply, tab.TodoList.PlanSource);
        Assert.StartsWith("Here's the plan:", tab.TodoList.Plan, StringComparison.Ordinal);
        Assert.True(tab.IsTasksPage);
    }

    [Fact]
    public async Task A_reply_without_a_list_or_older_than_the_plan_isnt_offered()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Init());
        h.Transport.Emit(Wire.Text(null, "I'll fix the config."));
        Create(h, "c1", "1", "Fix the config");
        Update(h, "u1", "1", "completed");
        await TabTestHarness.Eventually(() => tab.TodoList.AllDone, "the first list done");
        Assert.False(tab.TodoList.HasSuggestion);

        // A list in a reply, but a plan chosen since.
        h.Transport.Emit(Wire.Text(null, "Next:\n- one\n- two\n- three"));
        await TabTestHarness.Eventually(() => tab.Items.OfType<AssistantTextItem>().Count() == 2, "the second reply");
        tab.ShowAsPlanCommand.Execute(tab.Items.OfType<AssistantTextItem>().Last());
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Create(h, "c2", "2", "One");
        await TabTestHarness.Eventually(() => tab.TodoList.Items.Count == 2, "the new list");
        Assert.False(tab.TodoList.HasSuggestion);
    }
}
