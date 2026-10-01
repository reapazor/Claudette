using System.Text.Json.Nodes;
using Claudette.Core.Auth;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.IntegrationTests.Support;

namespace Claudette.IntegrationTests;

/// <summary>
/// Process handling against <c>fake-claude</c>: real child processes and pipes, no Claude Code needed (DESIGN.md §15).
/// </summary>
public sealed class FakeClaudeTests : IDisposable
{
    private readonly TempFolder _work = new();
    private readonly ClaudeSessionFactory _factory = new(FakeClaude.Path, new ProcessLauncher(), TimeProvider.System);

    public void Dispose() => _work.Dispose();

    [Fact]
    public async Task Locator_reads_the_version()
    {
        var result = await new ClaudeLocator(new ProcessLauncher(), TimeProvider.System).LocateAsync(FakeClaude.Path, TestContext.Current.CancellationToken);

        Assert.True(result.IsUsable, result.Detail);
        Assert.Equal(new Version(2, 1, 284), result.Install!.Version);
    }

    [Fact]
    public async Task Auth_status_is_read()
    {
        var status = await new ClaudeAuth(FakeClaude.Path, new ProcessLauncher(), TimeProvider.System).GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.LoggedIn);
        Assert.Equal("fake@example.com", status.Email);
    }

    [Fact]
    public async Task A_turn_streams_and_completes()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("pong: hello", done.Result.Result);
        Assert.Equal("pong: hello", string.Concat(seen.OfType<TextDelta>().Select(d => d.Text)));
        Assert.Contains(session.Initialization!.Models, m => m.Value == "fake");
        Assert.Equal("claude-fake-1", session.Model);
    }

    [Fact]
    public async Task A_turn_stopped_at_the_limit_waits_for_the_reset()
    {
        await using var session = await StartAsync();
        var sent = new List<string>();
        using var monitor = new AutoContinueMonitor(TimeProvider.System, () => true, sent.Add, () => { });

        // DESIGN.md §6, "Continuing after a limit resets": the rejection and the failed turn, read off the process.
        await session.SendUserMessageAsync("LIMIT 30", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();
        foreach (var rateLimit in seen.OfType<RateLimitUpdated>())
        {
            monitor.RateLimit(rateLimit.Message.Info);
        }
        monitor.TurnEnded(done.Result);

        Assert.True(done.Result.IsError);
        var wait = Assert.IsType<LimitWait>(monitor.Wait);
        Assert.True(wait.WillContinue);
        Assert.Equal("session limit", wait.LimitName);
        Assert.InRange(wait.ResetsAt - TimeProvider.System.GetUtcNow(), TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        Assert.Empty(sent);
    }

    [Fact]
    public async Task A_message_with_images_is_accepted()
    {
        await using var session = await StartAsync();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

        await session.SendUserMessageAsync("look", [new MessageImage("image/png", png), new MessageImage("image/jpeg", [0xFF, 0xD8, 0xFF])], TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("pong: look [images: image/png, image/jpeg]", done.Result.Result);
        Assert.Contains(session.Initialization!.Commands, c => c.Name == "compact");
    }

    [Fact]
    public async Task Launches_with_the_expected_arguments_and_folder()
    {
        var record = _work.Combine("record.json");
        await using var session = await StartAsync(new Dictionary<string, string?> { ["FAKE_CLAUDE_RECORD"] = record });

        var recorded = JsonNode.Parse(await File.ReadAllTextAsync(record, TestContext.Current.CancellationToken))!;
        var args = recorded["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        Assert.Equal(ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions { WorkingDirectory = _work.Path }), args);
        // Compare folders by identity, not by path text: on macOS the temp folder is under /var, a symlink to
        // /private/var, and the child reports the resolved path.
        var marker = $"marker-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(_work.Combine(marker), "", TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(recorded["cwd"]!.GetValue<string>(), marker)), $"Expected the working folder to be {_work.Path}, was {recorded["cwd"]}");
    }

    [Fact]
    public async Task Allowing_a_permission_request_reaches_claude()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("ASK_PERMISSION", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Allow();
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("Bash", requested.Request.ToolName);
        Assert.Equal("permission: allow", done.Result.Result);
    }

    [Fact]
    public async Task Denying_a_permission_request_reaches_claude()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("ASK_PERMISSION", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Deny("no");
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("permission: deny", done.Result.Result);
    }

    [Fact]
    public async Task Subagents_fan_out_with_a_prompt_from_inside_one()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("SUBAGENTS", TestContext.Current.CancellationToken);
        var (requested, before) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Allow();
        var (done, after) = await session.ReadUntilAsync<TurnCompleted>();
        var seen = before.Concat(after).ToArray();

        // The same shape as Claude Code's (DESIGN.md §18): nested traffic tagged with its Agent call, and the prompt
        // naming the subagent's task.
        Assert.Equal("fake_task_1", requested.Request.AgentId);
        var calls = seen.OfType<AssistantMessageReceived>()
            .SelectMany(a => a.Message.Content.OfType<ToolUseBlock>().Where(t => t.Name == "Agent").Select(t => $"{a.Message.ParentToolUseId ?? "main"}>{t.Id}"));
        Assert.Equal(["main>toolu_fake_agent_1", "main>toolu_fake_agent_2", "toolu_fake_agent_2>toolu_fake_agent_3"], calls);
        Assert.Equal(3, seen.OfType<SystemNotice>().Count(n => n.Message.Subtype == "task_notification"));
        Assert.Equal("Both parts are done.", done.Result.Result);
    }

    [Fact]
    public async Task Stop_task_stops_a_subagent_waiting_on_permission()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("SUBAGENTS", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        await session.StopTaskAsync(requested.Request.AgentId!, TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Contains(seen, e => e is PermissionCancelled);
        Assert.Contains(seen.OfType<SystemNotice>(), n => n.Message.Subtype == "task_notification" && n.Message.Raw["status"]?.GetValue<string>() == "stopped");
        Assert.False(done.Result.IsError);
        await Assert.ThrowsAnyAsync<Exception>(() => session.StopTaskAsync("no-such-task", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Interrupt_stops_a_streaming_turn()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("SLOW", TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TextDelta>();
        await session.InterruptAsync(TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(5));

        Assert.Equal("aborted_streaming", done.Result.TerminalReason);
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task A_quiet_turn_answers_a_check_in_sent_mid_turn()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("SILENT", TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TextDelta>();
        await session.SendUserMessageAsync("Everything OK? Give me a one or two sentence status update.", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(10));

        Assert.Equal("status sent", done.Result.Result);
        Assert.Contains(seen.OfType<AssistantMessageReceived>(), a => a.Message.Content.OfType<TextBlock>().Any(t => t.Text.StartsWith("Status:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_hung_turn_ends_only_when_interrupted()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("HANG", TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TurnStarted>();
        await session.InterruptAsync(TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(10));

        Assert.Equal("aborted_streaming", done.Result.TerminalReason);
    }

    [Fact]
    public async Task A_spawned_child_process_outlives_the_turn()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("SPAWN 20", TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(10));

        var pid = int.Parse(done.Result.Result!["started child ".Length..], System.Globalization.CultureInfo.InvariantCulture);
        using var child = System.Diagnostics.Process.GetProcessById(pid);
        try
        {
            Assert.False(child.HasExited);
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public async Task Stopping_doesnt_wait_for_a_child_that_holds_the_output_open()
    {
        // The child inherits fake-claude's standard streams, as a build server or a backgrounded command does.
        var session = await StartAsync();
        await session.SendUserMessageAsync("SPAWN 60", TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(10));
        var pid = int.Parse(done.Result.Result!["started child ".Length..], System.Globalization.CultureInfo.InvariantCulture);
        using var child = System.Diagnostics.Process.GetProcessById(pid);
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await session.StopAsync(TimeSpan.FromSeconds(3));
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Stopping took {watch.Elapsed}.");
            Assert.Equal(SessionState.Exited, session.State);
            Assert.False(child.HasExited);
        }
        finally
        {
            child.Kill();
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_crash_is_reported_with_its_exit_code_and_stderr()
    {
        await using var session = await StartAsync();

        await session.SendUserMessageAsync("CRASH", TestContext.Current.CancellationToken);
        var (exited, _) = await session.ReadUntilAsync<SessionExited>();

        Assert.Equal(7, exited.Exit.ExitCode);
        Assert.Contains("crashed on purpose", exited.Exit.StandardErrorTail, StringComparison.Ordinal);
        Assert.Equal(SessionState.Exited, session.State);
    }

    [Fact]
    public async Task Requests_fail_once_the_process_has_exited()
    {
        await using var session = await StartAsync(new Dictionary<string, string?> { ["FAKE_CLAUDE_EXIT_AFTER_INITIALIZE"] = "1" });
        await session.ReadUntilAsync<SessionExited>();

        await Assert.ThrowsAnyAsync<Exception>(() => session.InterruptAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Stop_ends_the_process()
    {
        var session = await StartAsync();

        await session.StopAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionState.Exited, session.State);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Utility_session_reports_an_unsupported_request_as_an_error()
    {
        await using var utility = await UtilitySession.StartAsync(_factory, _work.Combine("utility"), TestContext.Current.CancellationToken);

        Assert.NotEmpty(utility.Initialization.Models);
        await Assert.ThrowsAsync<ControlRequestException>(() => utility.GetUsageAsync(TestContext.Current.CancellationToken));
    }

    private Task<ClaudeSession> StartAsync(IReadOnlyDictionary<string, string?>? environment = null) =>
        _factory.StartAsync(new ClaudeLaunchOptions
        {
            WorkingDirectory = _work.Path,
            EnvironmentOverrides = environment ?? new Dictionary<string, string?>(),
        }, TestContext.Current.CancellationToken);
}

/// <summary>Changes this process's environment, so it doesn't run in parallel with other tests.</summary>
[CollectionDefinition(nameof(ProcessEnvironmentCollection), DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class CleanEnvironmentTests
{
    [Fact]
    public async Task Session_variables_from_a_parent_claude_are_not_passed_on()
    {
        using var work = new TempFolder();
        var record = work.Combine("record.json");
        Environment.SetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION", "1");
        Environment.SetEnvironmentVariable("CLAUDETTE_TEST_MARKER", "kept");
        try
        {
            var factory = new ClaudeSessionFactory(FakeClaude.Path, new ProcessLauncher(), TimeProvider.System);
            await using var session = await factory.StartAsync(new ClaudeLaunchOptions
            {
                WorkingDirectory = work.Path,
                EnvironmentOverrides = new Dictionary<string, string?> { ["FAKE_CLAUDE_RECORD"] = record },
            }, TestContext.Current.CancellationToken);

            var env = JsonNode.Parse(await File.ReadAllTextAsync(record, TestContext.Current.CancellationToken))!["env"]!.AsObject();
            Assert.False(env.ContainsKey("CLAUDE_CODE_CHILD_SESSION"));
            Assert.False(env.ContainsKey("CLAUDECODE"));
            Assert.Equal("kept", env["CLAUDETTE_TEST_MARKER"]!.GetValue<string>());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_CHILD_SESSION", null);
            Environment.SetEnvironmentVariable("CLAUDETTE_TEST_MARKER", null);
        }
    }
}
