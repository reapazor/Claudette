using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.Core.Composer;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Sessions;
using Claudette.MockApi;

namespace Claudette.App.Tests;

/// <summary>
/// Two tabs running the real <c>claude</c> against the mock Messages API (DESIGN.md §13, "Session naming"): each
/// session is named after its tab, Claude Code's sessions folder says so, and one tab's Claude reaches the other by the
/// tab's name with Claude Code's own <c>SendMessage</c>. No account and no tokens. Skipped when Claude Code isn't
/// installed.
/// </summary>
[Trait("Category", "RealCli")]
public sealed class SessionNameRealCliTests
{
    [Fact]
    public async Task A_tab_is_reached_by_its_name()
    {
        var located = await new ClaudeLocator(new ProcessLauncher(), TimeProvider.System).LocateAsync(null, TestContext.Current.CancellationToken);
        RealCli.SkipUnlessInstalled(located.IsUsable);
        await using var api = await MockAnthropicApi.StartAsync();
        await using var h = new TabTestHarness();
        var config = Directory.CreateDirectory(Path.Combine(h.Root, "claude-config")).FullName;
        h.Services.ClaudeConfigDirectory = config;
        h.Services.UseSessionFactory(new MockedFactory(new ClaudeSessionFactory(located.Install!.Path, new ProcessLauncher(), TimeProvider.System), new Dictionary<string, string?>
        {
            ["ANTHROPIC_BASE_URL"] = api.BaseAddress.ToString().TrimEnd('/'),
            ["ANTHROPIC_API_KEY"] = "sk-ant-mock-000",
            ["ANTHROPIC_AUTH_TOKEN"] = null,
            ["CLAUDE_CODE_OAUTH_TOKEN"] = null,
            ["CLAUDE_CONFIG_DIR"] = config,
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
        }));
        var plan = await ThreadScript.OpenAsync(h, "Plan");
        var art = await ThreadScript.OpenAsync(h, "Art page");

        // Claude Code's sessions folder has each under its tab's name.
        await Claudette.Tests.Waiting.UntilAsync(() => InlineDispatcher.Read(() => art.ReachedAs == "Art page" && plan.ReachedAs == "Plan"),
            "the sessions' names", TimeSpan.FromSeconds(30));
        Assert.Contains(InlineDispatcher.Read(plan.MentionTargets), t => t is { Name: "Art page", Address: "Art page", Kind: MentionKind.Tab });

        // Not a thread: Claude Code delivers it itself, to the session it knows as Art page.
        plan.ComposerText = "SEND_MESSAGE Art page | The build is green.";
        await plan.SendCommand.ExecuteAsync(null);

        await Claudette.Tests.Waiting.UntilAsync(
            () => InlineDispatcher.Read(() => art.Items.OfType<UserMessageItem>().Any(m => m.AutomaticLabel == "From the session Plan")), "the message", TimeSpan.FromSeconds(90));
        var received = InlineDispatcher.Read(() => art.Items.OfType<UserMessageItem>().Single(m => m.AutomaticLabel == "From the session Plan"));
        Assert.Contains("The build is green.", received.Text, StringComparison.Ordinal);
    }

    /// <summary>Starts the real <c>claude</c> pointed at the mock, keeping the tab's own variables.</summary>
    private sealed class MockedFactory(IClaudeSessionFactory inner, IReadOnlyDictionary<string, string?> environment) : IClaudeSessionFactory
    {
        public Task<ClaudeSession> StartAsync(ClaudeLaunchOptions options, CancellationToken cancellationToken = default)
        {
            var merged = new Dictionary<string, string?>(options.EnvironmentOverrides);
            foreach (var (name, value) in environment)
            {
                merged[name] = value;
            }
            return inner.StartAsync(options with { EnvironmentOverrides = merged, Model = "claude-haiku-4-5" }, cancellationToken);
        }
    }
}
