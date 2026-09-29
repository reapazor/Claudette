using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Processes;

namespace Claudette.App.Tests;

/// <summary>Settings → Claude Code → Use my login shell's environment, and what Diagnostics says (DESIGN.md §13, §14).</summary>
public class LoginShellSettingsTests
{
    private const string Secret = "ghp_do-not-show-me";

    [Fact]
    public async Task Diagnostics_name_the_shell_and_what_it_changed_but_never_a_value()
    {
        var shell = new FakeLoginShell();
        await using var h = new TabTestHarness(loginShell: shell);
        h.Services.UserEnvironment.Start();
        await h.Services.UserEnvironment.GetAsync(TestContext.Current.CancellationToken);
        var settings = new SettingsViewModel(h.Services, null) { SelectedCategory = "Advanced" };

        Assert.StartsWith("Used: /bin/zsh, read in 0.25 s.", settings.LoginShellText, StringComparison.Ordinal);
        Assert.Contains("GITHUB_TOKEN", settings.LoginShellText, StringComparison.Ordinal);
        await settings.CopyDiagnosticsCommand.ExecuteAsync(null);

        Assert.Contains("Login shell environment: Used: /bin/zsh", h.Platform.Clipboard, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, h.Platform.Clipboard, StringComparison.Ordinal);
        Assert.DoesNotContain("/opt/homebrew/bin", h.Platform.Clipboard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turning_it_on_reads_the_shell_then_and_off_says_so()
    {
        var shell = new FakeLoginShell();
        await using var h = new TabTestHarness(s => s.ClaudeCode.UseLoginShellEnvironment = false, loginShell: shell);
        h.Services.UserEnvironment.Start();
        var settings = new SettingsViewModel(h.Services, null);

        Assert.False(settings.UseLoginShellEnvironment);
        Assert.Equal(0, shell.Reads);
        Assert.Equal("Not used: turned off in Settings → Claude Code.", settings.LoginShellText);

        settings.UseLoginShellEnvironment = true;
        var environment = await h.Services.UserEnvironment.GetAsync(TestContext.Current.CancellationToken);

        Assert.True(h.Services.Settings.ClaudeCode.UseLoginShellEnvironment);
        Assert.Equal(1, shell.Reads);
        Assert.Equal("/opt/homebrew/bin:/usr/bin", environment!["PATH"]);
    }

    [Fact]
    public async Task Reset_to_defaults_turns_it_back_on()
    {
        await using var h = new TabTestHarness(s => s.ClaudeCode.UseLoginShellEnvironment = false);
        var settings = new SettingsViewModel(h.Services, null);

        settings.ResetClaudeCodeCommand.Execute(null);

        Assert.True(h.Services.Settings.ClaudeCode.UseLoginShellEnvironment);
        Assert.True(settings.UseLoginShellEnvironment);
    }

    [Fact]
    public async Task Only_macOS_and_Linux_show_it_and_find_it_in_search()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);

        Assert.Equal(!OperatingSystem.IsWindows(), SettingsViewModel.ShowLoginShellSetting);
        var found = settings.SearchResultsFor("login shell");
        if (OperatingSystem.IsWindows())
        {
            Assert.Empty(found);
        }
        else
        {
            Assert.Equal([new SettingsSearchResult("Claude Code", "Use my login shell's environment")], found);
        }
    }

    private sealed class FakeLoginShell : ILoginShell
    {
        private int _reads;

        public string Shell => "/bin/zsh";

        public LoginShellOutcome? NotNeeded => null;

        public int Reads => Volatile.Read(ref _reads);

        public Task<LoginShellResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(new LoginShellResult(LoginShellOutcome.Used, Shell, TimeSpan.FromMilliseconds(250), new Dictionary<string, string>
            {
                ["PATH"] = "/opt/homebrew/bin:/usr/bin",
                ["GITHUB_TOKEN"] = Secret,
            }));
        }
    }
}
