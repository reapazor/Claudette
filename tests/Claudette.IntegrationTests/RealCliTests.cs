using System.Diagnostics;
using System.Text.Json.Nodes;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Sessions;
using Claudette.IntegrationTests.Support;
using Claudette.MockApi;

namespace Claudette.IntegrationTests;

/// <summary>
/// The real <c>claude</c> against a mock Messages API: real tools, prompts, edits and transcripts, and no tokens
/// (DESIGN.md §15). Every test gets its own <c>CLAUDE_CONFIG_DIR</c>, so the user's <c>~/.claude</c> is never touched.
/// Skipped when Claude Code isn't installed.
/// </summary>
[Trait("Category", "RealCli")]
public sealed class RealCliTests : IAsyncLifetime
{
    private readonly TempFolder _root = new("claudette-realcli");
    private MockAnthropicApi _api = null!;
    private ClaudeSessionFactory? _factory;

    private string Work => _root.Combine("repo");

    private string Config => _root.Combine("config");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Config);
        Process.Start(new ProcessStartInfo("git", ["init", "-q"]) { WorkingDirectory = Work, CreateNoWindow = true })?.WaitForExit(10_000);
        _api = await MockAnthropicApi.StartAsync();
        var located = await new ClaudeLocator(new ProcessLauncher(), TimeProvider.System).LocateAsync(null);
        if (located.IsUsable)
        {
            _factory = new ClaudeSessionFactory(located.Install!.Path, new ProcessLauncher(), TimeProvider.System);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        _root.Dispose();
    }

    [Fact]
    public async Task A_turn_round_trips_through_the_real_cli()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("pong", done.Result.Result);
        Assert.Contains(seen, e => e is TextDelta);
        Assert.Contains("interrupt_receipt_v1", session.Capabilities);
        Assert.Contains(_api.Requests, r => r.LastUserText.Contains("hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_allowed_write_creates_the_file()
    {
        await using var session = await StartAsync();
        var file = Path.Combine(Work, "written.txt");

        await session.SendUserMessageAsync($"WRITE_FILE {file}", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Allow();
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("Write", requested.Request.ToolName);
        Assert.Equal("Done with the tool.", done.Result.Result);
        Assert.Equal("hello from mock\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        var result = seen.OfType<ToolResultsReceived>().Single().Message.ToolUseResult!;
        Assert.Equal("create", result["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_denied_write_leaves_the_file_alone()
    {
        await using var session = await StartAsync();
        var file = Path.Combine(Work, "denied.txt");

        await session.SendUserMessageAsync($"WRITE_FILE {file}", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Deny("Not now");
        await session.ReadUntilAsync<TurnCompleted>();

        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Always_allow_saves_the_rule_to_project_settings()
    {
        await using var session = await StartAsync();
        var rule = new JsonArray(new JsonObject
        {
            ["type"] = "addRules",
            ["rules"] = new JsonArray(new JsonObject { ["toolName"] = "Bash", ["ruleContent"] = "touch:*" }),
            ["behavior"] = "allow",
            ["destination"] = "localSettings",
        });

        await session.SendUserMessageAsync("RUN_BASH touch first.txt", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        Assert.Contains(requested.Request.Suggestions, s => s["type"]?.GetValue<string>() == "addRules");
        requested.Request.Allow(updatedPermissions: rule);
        await session.ReadUntilAsync<TurnCompleted>();

        var settings = await File.ReadAllTextAsync(Path.Combine(Work, ".claude", "settings.local.json"), TestContext.Current.CancellationToken);
        Assert.Contains("Bash(touch:*)", settings, StringComparison.Ordinal);

        await session.SendUserMessageAsync("RUN_BASH touch second.txt", TestContext.Current.CancellationToken);
        var (_, seen) = await session.ReadUntilAsync<TurnCompleted>();
        Assert.DoesNotContain(seen, e => e is PermissionRequested);
    }

    [Fact]
    public async Task A_clarifying_question_is_answered_through_the_permission_reply()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("ASK_QUESTION", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        Assert.Equal("AskUserQuestion", requested.Request.ToolName);
        var input = (JsonObject)requested.Request.Input.DeepClone();
        input["answers"] = new JsonObject { ["Which database?"] = "SQLite" };
        requested.Request.Allow(input);
        await session.ReadUntilAsync<TurnCompleted>();

        // The answer reaches the model as the tool's result (DESIGN.md §7).
        Assert.Contains(_api.Requests, r => r.LastToolResultText.Contains("SQLite", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Approving_a_plan_leaves_plan_mode()
    {
        await using var session = await StartAsync(permissionMode: "plan");

        await session.SendUserMessageAsync("EXIT_PLAN", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        Assert.Equal("ExitPlanMode", requested.Request.ToolName);
        Assert.Equal("1. Read the code\n2. Fix the bug", requested.Request.Input["plan"]?.GetValue<string>());
        requested.Request.AllowAndSetMode("acceptEdits");
        await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("acceptEdits", session.PermissionMode);
    }

    [Fact]
    public async Task An_edit_reports_the_original_file()
    {
        await using var session = await StartAsync(permissionMode: "acceptEdits");
        var file = Path.Combine(Work, "edit.txt");
        await File.WriteAllTextAsync(file, "first line\nORIGINAL LINE\nlast line\n", TestContext.Current.CancellationToken);

        await session.SendUserMessageAsync($"EDIT_FILE {file}", TestContext.Current.CancellationToken);
        var (_, seen) = await session.ReadUntilAsync<TurnCompleted>();

        var edit = seen.OfType<ToolResultsReceived>().Select(r => r.Message.ToolUseResult).Single(r => r?["oldString"] is not null)!;
        Assert.Equal("first line\nORIGINAL LINE\nlast line\n", edit["originalFile"]!.GetValue<string>());
        Assert.Contains("EDITED LINE", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_and_effort_changes_reach_the_api()
    {
        await using var session = await StartAsync();

        await session.SetModelAsync("sonnet", TestContext.Current.CancellationToken);
        await session.SetEffortAsync("low", TestContext.Current.CancellationToken);
        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TurnCompleted>();

        var request = _api.Requests.Last(r => r.Reply == "text");
        Assert.Contains("sonnet", request.Model, StringComparison.Ordinal);
        Assert.Equal("low", request.Effort);
    }

    [Fact]
    public async Task Interrupt_stops_a_streaming_reply()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("SLOW", TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TextDelta>();
        await session.InterruptAsync(TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(15));

        Assert.Equal("aborted_streaming", done.Result.TerminalReason);
    }

    [Fact]
    public async Task Context_usage_is_reported()
    {
        await using var session = await StartAsync();

        var usage = await session.GetContextUsageAsync(TestContext.Current.CancellationToken);

        Assert.True(usage.MaxTokens > 0);
    }

    [Fact]
    public async Task Resuming_from_a_transcript_file_continues_next_to_it()
    {
        string sessionId;
        await using (var first = await StartAsync())
        {
            await first.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
            var (done, _) = await first.ReadUntilAsync<TurnCompleted>();
            sessionId = done.Result.SessionId!;
        }
        var transcript = Directory.EnumerateFiles(Path.Combine(Config, "projects"), $"{sessionId}.jsonl", SearchOption.AllDirectories).Single();
        var library = _root.Combine("library");
        Directory.CreateDirectory(library);
        var copy = Path.Combine(library, "copied.jsonl");
        File.Copy(transcript, copy);

        await using var resumed = await StartAsync(resume: copy);
        await resumed.SendUserMessageAsync("hello again", TestContext.Current.CancellationToken);
        await resumed.ReadUntilAsync<TurnCompleted>();

        Assert.Equal(sessionId, resumed.SessionId);
        Assert.True(File.Exists(Path.Combine(library, $"{sessionId}.jsonl")), "The continued transcript should be written next to the resumed file (DESIGN.md §9).");
    }

    [Fact]
    public async Task Subagent_traffic_and_prompts_name_the_subagent()
    {
        // Nesting is on by default, but a user's settings or environment can limit it; this test needs one level.
        await using var session = await StartAsync(environment: new() { ["CLAUDE_CODE_MAX_SUBAGENT_SPAWN_DEPTH"] = "3" });

        await session.SendUserMessageAsync("SUBAGENTS", TestContext.Current.CancellationToken);
        var (requested, before) = await session.ReadUntilAsync<PermissionRequested>(timeout: TimeSpan.FromSeconds(30));
        requested.Request.Allow();
        var (done, after) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(30));
        var seen = before.Concat(after).ToArray();

        // DESIGN.md §18: task_started ties each subagent's task id to its Agent call, and a permission request from
        // inside a subagent names that task id in agent_id.
        var tasks = seen.OfType<SystemNotice>().Where(n => n.Message.Subtype == "task_started" && n.Message.Raw["task_type"]?.GetValue<string>() == "local_agent")
            .ToDictionary(n => n.Message.Raw["tool_use_id"]!.GetValue<string>(), n => n.Message.Raw["task_id"]!.GetValue<string>());
        var calls = seen.OfType<AssistantMessageReceived>()
            .SelectMany(a => a.Message.Content.OfType<Core.Protocol.ToolUseBlock>().Where(t => t.Name == "Agent").Select(t => (a.Message.ParentToolUseId, t)))
            .ToArray();
        Assert.Equal(3, calls.Length);
        var touch = calls.Single(c => c.t.Input["description"]?.GetValue<string>() == "Touch a marker file").t;
        var deeper = calls.Single(c => c.t.Input["description"]?.GetValue<string>() == "Delegate a deeper look").t;
        Assert.Equal(deeper.Id, calls.Single(c => c.t.Input["description"]?.GetValue<string>() == "Search deeper").ParentToolUseId);
        Assert.Equal(tasks[touch.Id], requested.Request.AgentId);
        Assert.Equal(3, tasks.Count);
        Assert.Contains(seen.OfType<AssistantMessageReceived>(), a => a.Message.ParentToolUseId == touch.Id);
        Assert.True(File.Exists(Path.Combine(Work, "agent-marker.txt")));
        Assert.False(done.Result.IsError);
    }

    [Fact]
    public async Task Stop_task_stops_one_foreground_subagent_and_the_turn_carries_on()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("LONG_AGENT", TestContext.Current.CancellationToken);
        var (started, _) = await session.ReadUntilAsync<SystemNotice>(
            n => n.Message.Subtype == "task_started" && n.Message.Raw["task_type"]?.GetValue<string>() == "local_agent", TimeSpan.FromSeconds(30));
        var taskId = started.Message.Raw["task_id"]!.GetValue<string>();
        var toolUseId = started.Message.Raw["tool_use_id"]!.GetValue<string>();
        Assert.False(started.Message.Raw["is_backgrounded"]?.GetValue<bool>());
        await session.ReadUntilAsync<AssistantMessageReceived>(a => a.Message.ParentToolUseId == toolUseId, TimeSpan.FromSeconds(30));

        await session.StopTaskAsync(taskId, TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(30));

        // DESIGN.md §18: the subagent is stopped, not the turn; its Agent call comes back as an error.
        Assert.Contains(seen.OfType<SystemNotice>(), n => n.Message.Subtype == "task_notification"
            && n.Message.Raw["task_id"]?.GetValue<string>() == taskId && n.Message.Raw["status"]?.GetValue<string>() == "stopped");
        var result = seen.OfType<ToolResultsReceived>().Where(r => r.Message.ParentToolUseId is null)
            .SelectMany(r => r.Message.Content.OfType<Core.Protocol.ToolResultBlock>()).Single(b => b.ToolUseId == toolUseId);
        Assert.True(result.IsError);
        Assert.False(done.Result.IsError);
        Assert.Equal("Done with the tool.", done.Result.Result);
    }

    private async Task<ClaudeSession> StartAsync(string? permissionMode = null, string? resume = null, Dictionary<string, string?>? environment = null)
    {
        Assert.SkipWhen(_factory is null, "Claude Code isn't installed.");
        var overrides = new Dictionary<string, string?>
        {
            ["ANTHROPIC_BASE_URL"] = _api.BaseAddress.ToString().TrimEnd('/'),
            ["ANTHROPIC_API_KEY"] = "sk-ant-mock-000",
            ["ANTHROPIC_AUTH_TOKEN"] = null,
            ["CLAUDE_CODE_OAUTH_TOKEN"] = null,
            ["CLAUDE_CONFIG_DIR"] = Config,
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
        };
        foreach (var (name, value) in environment ?? [])
        {
            overrides[name] = value;
        }
        return await _factory!.StartAsync(new ClaudeLaunchOptions
        {
            WorkingDirectory = Work,
            Model = "claude-haiku-4-5",
            PermissionMode = permissionMode,
            Resume = resume,
            EnvironmentOverrides = overrides,
        }, TestContext.Current.CancellationToken);
    }
}
