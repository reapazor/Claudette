using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>
/// Running tasks (DESIGN.md §5, "Running tasks"): the work Claude Code keeps going in the background, counted from its
/// task messages, in the shape the Agent SDK documents and Claude Code 2.1.284 sends them (the 03 and 12 protocol
/// fixtures have a foreground Bash task and foreground subagents).
/// </summary>
public class RunningTasksTests
{
    [Fact]
    public async Task A_backgrounded_Bash_command_counts_until_its_terminal_message()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Tool(null, "b1", "Bash", Bash("npm run dev", "Start the dev server", background: true)));
        h.Transport.Emit(TaskStarted("task-1", "b1", "local_bash", "Start the dev server", backgrounded: true));
        // A slow command in the foreground gets a task too, but it's part of the turn.
        h.Transport.Emit(Wire.Tool(null, "b2", "Bash", Bash("dotnet test", "Run the tests")));
        h.Transport.Emit(TaskStarted("task-2", "b2", "local_bash", "Run the tests", backgrounded: false));

        await TabTestHarness.Eventually(() => tab.Tasks.Find("task-2") is not null, "the tasks");
        var task = Assert.Single(tab.Tasks.Running);
        Assert.Equal("task-1", task.TaskId);
        Assert.Equal(TaskKind.Shell, task.Kind);
        Assert.Equal("IconToolBash", task.IconKey);
        Assert.Equal("Start the dev server", task.Title);
        Assert.Equal("Shell command: Start the dev server\nnpm run dev\nClick to show it in the conversation.", task.Tooltip);
        Assert.Same(tab.Items.OfType<ToolUseItem>().First(t => t.ToolUseId == "b1"), task.Tool);
        Assert.True(tab.HasRunningTasks);
        Assert.Equal(1, tab.RunningTaskCount);
        Assert.Equal("1 running task", tab.RunningTasksText);
        Assert.Contains(tab.InfoRows, r => r is { Label: "Running tasks", Value: "Shell command: Start the dev server" });
        // The process monitor stops either through Claude Code (DESIGN.md §4).
        Assert.Equal("task-1", tab.Tasks.TaskIdFor("b1"));
        Assert.Equal("task-2", tab.Tasks.TaskIdFor("b2"));

        // Moved to the background later, the foreground one counts too; a status that isn't an end changes nothing.
        h.Transport.Emit(Updated("task-2", new JsonObject { ["is_backgrounded"] = true }));
        h.Transport.Emit(Updated("task-1", new JsonObject { ["status"] = "running" }));
        await TabTestHarness.Eventually(() => tab.Tasks.Count == 2, "the second task in the background");
        Assert.Equal(["task-1", "task-2"], tab.Tasks.Running.Select(t => t.TaskId));
        Assert.Equal("2 running tasks", tab.RunningTasksText);
        Assert.Contains(tab.InfoRows, r => r is { Label: "Running tasks", Value: "Shell command: Start the dev server\nShell command: Run the tests" });

        // task_updated ends one, task_notification the other.
        h.Transport.Emit(Wire.Updated("task-1", "completed"));
        await TabTestHarness.Eventually(() => tab.Tasks.Count == 1, "the first to end");
        Assert.Equal("task-2", Assert.Single(tab.Tasks.Running).TaskId);
        h.Transport.Emit(Wire.Notification("task-2", "b2", "stopped", "Run the tests"));
        await TabTestHarness.Eventually(() => tab.Tasks.Count == 0, "the second to end");
        Assert.False(tab.HasRunningTasks);
        Assert.DoesNotContain(tab.InfoRows, r => r.Label == "Running tasks");
    }

    [Fact]
    public async Task A_background_subagent_counts_and_a_foreground_one_does_not()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("fg", "Look around", "p", "Explore"));
        h.Transport.Emit(Wire.TaskStarted("task-fg", "fg"));
        h.Transport.Emit(Wire.Agent("bg", "Long job", "RUN_BASH sleep 20", background: true));
        h.Transport.Emit(TaskStarted("task-bg", "bg", "local_agent", "Long job", backgrounded: true));

        await TabTestHarness.Eventually(() => tab.Tasks.Count == 1, "the background subagent");
        var task = Assert.Single(tab.Tasks.Running);
        Assert.Equal("task-bg", task.TaskId);
        Assert.Equal(TaskKind.Agent, task.Kind);
        Assert.Equal("IconToolAgent", task.IconKey);
        Assert.Equal("Subagent", task.KindText);
        Assert.Equal("Long job", task.Title);
        Assert.Same(tab.Agents.Find("bg"), task.Agent);
        Assert.Same(tab.Agents.Find("bg")!.Item, task.Tool);
        // The foreground one is on the Agents page, as part of the turn.
        Assert.Equal("2 agents running", tab.AgentsButtonText);

        // One Claude Code sends to the background when its call returns async_launched counts from then on.
        h.Transport.Emit(Wire.Agent("later", "Later job", "p"));
        h.Transport.Emit(Wire.TaskStarted("task-later", "later"));
        await TabTestHarness.Eventually(() => tab.Tasks.Find("task-later") is not null, "the third subagent");
        Assert.Single(tab.Tasks.Running);
        h.Transport.Emit(Wire.Result("later", "Async agent launched successfully.", toolUseResult: new JsonObject
        {
            ["isAsync"] = true, ["status"] = "async_launched", ["agentId"] = "task-later", ["description"] = "Later job",
        }));
        await TabTestHarness.Eventually(() => tab.Tasks.Count == 2, "the subagent in the background");

        // The foreground one finishing changes nothing; the background ones end with their notifications, as on the map.
        h.Transport.Emit(Wire.Result("fg", "Found it."));
        h.Transport.Emit(Wire.TurnResult());
        await TabTestHarness.Eventually(() => tab.Agents.Find("fg")!.IsDone, "the foreground subagent");
        Assert.Equal(2, tab.Tasks.Count);
        h.Transport.Emit(Wire.Notification("task-bg", "bg", "completed", "Slept well."));
        h.Transport.Emit(Wire.Notification("task-later", "later", "failed", "It broke."));
        await TabTestHarness.Eventually(() => tab.Tasks.Count == 0, "the notifications");
        Assert.True(tab.Agents.Find("bg")!.IsDone);
    }

    [Fact]
    public async Task A_Monitor_watch_counts_even_when_its_task_comes_before_its_call()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        // Only the call says it's a Monitor: its task is local_bash, like a command's.
        h.Transport.Emit(TaskStarted("task-m", "m1", "local_bash", "Watch the build log"));
        await TabTestHarness.Eventually(() => tab.Tasks.Find("task-m") is not null, "the task");
        Assert.Equal(0, tab.Tasks.Count);

        h.Transport.Emit(Wire.Tool(null, "m1", "Monitor", new JsonObject { ["description"] = "Watch the build log", ["command"] = "tail -f build.log", ["timeout_ms"] = 300000 }));

        await TabTestHarness.Eventually(() => tab.Tasks.Count == 1, "the watch");
        var task = Assert.Single(tab.Tasks.Running);
        Assert.Equal(TaskKind.Monitor, task.Kind);
        Assert.Equal("IconToolMonitor", task.IconKey);
        Assert.Equal("Monitor: Watch the build log\ntail -f build.log\nClick to show it in the conversation.", task.Tooltip);
        Assert.Equal("IconToolMonitor", tab.Items.OfType<ToolUseItem>().Single().IconKey);

        h.Transport.Emit(Wire.Updated("task-m", "killed"));
        await TabTestHarness.Eventually(() => tab.Tasks.Count == 0, "the watch to end");
    }

    [Fact]
    public async Task Remote_and_unknown_tasks_count_but_Claude_Codes_own_ambient_ones_do_not()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(TaskStarted("task-r", null, "remote_agent", "Fix the flaky test in the cloud"));
        h.Transport.Emit(TaskStarted("task-x", null, "something_new", "A kind Claudette doesn't know"));
        h.Transport.Emit(TaskStarted("task-a", null, "local_bash", "Watch settings for changes", backgrounded: true, ambient: true));
        h.Transport.Emit(TaskStarted("task-w", null, "local_workflow", "Review every file", backgrounded: false));

        await TabTestHarness.Eventually(() => tab.Tasks.Find("task-w") is not null, "the tasks");
        Assert.Equal(["task-r", "task-x"], tab.Tasks.Running.Select(t => t.TaskId));
        Assert.Equal([TaskKind.Remote, TaskKind.Unknown], tab.Tasks.Running.Select(t => t.Kind));
        Assert.Equal(["IconToolRemote", "IconToolDefault"], tab.Tasks.Running.Select(t => t.IconKey));
        Assert.Equal("Remote agent: Fix the flaky test in the cloud", tab.Tasks.Running[0].Tooltip);
        // Without a call, there's no card to go to.
        Assert.False(tab.Tasks.Running[0].CanShow);
    }

    [Fact]
    public async Task Stop_sends_stop_task_with_the_task_id()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Tool(null, "b1", "Bash", Bash("npm run dev", "Start the dev server", background: true)));
        h.Transport.Emit(TaskStarted("task-1", "b1", "local_bash", "Start the dev server", backgrounded: true));
        h.Transport.Emit(Wire.Agent("bg", "Long job", "p", background: true));
        h.Transport.Emit(Wire.TaskStarted("task-bg", "bg", backgrounded: true));
        await TabTestHarness.Eventually(() => tab.Tasks.Count == 2, "the tasks");

        tab.StopTaskCommand.Execute(tab.Tasks.Find("task-1"));
        var confirmation = Assert.IsType<ConfirmationViewModel>(h.Shell.Confirmation);
        Assert.Equal("Stop \"Start the dev server\"?", confirmation.Title);
        await confirmation.ConfirmCommand.ExecuteAsync(null);

        await TabTestHarness.Eventually(() => h.Transport.SentControlSubtypes.Contains("stop_task"), "stop_task");
        var sent = h.Transport.Sent.Last(m => m["request"]?["subtype"]?.GetValue<string>() == "stop_task");
        Assert.Equal("task-1", sent["request"]!["task_id"]!.GetValue<string>());
        // It keeps running until Claude Code says it ended.
        Assert.Equal(2, tab.Tasks.Count);

        // A subagent goes through the agent map's own Stop.
        tab.StopTaskCommand.Execute(tab.Tasks.Find("task-bg"));
        confirmation = Assert.IsType<ConfirmationViewModel>(h.Shell.Confirmation);
        Assert.Equal("Stop \"Long job\"?", confirmation.Title);
        await confirmation.ConfirmCommand.ExecuteAsync(null);
        await TabTestHarness.Eventually(() => h.Transport.Sent.Any(m => m["request"]?["task_id"]?.GetValue<string>() == "task-bg"), "stop_task for the subagent");
        Assert.DoesNotContain("interrupt", h.Transport.SentControlSubtypes);
    }

    [Fact]
    public async Task Clicking_a_task_scrolls_to_its_card_inside_the_groups_around_it()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "Build it", "p"));
        h.Transport.Emit(Wire.Tool("a1", "b1", "Bash", Bash("npm run watch", "Watch for changes", background: true)));
        h.Transport.Emit(TaskStarted("task-1", "b1", "local_bash", "Watch for changes", backgrounded: true));
        await TabTestHarness.Eventually(() => tab.Tasks.Count == 1, "the task");
        var group = tab.Agents.Find("a1")!.Item!;
        group.IsExpanded = false;
        ConversationItem? scrolledTo = null;
        tab.ScrollToRequested += item => scrolledTo = item;

        tab.ShowTaskCommand.Execute(tab.Tasks.Running[0]);

        var card = group.Items.OfType<ToolUseItem>().Single();
        Assert.Same(card, scrolledTo);
        Assert.True(group.IsExpanded);
    }

    [Fact]
    public async Task The_count_clears_when_the_session_exits()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Tool(null, "b1", "Bash", Bash("npm run dev", "Start the dev server", background: true)));
        h.Transport.Emit(TaskStarted("task-1", "b1", "local_bash", "Start the dev server", backgrounded: true));
        await TabTestHarness.Eventually(() => tab.Tasks.Count == 1, "the task");

        h.Transport.Exit(1, "boom");

        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Error, "the exit");
        Assert.Equal(0, tab.Tasks.Count);
        Assert.False(tab.HasRunningTasks);
        Assert.False(tab.ShowTaskBadge);
        Assert.Null(tab.Tasks.TaskIdFor("b1"));
    }

    [Fact]
    public async Task Running_time_comes_from_the_injected_clock_and_ticks_while_the_list_is_open()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(TaskStarted("task-1", null, "local_bash", "Start the dev server", backgrounded: true));
        await TabTestHarness.Eventually(() => tab.Tasks.Count == 1, "the task");
        var task = tab.Tasks.Running[0];
        var ticks = 0;
        task.PropertyChanged += (_, e) => ticks += e.PropertyName == nameof(RunningTask.RunningText) ? 1 : 0;
        Assert.Equal("<1s", task.RunningText);

        h.Time.Advance(TimeSpan.FromSeconds(65));

        Assert.Equal("1m 05s", task.RunningText);
        Assert.Equal(0, ticks);

        // Open, the list ticks every second.
        InlineDispatcher.Read(() => tab.IsTaskListOpen = true);
        var opened = ticks;
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await TabTestHarness.Eventually(() => ticks > opened, "a tick");
        Assert.Equal("1m 06s", task.RunningText);

        InlineDispatcher.Read(() => tab.IsTaskListOpen = false);
        var closed = ticks;
        h.Time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(closed, ticks);
        Assert.Equal("1h 00m", task.RunningText);
    }

    [Fact]
    public async Task The_row_badge_shows_only_when_the_turn_is_over_and_tasks_remain()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "start the dev server";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Tool(null, "b1", "Bash", Bash("npm run dev", "Start the dev server", background: true)));
        h.Transport.Emit(TaskStarted("task-1", "b1", "local_bash", "Start the dev server", backgrounded: true));
        await TabTestHarness.Eventually(() => tab.Tasks.Count == 1 && tab.IsWorking, "the task during the turn");

        // While Claude works, the tab already shows it's busy.
        Assert.True(tab.HasRunningTasks);
        Assert.False(tab.ShowTaskBadge);

        h.Transport.Emit(Wire.TurnResult());
        await TabTestHarness.Eventually(() => !tab.IsWorking, "the end of the turn");
        Assert.True(tab.ShowTaskBadge);
        Assert.Equal("1 task still running", tab.TaskBadgeTip);
        h.Transport.Emit(TaskStarted("task-2", null, "remote_agent", "Fix it in the cloud"));
        await TabTestHarness.Eventually(() => tab.RunningTaskCount == 2, "the second task");
        Assert.Equal("2 tasks still running", tab.TaskBadgeTip);

        h.Transport.Emit(Wire.Notification("task-1", "b1", "completed", "done"));
        h.Transport.Emit(Wire.Notification("task-2", "", "completed", "done"));
        await TabTestHarness.Eventually(() => !tab.HasRunningTasks, "the tasks to end");
        Assert.False(tab.ShowTaskBadge);
    }

    private static JsonObject Bash(string command, string description, bool background = false)
    {
        var input = new JsonObject { ["command"] = command, ["description"] = description };
        if (background)
        {
            input["run_in_background"] = true;
        }
        return input;
    }

    private static string TaskStarted(string taskId, string? toolUseId, string type, string description, bool? backgrounded = null, bool? ambient = null)
    {
        var message = new JsonObject
        {
            ["type"] = "system", ["subtype"] = "task_started", ["task_id"] = taskId, ["description"] = description, ["task_type"] = type,
            ["uuid"] = Guid.NewGuid().ToString(), ["session_id"] = "s1",
        };
        if (toolUseId is not null)
        {
            message["tool_use_id"] = toolUseId;
        }
        if (backgrounded is not null)
        {
            message["is_backgrounded"] = backgrounded;
        }
        if (ambient is not null)
        {
            message["ambient"] = ambient;
        }
        return message.ToJsonString();
    }

    private static string Updated(string taskId, JsonObject patch) => new JsonObject
    {
        ["type"] = "system", ["subtype"] = "task_updated", ["task_id"] = taskId, ["patch"] = patch,
    }.ToJsonString();
}
