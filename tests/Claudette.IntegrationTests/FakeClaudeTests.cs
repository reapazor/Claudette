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
    public async Task Launches_with_the_expected_arguments_and_folder()
    {
        var record = _work.Combine("record.json");
        await using var session = await StartAsync(new Dictionary<string, string?> { ["FAKE_CLAUDE_RECORD"] = record });

        var recorded = JsonNode.Parse(await File.ReadAllTextAsync(record, TestContext.Current.CancellationToken))!;
        var args = recorded["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        Assert.Equal(ClaudeArguments.ForStreamingSession(new ClaudeLaunchOptions { WorkingDirectory = _work.Path }), args);
        Assert.Equal(Path.GetFullPath(_work.Path).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(recorded["cwd"]!.GetValue<string>()).TrimEnd(Path.DirectorySeparatorChar), ignoreCase: OperatingSystem.IsWindows());
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
