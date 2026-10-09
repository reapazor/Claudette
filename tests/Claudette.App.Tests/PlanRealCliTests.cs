using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Sessions;
using Claudette.MockApi;

namespace Claudette.App.Tests;

/// <summary>
/// A tab in Plan mode driving the real <c>claude</c> against the mock Messages API (DESIGN.md §5, "Tasks"): the plan
/// shows as Claude writes it to the plan file Claude Code names, and becomes the version the card puts up. No account
/// and no tokens. Skipped when Claude Code isn't installed.
/// </summary>
[Trait("Category", "RealCli")]
public sealed class PlanRealCliTests
{
    [Fact]
    public async Task The_plan_file_Claude_writes_in_Plan_mode_is_the_draft_and_then_the_plan_put_up()
    {
        var located = await new ClaudeLocator(new ProcessLauncher(), TimeProvider.System).LocateAsync(null, TestContext.Current.CancellationToken);
        RealCli.SkipUnlessInstalled(located.IsUsable);
        await using var api = await MockAnthropicApi.StartAsync();
        await using var h = new TabTestHarness(settings => settings.NewTabs.DefaultPermissionMode = "plan");
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
        var drafted = false;
        tab.TodoList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TodoList.IsDrafting) && tab.TodoList.IsDrafting)
            {
                drafted = true;
            }
        };

        tab.ComposerText = "EXIT_PLAN";
        await tab.SendCommand.ExecuteAsync(null);

        await Claudette.Tests.Waiting.UntilAsync(() => InlineDispatcher.Read(() => tab.Items.OfType<PlanItem>().Any()), "the plan card", TimeSpan.FromSeconds(60));
        Assert.True(drafted, "The plan file Claude wrote didn't show as a draft.");
        var version = Assert.Single(InlineDispatcher.Read(() => tab.TodoList.Plans.ToArray()));
        Assert.Equal(PlanState.Waiting, version.State);
        Assert.Equal("1. Read the code\n2. Fix the bug", version.Text);
        var card = InlineDispatcher.Read(() => tab.Items.OfType<PlanItem>().Single());
        Assert.Equal("Claude's plan", card.Title);
        Assert.Contains("Fix the bug", card.Plan.ToString(), StringComparison.Ordinal);
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
