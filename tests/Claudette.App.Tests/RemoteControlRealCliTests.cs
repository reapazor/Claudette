using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.RemoteControl;
using Claudette.Core.Sessions;
using Claudette.MockApi;

namespace Claudette.App.Tests;

/// <summary>
/// A tab driving the real <c>claude</c> against the mock Messages API, with its Claude app switch turned on (DESIGN.md
/// §18, "Remote Control"): the whole path from the switch to Claude Code's own eligibility check and back to the tab.
/// No account and no tokens: the custom <c>ANTHROPIC_BASE_URL</c> is what rules it out. Skipped when Claude Code isn't
/// installed.
/// </summary>
[Trait("Category", "RealCli")]
public sealed class RemoteControlRealCliTests
{
    [Fact]
    public async Task Turning_a_tabs_switch_on_asks_Claude_Code_and_shows_why_it_cant_connect()
    {
        var located = await new ClaudeLocator(new ProcessLauncher(), TimeProvider.System).LocateAsync(null, TestContext.Current.CancellationToken);
        RealCli.SkipUnlessInstalled(located.IsUsable);
        await using var api = await MockAnthropicApi.StartAsync();
        await using var h = new TabTestHarness();
        var config = Directory.CreateDirectory(Path.Combine(h.Root, "claude-config")).FullName;
        h.Services.UseSessionFactory(new MockedFactory(new ClaudeSessionFactory(located.Install!.Path, new ProcessLauncher(), TimeProvider.System), new Dictionary<string, string?>
        {
            ["ANTHROPIC_BASE_URL"] = api.BaseAddress.ToString().TrimEnd('/'),
            ["ANTHROPIC_API_KEY"] = "sk-ant-mock-000",
            ["ANTHROPIC_AUTH_TOKEN"] = null,
            ["CLAUDE_CODE_OAUTH_TOKEN"] = null,
            ["CLAUDE_CONFIG_DIR"] = config,
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
        }));
        var tab = await h.OpenTabAsync();
        Assert.False(tab.RemoteControl.IsOn);

        await tab.RemoteControl.SetAsync(true);

        await TabTestHarness.Eventually(() => tab.RemoteControl.Status.State == RemoteControlState.Unavailable, "Claude Code's answer");
        Assert.Contains("api.anthropic.com", tab.RemoteControl.Status.Detail, StringComparison.Ordinal);
        Assert.StartsWith("Couldn't connect to the Claude app: Remote Control", InlineDispatcher.Read(() => tab.Items.OfType<NoteItem>().Last().Text), StringComparison.Ordinal);
        Assert.False(h.SleepBlocker.IsBlocking);
        // The request went to Claude Code, not to the model (the context indicator counts tokens, which is free).
        Assert.DoesNotContain(api.Requests, r => r.Path.Contains("/messages", StringComparison.Ordinal) && !r.Path.Contains("count_tokens", StringComparison.Ordinal));
    }

    /// <summary>Starts the real <c>claude</c> pointed at the mock, keeping the tab's own variables (the presence file).</summary>
    private sealed class MockedFactory(IClaudeSessionFactory inner, IReadOnlyDictionary<string, string?> environment) : IClaudeSessionFactory
    {
        public Task<ClaudeSession> StartAsync(ClaudeLaunchOptions options, CancellationToken cancellationToken = default)
        {
            var merged = new Dictionary<string, string?>(options.EnvironmentOverrides);
            foreach (var (name, value) in environment)
            {
                merged[name] = value;
            }
            // A cloud session's own variables change what Claude Code says about Remote Control: without them, as on a desktop.
            foreach (var name in Environment.GetEnvironmentVariables().Keys.OfType<string>().Where(n => n.StartsWith("CLAUDE_CODE_REMOTE", StringComparison.OrdinalIgnoreCase)))
            {
                merged[name] = null;
            }
            return inner.StartAsync(options with { EnvironmentOverrides = merged, Model = "claude-haiku-4-5" }, cancellationToken);
        }
    }
}
