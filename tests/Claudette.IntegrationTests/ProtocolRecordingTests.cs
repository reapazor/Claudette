using System.Diagnostics;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Sessions;
using Claudette.Fixtures;
using Claudette.IntegrationTests.Support;
using Claudette.MockApi;

namespace Claudette.IntegrationTests;

/// <summary>
/// The protocol scenarios DESIGN.md §15 lists, run against the real <c>claude</c> and the mock Messages API: a denied
/// permission, an interrupt, <c>/clear</c>, compaction and API retries. Each checks what Claudette sees, and with
/// <c>CLAUDETTE_RECORD_FIXTURES=&lt;folder&gt;</c> set also writes the traffic there as a cleaned protocol fixture
/// ("record mode"), to check in under <c>tests/Claudette.Core.Tests/Fixtures/protocol/&lt;version&gt;/</c>.
/// </summary>
[Trait("Category", "RealCli")]
public sealed class ProtocolRecordingTests : IAsyncLifetime
{
    private readonly TempFolder _root = new("claudette-record");
    private MockAnthropicApi _api = null!;
    private ClaudeSessionFactory? _factory;
    private string? _fixtureName;
    private string? _version;

    private string Work => _root.Combine("repo");

    private string Config => _root.Combine("config");

    private string LogPath => _root.Combine("protocol.log");

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
            _version = located.Install.Version.ToString();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        if (Environment.GetEnvironmentVariable("CLAUDETTE_RECORD_FIXTURES") is { Length: > 0 } folder && _fixtureName is not null && File.Exists(LogPath))
        {
            var lines = new ProtocolFixtureWriter([_root.Path]).FromProtocolLog(await File.ReadAllLinesAsync(LogPath));
            var target = Path.Combine(folder, _version ?? "unknown", $"{_fixtureName}.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllLinesAsync(target, lines);
        }
        _root.Dispose();
    }

    [Fact]
    public async Task A_denied_permission_reaches_claude_as_a_tool_error()
    {
        await using var session = await StartAsync("07-permission-denied");

        await session.SendUserMessageAsync("RUN_BASH touch denied.txt", TestContext.Current.CancellationToken);
        var (requested, _) = await session.ReadUntilAsync<PermissionRequested>();
        requested.Request.Deny("Not in this folder, please.");
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("Bash", requested.Request.ToolName);
        Assert.False(File.Exists(Path.Combine(Work, "denied.txt")));
        var denial = seen.OfType<ToolResultsReceived>().SelectMany(r => r.Message.Content.OfType<Core.Protocol.ToolResultBlock>()).Single();
        Assert.True(denial.IsError);
        Assert.Contains("Not in this folder, please.", _api.Requests.Last(r => r.Reply != "title").LastToolResultText, StringComparison.Ordinal);
        Assert.False(done.Result.IsError);
    }

    [Fact]
    public async Task An_interrupt_ends_a_streaming_turn()
    {
        await using var session = await StartAsync("08-interrupt");

        await session.SendUserMessageAsync("SLOW", TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TextDelta>();
        await session.InterruptAsync(TestContext.Current.CancellationToken);
        var (done, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.True(done.Result.IsError || done.Result.TerminalReason is not null and not "completed");
        Assert.Equal(SessionState.Idle, session.State);
    }

    [Fact]
    public async Task Clear_starts_a_new_conversation_in_the_same_session()
    {
        await using var session = await StartAsync("09-clear");

        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (first, _) = await session.ReadUntilAsync<TurnCompleted>();
        await session.SendUserMessageAsync("/clear", TestContext.Current.CancellationToken);
        var (reset, _) = await session.ReadUntilAsync<ConversationReset>();
        // /clear is a turn of its own, with an empty result.
        await session.ReadUntilAsync<TurnCompleted>();
        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        var (second, _) = await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("pong", second.Result.Result);
        Assert.NotEqual(first.Result.SessionId, second.Result.SessionId);
        Assert.Equal("clear", reset.Trigger);
    }

    [Fact]
    public async Task Compact_summarizes_and_marks_the_boundary()
    {
        await using var session = await StartAsync("10-compact");

        await session.SendUserMessageAsync("hello", TestContext.Current.CancellationToken);
        await session.ReadUntilAsync<TurnCompleted>();
        await session.SendUserMessageAsync("/compact", TestContext.Current.CancellationToken);
        var (boundary, _) = await session.ReadUntilAsync<SystemNotice>(n => n.Message.Subtype == "compact_boundary");
        await session.ReadUntilAsync<TurnCompleted>();

        Assert.Equal("manual", boundary.Message.Raw["compact_metadata"]?["trigger"]?.GetValue<string>());
    }

    [Fact]
    public async Task Api_errors_are_retried_and_reported()
    {
        await using var session = await StartAsync("11-api-retry");

        await session.SendUserMessageAsync("API_ERROR", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromSeconds(120));

        Assert.Equal("pong", done.Result.Result);
        Assert.Contains(seen, e => e is SystemNotice { Message.Subtype: "api_retry" });
        Assert.Equal(2, _api.Requests.Count(r => r.Reply == "overloaded"));
    }

    private async Task<ClaudeSession> StartAsync(string fixtureName)
    {
        Assert.SkipWhen(_factory is null, "Claude Code isn't installed.");
        _fixtureName = fixtureName;
        return await _factory!.StartAsync(new ClaudeLaunchOptions
        {
            WorkingDirectory = Work,
            Model = "claude-haiku-4-5",
            ProtocolLogPath = LogPath,
            EnvironmentOverrides = new Dictionary<string, string?>
            {
                ["ANTHROPIC_BASE_URL"] = _api.BaseAddress.ToString().TrimEnd('/'),
                ["ANTHROPIC_API_KEY"] = "sk-ant-mock-000",
                ["ANTHROPIC_AUTH_TOKEN"] = null,
                ["CLAUDE_CODE_OAUTH_TOKEN"] = null,
                ["CLAUDE_CONFIG_DIR"] = Config,
                ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            },
        }, TestContext.Current.CancellationToken);
    }
}
