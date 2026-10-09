using Claudette.App.Conversation;
using Claudette.App.Tests.Support;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Sessions;
using Claudette.MockApi;

namespace Claudette.App.Tests;

/// <summary>
/// A thread and its sub-thread, each a tab running the real <c>claude</c> against the mock Messages API (DESIGN.md §18,
/// "Threads"): the thread's Claude calls Claude Code's own <c>SendMessage</c>, Claudette's hook delivers it to the
/// sub-thread, and the sub-thread's reply comes back to the thread. No account and no tokens. Skipped when Claude Code
/// isn't installed.
/// </summary>
[Trait("Category", "RealCli")]
public sealed class ThreadRealCliTests
{
    [Fact]
    public async Task A_threads_message_reaches_its_sub_thread_and_the_reply_comes_back()
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
        var plan = await ThreadScript.OpenAsync(h, "Plan");
        var art = await ThreadScript.OpenAsync(h, "Art page");
        h.Shell.MakeThreadCommand.Execute(plan);
        art.AssignToThreadCommand.Execute(plan);
        plan.ToggleAskBeforeSendingToSubThreadsCommand.Execute(null);

        // The message scripts the sub-thread's reply.
        plan.ComposerText = "SEND_MESSAGE Art page | AGENT_REPLY The art page is done.";
        await plan.SendCommand.ExecuteAsync(null);

        await Claudette.Tests.Waiting.UntilAsync(
            () => InlineDispatcher.Read(() => plan.Items.OfType<UserMessageItem>().Any(m => m.AutomaticLabel == "From the sub-threads")), "the report", TimeSpan.FromSeconds(90));
        var delivered = InlineDispatcher.Read(() => art.Items.OfType<UserMessageItem>().Single(m => m.IsFromThread));
        Assert.Equal("AGENT_REPLY The art page is done.", delivered.Text);
        Assert.Equal("From the thread Plan", delivered.AutomaticLabel);
        // The hook stopped Claude Code's own delivery: no copy came as another session's message.
        Assert.DoesNotContain(InlineDispatcher.Read(() => art.Items.OfType<UserMessageItem>().ToArray()), m => m.AutomaticLabel?.StartsWith("From the session", StringComparison.Ordinal) == true);
        var report = InlineDispatcher.Read(() => plan.Items.OfType<UserMessageItem>().Single(m => m.AutomaticLabel == "From the sub-threads"));
        Assert.Contains("## Art page\n\nFinished. Its final reply:\n\nThe art page is done.", report.Text, StringComparison.Ordinal);
        // The tool call reads as a refusal to Claude Code, but its row says it went.
        var send = InlineDispatcher.Read(() => plan.Items.OfType<ToolUseItem>().Single(t => t.Name == "SendMessage"));
        Assert.True(send.IsComplete);
        Assert.False(send.IsError);
        Assert.Contains(api.Requests, r => r.LastToolResultText.StartsWith("Claudette delivered this message to the sub-thread \"Art page\"", StringComparison.Ordinal));
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
