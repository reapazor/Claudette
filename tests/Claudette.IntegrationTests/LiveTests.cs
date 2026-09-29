using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Sessions;
using Claudette.Fixtures;
using Claudette.IntegrationTests.Support;
using Claudette.Usage;

namespace Claudette.IntegrationTests;

/// <summary>
/// The live suite (DESIGN.md §15): what only the real service can confirm. <b>These use real tokens</b>, so they're
/// tagged <c>Live</c>, left out of every normal run (<c>--filter "Category!=Live"</c>), and run by hand before a release:
/// <list type="bullet">
/// <item><c>CLAUDETTE_LIVE_API_KEY</c>: a separate API key with a spend limit, never a personal subscription. Real model
/// output, at the cheapest settings: Haiku, low effort, one-line prompts.</item>
/// <item><c>CLAUDETTE_LIVE_CONFIG_DIR</c>: a Claude Code config folder signed in to a subscription, for what an API key
/// can't show: <c>get_usage</c>'s plan limits and <c>claude auth status</c>. These make no model calls.</item>
/// <item><c>CLAUDETTE_RECORD_FIXTURES</c>: also writes the traffic there as cleaned protocol fixtures (record mode).</item>
/// </list>
/// Each test skips when its variable isn't set.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveTests : IAsyncLifetime
{
    private const string CheapModel = "claude-haiku-4-5";

    private readonly TempFolder _root = new("claudette-live");
    private ClaudeInstall? _install;
    private string? _fixtureName;

    private string Work => _root.Combine("repo");

    private string LogPath => _root.Combine("protocol.log");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Work);
        var located = await new ClaudeLocator(new ProcessLauncher(), TimeProvider.System).LocateAsync(null);
        _install = located.IsUsable ? located.Install : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (System.Environment.GetEnvironmentVariable("CLAUDETTE_RECORD_FIXTURES") is { Length: > 0 } folder && _fixtureName is not null && File.Exists(LogPath))
        {
            var lines = new ProtocolFixtureWriter([_root.Path]).FromProtocolLog(await File.ReadAllLinesAsync(LogPath));
            var target = Path.Combine(folder, _install?.Version.ToString() ?? "unknown", $"{_fixtureName}.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllLinesAsync(target, lines);
        }
        _root.Dispose();
    }

    [Fact]
    public async Task The_real_model_answers_a_one_line_prompt()
    {
        await using var session = await StartWithApiKeyAsync("live-simple-reply");

        await session.SendUserMessageAsync("Reply with just the word: pong", TestContext.Current.CancellationToken);
        var (done, seen) = await session.ReadUntilAsync<TurnCompleted>(timeout: TimeSpan.FromMinutes(2));

        Assert.False(done.Result.IsError, done.Result.Result);
        Assert.Contains("pong", done.Result.Result ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains(seen, e => e is TextDelta);
        // Per-model usage, with the context window the context estimate needs (DESIGN.md §6).
        var usage = Assert.Single(done.Result.ModelUsage!).Value!.AsObject();
        Assert.True(usage["outputTokens"]!.GetValue<double>() > 0);
        Assert.True(usage["contextWindow"]!.GetValue<double>() > 0);
    }

    [Fact]
    public async Task A_subscription_reports_its_account_and_plan_limits()
    {
        var config = System.Environment.GetEnvironmentVariable("CLAUDETTE_LIVE_CONFIG_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(config), "Set CLAUDETTE_LIVE_CONFIG_DIR to a signed-in Claude Code config folder.");
        Assert.SkipWhen(_install is null, "Claude Code isn't installed.");
        var factory = new ClaudeSessionFactory(_install!.Path, new ProcessLauncher(), TimeProvider.System);
        await using var utility = await UtilitySession.StartAsync(new ConfiguredFactory(factory, SignedInEnvironment(config!)), Work, TestContext.Current.CancellationToken);

        var response = await utility.GetUsageAsync(TestContext.Current.CancellationToken);
        var snapshot = UsageParser.FromGetUsage(response, DateTimeOffset.UtcNow);

        Assert.False(string.IsNullOrEmpty(utility.Initialization.Account.Email));
        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Session);
        Assert.InRange(snapshot.Session.Percent, 0, 100);
        Assert.NotNull(snapshot.WeeklyAll);
    }

    private async Task<ClaudeSession> StartWithApiKeyAsync(string fixtureName)
    {
        var key = System.Environment.GetEnvironmentVariable("CLAUDETTE_LIVE_API_KEY");
        Assert.SkipWhen(string.IsNullOrEmpty(key), "Set CLAUDETTE_LIVE_API_KEY to a separate API key with a spend limit.");
        Assert.SkipWhen(_install is null, "Claude Code isn't installed.");
        _fixtureName = fixtureName;
        var factory = new ClaudeSessionFactory(_install!.Path, new ProcessLauncher(), TimeProvider.System);
        Directory.CreateDirectory(_root.Combine("config"));
        return await factory.StartAsync(new ClaudeLaunchOptions
        {
            WorkingDirectory = Work,
            Model = CheapModel,
            Effort = "low",
            ProtocolLogPath = LogPath,
            EnvironmentOverrides = new Dictionary<string, string?>
            {
                ["ANTHROPIC_API_KEY"] = key,
                ["ANTHROPIC_BASE_URL"] = null,
                ["ANTHROPIC_AUTH_TOKEN"] = null,
                ["CLAUDE_CODE_OAUTH_TOKEN"] = null,
                ["CLAUDE_CONFIG_DIR"] = _root.Combine("config"),
                ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            },
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>The signed-in config folder, and nothing that would override its account.</summary>
    private static Dictionary<string, string?> SignedInEnvironment(string config) => new()
    {
        ["CLAUDE_CONFIG_DIR"] = config,
        ["ANTHROPIC_API_KEY"] = null,
        ["ANTHROPIC_BASE_URL"] = null,
        ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
    };

    /// <summary>Starts every session with the given environment, as the app's factory does with its own settings.</summary>
    private sealed class ConfiguredFactory(IClaudeSessionFactory inner, IReadOnlyDictionary<string, string?> environment) : IClaudeSessionFactory
    {
        public Task<ClaudeSession> StartAsync(ClaudeLaunchOptions options, CancellationToken cancellationToken = default) =>
            inner.StartAsync(options with { EnvironmentOverrides = environment }, cancellationToken);
    }
}
