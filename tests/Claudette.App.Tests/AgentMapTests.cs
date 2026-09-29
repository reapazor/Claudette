using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>
/// The agent map (DESIGN.md §18): the tree of a tab's subagents, built from the same stream as the conversation's
/// subagent groups. The lines below follow what Claude Code 2.1.284 sends (see the 07-subagents protocol fixture).
/// </summary>
public class AgentMapTests
{
    [Fact]
    public async Task A_fan_out_becomes_a_tree_with_nesting_and_parallel_siblings()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "Explore the auth code", "Find where tokens are checked.\n\n- `src/auth`", "Explore"));
        h.Transport.Emit(Wire.TaskStarted("task-a", "a1"));
        h.Transport.Emit(Wire.Agent("a2", "Check the tests", "Run the auth tests", "general-purpose"));
        h.Transport.Emit(Wire.TaskStarted("task-b", "a2"));
        h.Transport.Emit(Wire.Tool("a1", "g1", "Grep", new JsonObject { ["pattern"] = "auth", ["path"] = "src/" }));
        h.Transport.Emit(Wire.Agent("a3", "Look deeper", "Read the middleware", "Explore", parent: "a2"));
        h.Transport.Emit(Wire.TaskStarted("task-c", "a3", depth: 2));
        h.Transport.Emit(Wire.Text("a3", "Reading the middleware now.\nIt's long."));

        await TabTestHarness.Eventually(() => tab.Agents.Find("a3")?.Activity is not null, "the nested subagent's text");
        var map = tab.Agents;
        Assert.Equal(["a1", "a2"], map.Root.Children.Select(n => n.ToolUseId));
        var explore = map.Find("a1")!;
        Assert.Equal("Explore the auth code", explore.Title);
        Assert.Equal("Explore", explore.AgentType);
        Assert.Equal("Find where tokens are checked.\n\n- `src/auth`", explore.Prompt);
        Assert.Equal("Grep auth  in src/", explore.Activity);
        Assert.Equal(1, explore.ToolCalls);
        Assert.Equal("task-a", explore.TaskId);
        Assert.Equal(AgentStatus.Running, explore.Status);
        var nested = Assert.Single(map.Find("a2")!.Children);
        Assert.Equal("a3", nested.ToolUseId);
        Assert.Equal("Reading the middleware now.", nested.ActivityLine);
        Assert.Same(nested, map.FindByTask("task-c"));
        Assert.Equal(AgentStatus.Running, map.Root.Status);
        Assert.True(tab.HasAgents);
        Assert.True(tab.HasActiveAgents);
    }

    [Fact]
    public async Task A_recorded_fan_out_from_the_real_cli_builds_the_map()
    {
        // SUBAGENTS against the mock Messages API, recorded from Claude Code 2.1.284: "Touch a marker file" asks for
        // permission from inside the subagent while "Delegate a deeper look" starts a nested Explore subagent.
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var lines = RecordedOutput("07-subagents");
        var prompt = lines.FindIndex(l => l.Contains("\"can_use_tool\"", StringComparison.Ordinal));

        foreach (var line in lines.Take(prompt + 1))
        {
            h.Transport.Emit(line);
        }
        await TabTestHarness.Eventually(() => tab.Agents.Find("toolu_mock_1")?.IsWaiting == true, "the permission prompt");
        Assert.Equal("toolu_mock_1(), toolu_mock_2(toolu_mock_4())", MapTree(tab.Agents.Root));
        Assert.Equal("Needs your permission: Bash touch agent-marker.txt", tab.Agents.Find("toolu_mock_1")!.ActivityLine);
        Assert.Equal("3 agents running (1 waiting on you)", tab.Agents.Summary);
        tab.Items.OfType<PermissionItem>().Single().AllowCommand.Execute(null);

        foreach (var line in lines.Skip(prompt + 1))
        {
            h.Transport.Emit(line);
        }
        await TabTestHarness.Eventually(() => tab.Items.OfType<TurnSummaryItem>().Any(), "the end of the turn");
        Assert.Equal(GroupTree(tab.Items), MapTree(tab.Agents.Root));
        var touch = tab.Agents.Find("toolu_mock_1")!;
        var deeper = tab.Agents.Find("toolu_mock_2")!;
        var nested = tab.Agents.Find("toolu_mock_4")!;
        Assert.All([touch, deeper, nested], n => Assert.Equal(AgentStatus.Done, n.Status));
        Assert.Equal("Touch a marker file", touch.Title);
        Assert.Equal("Create the marker file.\n\nRUN_BASH touch agent-marker.txt", touch.Prompt);
        Assert.Equal("Done with the tool.", touch.ResultText);
        Assert.Equal(1, touch.ToolCalls);
        Assert.Equal(1020, touch.Tokens);
        Assert.Equal("Explore", nested.AgentType);
        Assert.Equal("Found 3 matches in src/.", nested.ResultText);
        Assert.Equal("Found 3 matches in src/.", nested.Item!.ResultSummary);
        Assert.Equal("Done with the tool.", deeper.ResultText);
        Assert.Equal("3 agents: 3 done", tab.Agents.Summary);
        Assert.Equal(AgentStatus.Idle, tab.Agents.Root.Status);
        Assert.False(tab.Agents.IsTicking);
    }

    /// <summary>What Claude Code wrote in a recorded fixture, less the <c>initialize</c> reply the harness gives itself.</summary>
    private static List<string> RecordedOutput(string name) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "protocol", "2.1.284", $"{name}.jsonl"))
            .Select(l => JsonNode.Parse(l)!.AsObject())
            .Where(e => e["dir"]!.GetValue<string>() == "out" && e["msg"]!["type"]!.GetValue<string>() != "control_response")
            .Select(e => e["msg"]!.ToJsonString())
            .ToList();

    [Fact]
    public async Task The_map_matches_the_conversation_groups()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "One", "p1"));
        h.Transport.Emit(Wire.Agent("a2", "Two", "p2"));
        h.Transport.Emit(Wire.Agent("a3", "Two's child", "p3", parent: "a2"));
        h.Transport.Emit(Wire.Agent("a4", "Grandchild", "p4", parent: "a3"));
        h.Transport.Emit(Wire.Agent("a5", "Two's other child", "p5", parent: "a2"));
        h.Transport.Emit(Wire.Tool("a4", "t1", "Read", new JsonObject { ["file_path"] = "a.cs" }));

        await TabTestHarness.Eventually(() => tab.Agents.Find("a4")?.ToolCalls == 1, "the grandchild's tool call");
        Assert.Equal(GroupTree(tab.Items), MapTree(tab.Agents.Root));
        Assert.Equal("a1(), a2(a3(a4()), a5())", MapTree(tab.Agents.Root));
        // Each node is its conversation group.
        var group = tab.Items.OfType<SubagentItem>().Single(s => s.ToolUseId == "a2").Items.OfType<SubagentItem>().First();
        Assert.Same(group, tab.Agents.Find("a3")!.Item);
    }

    [Fact]
    public async Task A_foreground_subagent_finishes_with_the_report_its_parent_got()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "Search", "Search it", "general-purpose"));
        h.Transport.Emit(Wire.TaskStarted("task-a", "a1"));
        h.Transport.Emit(Wire.Agent("a2", "Search deeper", "Deeper", "Explore", parent: "a1"));
        h.Transport.Emit(Wire.TaskStarted("task-b", "a2", depth: 2));
        h.Transport.Emit(Wire.Text("a2", "Nested done."));
        h.Time.Advance(TimeSpan.FromSeconds(3));
        // A nested result has no tool_use_result: the report is in the framed tool result text.
        h.Transport.Emit(Wire.Notification("task-b", "a2", "completed", "Nested done.", tokens: 1020, toolUses: 0, durationMs: 94));
        h.Transport.Emit(Wire.Result("a2", Wire.HandBack("Nested done.\nSecond line.", "task-b"), parent: "a1"));
        h.Transport.Emit(Wire.Text("a1", "All found."));
        h.Transport.Emit(Wire.Result("a1", Wire.HandBack("All found.", "task-a"), toolUseResult: new JsonObject
        {
            ["status"] = "completed", ["agentId"] = "task-a", ["agentType"] = "general-purpose", ["resolvedModel"] = "claude-haiku-4-5",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "All found.\n\n| file | hits |\n|---|---|\n| a.cs | 3 |" }),
            ["totalDurationMs"] = 3100, ["totalTokens"] = 4321, ["totalToolUseCount"] = 5,
        }));

        await TabTestHarness.Eventually(() => tab.Agents.Find("a1")?.IsDone == true, "the subagent to finish");
        var top = tab.Agents.Find("a1")!;
        Assert.Equal("All found.\n\n| file | hits |\n|---|---|\n| a.cs | 3 |", top.ResultText);
        Assert.Equal(top.ResultText, top.ResultMarkdown.ToString());
        Assert.Equal(4321, top.Tokens);
        Assert.Equal(5, top.ToolCalls);
        Assert.Equal("3s", top.RunningText);
        Assert.Equal("claude-haiku-4-5", top.Model);
        // The model as the tab names it.
        Assert.Equal("general-purpose · Haiku · 3s · 5 tool calls · 4.3k tok", top.Meta);
        var nested = tab.Agents.Find("a2")!;
        Assert.Equal(AgentStatus.Done, nested.Status);
        Assert.Equal("Nested done.\nSecond line.", nested.ResultText);
        Assert.Equal(1020, nested.Tokens);
        // The conversation's groups say the same.
        Assert.True(top.Item!.IsComplete);
        Assert.False(top.Item.IsError);
        Assert.Equal("All found.", top.Item.ResultSummary);
        Assert.Equal("Nested done.", nested.Item!.ResultSummary);
        Assert.Equal("2 agents: 2 done", tab.Agents.Summary);
    }

    [Fact]
    public async Task A_permission_prompt_from_a_subagent_marks_it_waiting()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "Touch a file", "Create the marker"));
        h.Transport.Emit(Wire.TaskStarted("task-a", "a1"));
        h.Transport.Emit(Wire.Agent("a2", "Say hi", "Hi"));
        // Claude Code asks before the subagent's tool call reaches the stream; agent_id is the subagent's task id.
        h.Transport.Emit(Wire.CanUseTool("req-1", "b1", "touch marker.txt", agentId: "task-a"));
        h.Transport.Emit(Wire.Tool("a1", "b1", "Bash", new JsonObject { ["command"] = "touch marker.txt" }));

        await TabTestHarness.Eventually(() => tab.Agents.Find("a1")?.IsWaiting == true, "the subagent to wait");
        var node = tab.Agents.Find("a1")!;
        var prompt = Assert.IsType<PermissionItem>(node.WaitingPrompt);
        Assert.Equal("Needs your permission: Bash touch marker.txt", node.ActivityLine);
        Assert.Equal("general-purpose subagent: Touch a file", prompt.Asker);
        Assert.Equal(AgentStatus.Running, tab.Agents.Root.Status);
        Assert.Contains(tab.InfoRows, r => r is { Label: "Agents", Value: "2 agents running (1 waiting on you)" });

        // Clicking it goes to the prompt.
        ConversationItem? scrolledTo = null;
        tab.ScrollToRequested += item => scrolledTo = item;
        tab.ShowAgentCommand.Execute(node);
        Assert.Same(prompt, scrolledTo);
        Assert.Same(node, tab.SelectedAgent);

        prompt.AllowCommand.Execute(null);
        await TabTestHarness.Eventually(() => node.IsRunning, "the subagent to carry on");
        Assert.Contains(tab.InfoRows, r => r is { Label: "Agents", Value: "2 agents running" });
    }

    [Fact]
    public async Task A_prompt_from_a_subagent_not_known_yet_waits_for_its_task()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "Late", "p"));
        h.Transport.Emit(Wire.CanUseTool("req-1", "b1", "touch x", agentId: "task-a"));

        await TabTestHarness.Eventually(() => tab.Items.OfType<PermissionItem>().Any(), "the prompt");
        Assert.False(tab.Agents.Root.IsWaiting);
        Assert.False(tab.Agents.Find("a1")!.IsWaiting);

        h.Transport.Emit(Wire.TaskStarted("task-a", "a1"));
        await TabTestHarness.Eventually(() => tab.Agents.Find("a1")!.IsWaiting, "the prompt to find its subagent");
    }

    [Fact]
    public async Task A_task_started_before_its_agent_call_still_finds_it()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.TaskStarted("task-a", "a1"));
        h.Transport.Emit(Wire.Agent("a1", "Early task", "p"));

        await TabTestHarness.Eventually(() => tab.Agents.Find("a1") is not null, "the subagent");
        Assert.Equal("task-a", tab.Agents.Find("a1")!.TaskId);
        Assert.Equal("Stop subagent", tab.Agents.Find("a1")!.StopText);
    }

    [Fact]
    public async Task Failed_and_stopped_subagents()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("fail", "Breaks", "p"));
        h.Transport.Emit(Wire.Agent("stop", "Stopped by the user", "p"));
        h.Transport.Emit(Wire.TaskStarted("task-s", "stop"));
        h.Transport.Emit(Wire.Agent("child", "Its child", "p", parent: "stop"));
        h.Transport.Emit(Wire.TaskStarted("task-c", "child", depth: 2));
        h.Transport.Emit(Wire.Agent("cut", "Cut off by the turn's end", "p"));
        h.Transport.Emit(Wire.Result("fail", "Agent type 'nope' not found", isError: true));
        // Stopping a subagent: its notification comes first, then its children's rejected calls, then its own.
        h.Transport.Emit(Wire.Updated("task-s", "killed"));
        h.Transport.Emit(Wire.Notification("task-s", "stop", "stopped", "Stopped by the user"));
        h.Transport.Emit(Wire.Result("child", "The user doesn't want to proceed with this tool use.", isError: true, parent: "stop", toolUseResult: JsonValue.Create("User rejected tool use")));
        h.Transport.Emit(Wire.Notification("task-c", "child", "stopped", "Its child"));
        h.Transport.Emit(Wire.Result("stop", "[Request interrupted by user for tool use]", isError: true, interrupted: true));
        h.Transport.Emit(Wire.TurnResult());

        await TabTestHarness.Eventually(() => tab.Items.OfType<TurnSummaryItem>().Any(), "the turn to end");
        var failed = tab.Agents.Find("fail")!;
        Assert.Equal(AgentStatus.Failed, failed.Status);
        Assert.Equal("Agent type 'nope' not found", failed.ResultText);
        Assert.Equal("Failed: Agent type 'nope' not found", failed.ActivityLine);
        Assert.True(failed.Item!.IsError);
        Assert.Equal(AgentStatus.Stopped, tab.Agents.Find("stop")!.Status);
        Assert.Equal(AgentStatus.Stopped, tab.Agents.Find("child")!.Status);
        Assert.False(tab.Agents.Find("child")!.Item!.IsError);
        Assert.Equal("Stopped", tab.Agents.Find("stop")!.Item!.ResultSummary);
        // A foreground subagent can't outlive its turn.
        Assert.Equal(AgentStatus.Stopped, tab.Agents.Find("cut")!.Status);
        Assert.Equal(AgentStatus.Idle, tab.Agents.Root.Status);
        Assert.Equal("4 agents: 1 failed, 3 stopped", tab.Agents.Summary);
        Assert.False(tab.HasActiveAgents);
    }

    [Fact]
    public async Task A_background_subagent_keeps_running_after_the_turn()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("bg", "Long job", "RUN_BASH sleep 20", background: true));
        h.Transport.Emit(Wire.TaskStarted("task-bg", "bg", backgrounded: true));
        h.Transport.Emit(Wire.Result("bg", "Async agent launched successfully.", toolUseResult: new JsonObject
        {
            ["isAsync"] = true, ["status"] = "async_launched", ["agentId"] = "task-bg", ["description"] = "Long job", ["prompt"] = "RUN_BASH sleep 20",
        }));
        h.Transport.Emit(Wire.TurnResult());

        await TabTestHarness.Eventually(() => tab.Items.OfType<TurnSummaryItem>().Any(), "the turn to end");
        var node = tab.Agents.Find("bg")!;
        Assert.True(node.IsRunning);
        Assert.True(node.IsBackground);
        Assert.Equal("Running in the background", node.StatusText);
        Assert.False(node.Item!.IsComplete);
        Assert.Equal("Running in the background", node.Item.ResultSummary);

        h.Transport.Emit(Wire.Tool("bg", "b1", "Bash", new JsonObject { ["command"] = "sleep 20" }));
        h.Transport.Emit(Wire.Text("bg", "Slept well."));
        h.Transport.Emit(Wire.Notification("task-bg", "bg", "completed", "Slept well.", tokens: 1200, toolUses: 1, durationMs: 20500));

        await TabTestHarness.Eventually(() => node.IsDone, "the notification");
        Assert.Equal("Slept well.", node.ResultText);
        Assert.Equal("20s", node.RunningText);
        Assert.True(node.Item.IsComplete);
        Assert.Equal("Slept well.", node.Item.ResultSummary);
    }

    [Fact]
    public async Task Running_time_ticks_from_the_injected_clock()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "Slow", "p"));
        await TabTestHarness.Eventually(() => tab.Agents.Find("a1") is not null, "the subagent");
        var node = tab.Agents.Find("a1")!;
        var ticks = 0;
        node.PropertyChanged += (_, e) => ticks += e.PropertyName == nameof(AgentNode.RunningText) ? 1 : 0;

        h.Time.Advance(TimeSpan.FromSeconds(65));

        await TabTestHarness.Eventually(() => ticks > 0, "a tick");
        Assert.Equal("1m 05s", node.RunningText);

        h.Transport.Emit(Wire.Result("a1", "done"));
        await TabTestHarness.Eventually(() => node.IsDone, "the result");
        h.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("1m 05s", node.RunningText);
    }

    [Fact]
    public async Task Stop_stops_one_subagent_through_Claude_Code()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "Long job", "p"));
        h.Transport.Emit(Wire.TaskStarted("task-a", "a1"));
        await TabTestHarness.Eventually(() => tab.Agents.Find("a1")?.TaskId is not null, "the task id");
        var node = tab.Agents.Find("a1")!;
        Assert.True(node.CanStop);
        Assert.Equal("Stop subagent", node.StopText);

        tab.StopAgentCommand.Execute(node);
        var confirmation = Assert.IsType<ConfirmationViewModel>(h.Shell.Confirmation);
        Assert.Equal("Stop \"Long job\"?", confirmation.Title);
        await confirmation.ConfirmCommand.ExecuteAsync(null);

        await TabTestHarness.Eventually(() => h.Transport.SentControlSubtypes.Contains("stop_task"), "stop_task");
        var sent = h.Transport.Sent.Last(m => m["request"]?["subtype"]?.GetValue<string>() == "stop_task");
        Assert.Equal("task-a", sent["request"]!["task_id"]!.GetValue<string>());
        Assert.DoesNotContain("interrupt", h.Transport.SentControlSubtypes);
    }

    [Fact]
    public async Task Without_a_task_id_stop_falls_back_to_the_whole_turn()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.ComposerText = "go";
        await tab.SendCommand.ExecuteAsync(null);
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "Old-style", "p"));
        await TabTestHarness.Eventually(() => tab.Agents.Find("a1") is not null && tab.IsWorking, "the subagent");
        var node = tab.Agents.Find("a1")!;
        Assert.Equal("Stop turn", node.StopText);

        tab.StopAgentCommand.Execute(node);
        await h.Shell.Confirmation!.ConfirmCommand.ExecuteAsync(null);

        await TabTestHarness.Eventually(() => h.Transport.SentControlSubtypes.Contains("interrupt"), "the interrupt");
    }

    [Fact]
    public async Task Clicking_a_node_expands_its_group_and_the_groups_around_it()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "Outer", "p"));
        h.Transport.Emit(Wire.Agent("a2", "Inner", "p", parent: "a1"));
        await TabTestHarness.Eventually(() => tab.Agents.Find("a2") is not null, "the nested subagent");
        ConversationItem? scrolledTo = null;
        tab.ScrollToRequested += item => scrolledTo = item;
        var inner = tab.Agents.Find("a2")!;

        tab.ShowAgentCommand.Execute(inner);

        Assert.Same(inner.Item, scrolledTo);
        Assert.True(inner.Item!.IsExpanded);
        Assert.True(tab.Agents.Find("a1")!.Item!.IsExpanded);
        Assert.Same(inner, tab.SelectedAgent);
    }

    [Fact]
    public async Task The_agents_page_is_a_side_panel_page()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        Assert.True(tab.IsFilesPage);
        Assert.Equal("Claude hasn't started any subagents in this tab.", tab.AgentsHeader);

        tab.ShowAgentsPageCommand.Execute(null);
        Assert.True(tab.IsAgentsPage);
        Assert.False(tab.IsFilesPage);

        tab.ShowProcessesPageCommand.Execute(null);
        Assert.False(tab.IsAgentsPage);
        tab.ShowAgentsPageCommand.Execute(null);
        Assert.False(tab.IsProcessesPage);

        tab.ShowFilesPageCommand.Execute(null);
        Assert.True(tab.IsFilesPage);
        Assert.False(tab.IsAgentsPage);
    }

    [Fact]
    public async Task Retries_and_heartbeats_from_tool_progress()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "Flaky", "p"));
        h.Transport.Emit("""{"type":"tool_progress","tool_use_id":"a1","tool_name":"Agent","parent_tool_use_id":"a1","elapsed_time_seconds":4,"subagent_type":"general-purpose","subagent_retry":{"agent_id":"t","attempt":2,"max_retries":10,"retry_delay_ms":4000,"error_status":529,"error_category":"overloaded"}}""");

        await TabTestHarness.Eventually(() => tab.Agents.Find("a1")?.RetryText is not null, "the retry");
        var node = tab.Agents.Find("a1")!;
        Assert.Equal("Retrying after the API being overloaded (attempt 2 of 10)…", node.ActivityLine);

        h.Transport.Emit("""{"type":"tool_progress","tool_use_id":"a1-heartbeat-0","tool_name":"Agent","parent_tool_use_id":"a1","elapsed_time_seconds":30,"heartbeat":true}""");
        h.Transport.Emit("""{"type":"tool_progress","tool_use_id":"a1","tool_name":"Agent","parent_tool_use_id":"a1","elapsed_time_seconds":31,"subagent_type":"general-purpose"}""");
        await TabTestHarness.Eventually(() => node.RetryText is null, "the retry to clear");
    }

    [Fact]
    public async Task Clearing_the_conversation_clears_the_map()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Transport.Emit(Wire.Init());
        h.Transport.Emit(Wire.Agent("a1", "One", "p"));
        await TabTestHarness.Eventually(() => tab.HasAgents, "the subagent");
        tab.SelectedAgent = tab.Agents.Find("a1");

        h.Transport.Emit("""{"type":"conversation_reset","trigger":"clear"}""");

        await TabTestHarness.Eventually(() => !tab.HasAgents, "the map to clear");
        Assert.Null(tab.SelectedAgent);
        Assert.Empty(tab.Agents.Root.Children);
        Assert.DoesNotContain(tab.InfoRows, r => r.Label == "Agents");
    }

    [Fact]
    public async Task A_restored_tab_shows_the_finished_tree_without_live_status()
    {
        await using var h = new TabTestHarness();
        var path = h.WriteTranscript("s1",
            Wire.Entry("user", "2026-09-28T10:00:00Z", new JsonObject { ["role"] = "user", ["content"] = "Look around" }),
            Wire.Entry("assistant", "2026-09-28T10:00:01Z", Wire.Message(Wire.AgentBlock("a1", "Explore it", "Explore the repo", "Explore"), Wire.AgentBlock("bg", "In the background", "Wait", "general-purpose", background: true))),
            Wire.Entry("user", "2026-09-28T10:00:02Z", Wire.ResultMessage("bg", "Async agent launched successfully."), new JsonObject { ["status"] = "async_launched", ["agentId"] = "task-bg" }),
            Wire.Entry("user", "2026-09-28T10:00:09Z", Wire.ResultMessage("a1", "framed"), new JsonObject
            {
                ["status"] = "completed", ["agentId"] = "task-a", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Found it." }),
                ["totalDurationMs"] = 8000, ["totalTokens"] = 2000, ["totalToolUseCount"] = 2,
            }),
            Wire.Entry("user", "2026-09-28T10:00:20Z", new JsonObject
            {
                ["role"] = "user",
                ["content"] = "<task-notification>\n<task-id>task-bg</task-id>\n<tool-use-id>bg</tool-use-id>\n<status>completed</status>\n<summary>Agent \"In the background\" finished</summary>\n<result>Waited.</result>\n<usage><subagent_tokens>900</subagent_tokens><tool_uses>0</tool_uses><duration_ms>18000</duration_ms></usage>\n</task-notification>",
            }),
            Wire.Entry("assistant", "2026-09-28T10:00:21Z", Wire.Message(Wire.AgentBlock("cut", "Never finished", "p", "general-purpose"))));
        var subagents = Path.Combine(Path.GetDirectoryName(path)!, "s1", "subagents");
        Directory.CreateDirectory(subagents);
        File.WriteAllText(Path.Combine(subagents, "agent-task-a.meta.json"), """{"agentType":"Explore","description":"Explore it","toolUseId":"a1","spawnDepth":1}""");
        File.WriteAllLines(Path.Combine(subagents, "agent-task-a.jsonl"),
        [
            Wire.Entry("assistant", "2026-09-28T10:00:03Z", Wire.Message(new JsonObject { ["type"] = "tool_use", ["id"] = "g1", ["name"] = "Grep", ["input"] = new JsonObject { ["pattern"] = "todo" } }), sidechain: true),
            Wire.Entry("assistant", "2026-09-28T10:00:04Z", Wire.Message(Wire.AgentBlock("a2", "Deeper", "Go deeper", "Explore")), sidechain: true),
            Wire.Entry("user", "2026-09-28T10:00:06Z", Wire.ResultMessage("a2", Wire.HandBack("Deep answer.", "task-b")), sidechain: true),
        ]);
        File.WriteAllText(Path.Combine(subagents, "agent-task-b.meta.json"), """{"agentType":"Explore","description":"Deeper","toolUseId":"a2","parentAgentId":"task-a","spawnDepth":2}""");
        File.WriteAllLines(Path.Combine(subagents, "agent-task-b.jsonl"),
            [Wire.Entry("assistant", "2026-09-28T10:00:05Z", Wire.Message(new JsonObject { ["type"] = "text", ["text"] = "Deep answer." }), sidechain: true)]);
        h.Services.State.Tabs = [new TabState { Folder = h.WorkFolder, IsPinned = true, SessionId = "s1" }];
        h.Shell.Restore(null);
        var tab = h.Shell.AllTabs.Single();

        await TabTestHarness.Eventually(() => tab.Status == TabStatus.Idle && tab.Agents.HasSubagents, "the restored conversation");
        Assert.Equal("a1(a2()), bg(), cut()", MapTree(tab.Agents.Root));
        Assert.Equal(GroupTree(tab.Items), MapTree(tab.Agents.Root));
        var explore = tab.Agents.Find("a1")!;
        Assert.False(explore.IsLive);
        Assert.Equal(AgentStatus.Done, explore.Status);
        Assert.Equal("Found it.", explore.ResultText);
        Assert.Equal("8s", explore.RunningText);
        Assert.Equal(2, explore.ToolCalls);
        Assert.False(explore.CanStop);
        // The nested one is in its parent's transcript, and its activity in its own.
        var nested = tab.Agents.Find("a2")!;
        Assert.Equal(AgentStatus.Done, nested.Status);
        Assert.Equal("Deep answer.", nested.ResultText);
        Assert.Equal("Deep answer.", Assert.IsType<AssistantTextItem>(Assert.Single(nested.Item!.Items)).Text);
        Assert.Equal("Grep todo", explore.Item!.Items.OfType<ToolUseItem>().Select(t => $"{t.Name} {t.Summary}").First());
        var background = tab.Agents.Find("bg")!;
        Assert.Equal(AgentStatus.Done, background.Status);
        Assert.Equal("Waited.", background.ResultText);
        Assert.Equal(900, background.Tokens);
        Assert.Equal("Didn't finish", tab.Agents.Find("cut")!.StatusText);
        Assert.False(tab.Agents.IsTicking);
        // The notification isn't shown as something the user typed.
        Assert.Equal(["Look around"], tab.Items.OfType<UserMessageItem>().Select(u => u.Text));
    }

    /// <summary>The conversation's subagent groups, as <c>id(children), …</c>.</summary>
    internal static string GroupTree(IEnumerable<ConversationItem> items) =>
        string.Join(", ", items.OfType<SubagentItem>().Select(s => $"{s.ToolUseId}({GroupTree(s.Items)})"));

    /// <summary>The agent map below <paramref name="node"/>, in the same form.</summary>
    internal static string MapTree(AgentNode node) =>
        string.Join(", ", node.Children.Select(c => $"{c.ToolUseId}({MapTree(c)})"));
}

/// <summary>Stream-json lines in the shape Claude Code 2.1.284 sends them.</summary>
internal static class Wire
{
    public static string Init() =>
        """{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default"}""";

    public static JsonObject AgentBlock(string id, string description, string prompt, string? type = null, bool background = false)
    {
        var input = new JsonObject { ["description"] = description, ["prompt"] = prompt };
        if (type is not null)
        {
            input["subagent_type"] = type;
        }
        if (background)
        {
            input["run_in_background"] = true;
        }
        return new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "Agent", ["input"] = input };
    }

    public static JsonObject Message(params JsonObject[] blocks) =>
        new() { ["role"] = "assistant", ["content"] = new JsonArray(blocks.Select(b => (JsonNode?)b).ToArray()) };

    public static string Agent(string id, string description, string prompt, string type = "general-purpose", string? parent = null, bool background = false) =>
        Assistant(parent, AgentBlock(id, description, prompt, type, background));

    public static string Tool(string? parent, string id, string name, JsonObject input) =>
        Assistant(parent, new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input });

    public static string Text(string? parent, string text) =>
        Assistant(parent, new JsonObject { ["type"] = "text", ["text"] = text });

    private static string Assistant(string? parent, JsonObject block) => new JsonObject
    {
        ["type"] = "assistant",
        ["message"] = Message(block),
        ["parent_tool_use_id"] = parent,
    }.ToJsonString();

    public static string TaskStarted(string taskId, string toolUseId, bool backgrounded = false, int depth = 1) => new JsonObject
    {
        ["type"] = "system", ["subtype"] = "task_started", ["task_id"] = taskId, ["tool_use_id"] = toolUseId,
        ["description"] = "d", ["subagent_type"] = "general-purpose", ["is_backgrounded"] = backgrounded, ["spawn_depth"] = depth, ["task_type"] = "local_agent",
    }.ToJsonString();

    public static string Notification(string taskId, string toolUseId, string status, string summary, long? tokens = null, int? toolUses = null, long? durationMs = null)
    {
        var message = new JsonObject
        {
            ["type"] = "system", ["subtype"] = "task_notification", ["task_id"] = taskId, ["tool_use_id"] = toolUseId,
            ["status"] = status, ["output_file"] = "", ["summary"] = summary,
        };
        if (tokens is not null)
        {
            message["usage"] = new JsonObject { ["total_tokens"] = tokens, ["tool_uses"] = toolUses ?? 0, ["duration_ms"] = durationMs ?? 0 };
        }
        return message.ToJsonString();
    }

    public static string Updated(string taskId, string status) => new JsonObject
    {
        ["type"] = "system", ["subtype"] = "task_updated", ["task_id"] = taskId, ["patch"] = new JsonObject { ["status"] = status },
    }.ToJsonString();

    public static JsonObject ResultMessage(string toolUseId, string text, bool isError = false) => new()
    {
        ["role"] = "user",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolUseId, ["content"] = text, ["is_error"] = isError }),
    };

    public static string Result(string toolUseId, string text, bool isError = false, string? parent = null, JsonNode? toolUseResult = null, bool interrupted = false)
    {
        var message = new JsonObject
        {
            ["type"] = "user",
            ["message"] = ResultMessage(toolUseId, text, isError),
            ["parent_tool_use_id"] = parent,
        };
        if (toolUseResult is not null)
        {
            message["tool_use_result"] = toolUseResult;
        }
        if (interrupted)
        {
            message["tool_result_meta"] = new JsonArray(new JsonObject { ["id"] = toolUseId, ["non_execution_kind"] = "interrupted" });
        }
        return message.ToJsonString();
    }

    /// <summary>The frame Claude Code puts around a subagent's report in the tool result text.</summary>
    public static string HandBack(string report, string agentId) =>
        "[Subagent hand-back] The text below is the final report of a subagent this session delegated to. The report follows:\n"
        + string.Join('\n', report.Split('\n').Select(l => "  " + l))
        + $"\nagentId: {agentId} (use SendMessage with to: '{agentId}' to continue this agent)\n<usage>subagent_tokens: 1020\ntool_uses: 0\nduration_ms: 95</usage>";

    public static string CanUseTool(string requestId, string toolUseId, string command, string? agentId = null) => new JsonObject
    {
        ["type"] = "control_request",
        ["request_id"] = requestId,
        ["request"] = new JsonObject
        {
            ["subtype"] = "can_use_tool", ["tool_name"] = "Bash", ["input"] = new JsonObject { ["command"] = command },
            ["tool_use_id"] = toolUseId, ["agent_id"] = agentId,
        },
    }.ToJsonString();

    public static string TurnResult() =>
        """{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"s1","duration_ms":1000}""";

    /// <summary>A transcript entry.</summary>
    public static string Entry(string type, string timestamp, JsonObject message, JsonNode? toolUseResult = null, bool sidechain = false)
    {
        var entry = new JsonObject { ["type"] = type, ["timestamp"] = timestamp, ["isSidechain"] = sidechain, ["message"] = message, ["sessionId"] = "s1" };
        if (toolUseResult is not null)
        {
            entry["toolUseResult"] = toolUseResult;
        }
        return entry.ToJsonString();
    }
}
