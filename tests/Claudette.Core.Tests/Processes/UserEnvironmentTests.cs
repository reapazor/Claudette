using Claudette.Core.Auth;
using Claudette.Core.Claude;
using Claudette.Core.Git;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Processes;

/// <summary>The environment of the user's processes, with the login shell's merged in (DESIGN.md §13, "Login shell environment").</summary>
public class UserEnvironmentTests
{
    private static readonly Dictionary<string, string> Own = new()
    {
        ["PATH"] = "/usr/bin:/bin",
        ["HOME"] = "/Users/me",
        ["CLAUDETTE_HOME"] = "/tmp/claudette-home",
        ["XPC_SERVICE_NAME"] = "application.com.reapazor.claudette",
    };

    private static readonly Dictionary<string, string> Login = new()
    {
        ["PATH"] = "/opt/homebrew/bin:/usr/bin:/bin",
        ["HOME"] = "/Users/me",
        ["HOMEBREW_PREFIX"] = "/opt/homebrew",
        ["GITHUB_TOKEN"] = "ghp_secret-value",
        ["CLAUDETTE_HOME"] = "/from/zprofile",
        ["CLAUDETTE_RESOLVING_ENVIRONMENT"] = "1",
        ["CLAUDE_CODE_ENTRYPOINT"] = "cli",
        ["_"] = "/usr/bin/env",
        ["PWD"] = "/Users/me",
        ["OLDPWD"] = "/",
        ["SHLVL"] = "1",
    };

    // ---- The merge --------------------------------------------------------------------------------------------

    [Fact]
    public void The_login_shells_variables_go_on_top_of_Claudettes_own()
    {
        var merged = UserEnvironment.Merge(Own, Login);

        Assert.Equal("/opt/homebrew/bin:/usr/bin:/bin", merged["PATH"]);
        Assert.Equal("/opt/homebrew", merged["HOMEBREW_PREFIX"]);
        Assert.Equal("ghp_secret-value", merged["GITHUB_TOKEN"]);
        // Variables only Claudette has are kept.
        Assert.Equal("application.com.reapazor.claudette", merged["XPC_SERVICE_NAME"]);
    }

    [Fact]
    public void Claudettes_own_variables_and_Claude_Codes_session_variables_never_come_from_the_shell()
    {
        var merged = UserEnvironment.Merge(Own, Login);

        Assert.Equal("/tmp/claudette-home", merged["CLAUDETTE_HOME"]);
        Assert.False(merged.ContainsKey("CLAUDETTE_RESOLVING_ENVIRONMENT"));
        Assert.False(merged.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
        // What the shell says about itself describes the shell Claudette ran.
        Assert.False(merged.ContainsKey("_"));
        Assert.False(merged.ContainsKey("PWD"));
        Assert.False(merged.ContainsKey("OLDPWD"));
        Assert.False(merged.ContainsKey("SHLVL"));
    }

    [Fact]
    public void Claude_starts_from_the_merge_with_the_session_variables_stripped_and_its_launch_overrides_on_top()
    {
        var own = new Dictionary<string, string>(Own) { ["CLAUDECODE"] = "1", ["CLAUDE_CODE_SESSION_ID"] = "abc" };
        var merged = UserEnvironment.Merge(own, Login);

        var environment = ClaudeEnvironment.From(merged, new Dictionary<string, string?>
        {
            ["CLAUDE_CONFIG_DIR"] = "/tmp/config",
            ["HOMEBREW_PREFIX"] = null,
        });

        Assert.Equal("/opt/homebrew/bin:/usr/bin:/bin", environment["PATH"]);
        Assert.Equal("/tmp/config", environment["CLAUDE_CONFIG_DIR"]);
        Assert.False(environment.ContainsKey("HOMEBREW_PREFIX"));
        Assert.False(environment.ContainsKey("CLAUDECODE"));
        Assert.False(environment.ContainsKey("CLAUDE_CODE_SESSION_ID"));
        Assert.Equal("/tmp/claudette-home", environment["CLAUDETTE_HOME"]);
    }

    [Fact]
    public void Without_a_base_Claude_starts_from_Claudettes_own_environment()
    {
        var environment = ClaudeEnvironment.From(null, new Dictionary<string, string?> { ["ADDED"] = "1" });

        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), environment.GetValueOrDefault("PATH"));
        Assert.Equal("1", environment["ADDED"]);
    }

    // ---- Reading it once, and waiting ------------------------------------------------------------------------

    [Fact]
    public async Task The_first_start_waits_for_the_shell_and_later_ones_use_what_it_read()
    {
        var shell = new FakeLoginShell();
        var environment = new UserEnvironment(shell, () => true, ownEnvironment: Own);

        environment.Start();
        var first = environment.GetAsync(TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);
        Assert.Null(environment.Current);

        shell.Finish(Used());
        var read = await first;

        Assert.Equal("/opt/homebrew/bin:/usr/bin:/bin", read!["PATH"]);
        Assert.Same(read, await environment.GetAsync(TestContext.Current.CancellationToken));
        Assert.Same(read, environment.Current);
        environment.Start();
        Assert.Equal(1, shell.Reads);
    }

    [Fact]
    public async Task Turned_off_nothing_is_read_and_Claudettes_own_environment_is_used()
    {
        var shell = new FakeLoginShell();
        shell.Finish(Used());
        var enabled = false;
        var environment = new UserEnvironment(shell, () => enabled, ownEnvironment: Own);

        environment.Start();
        Assert.Null(await environment.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, shell.Reads);
        Assert.Equal("Not used: turned off in Settings → Claude Code.", environment.Describe());

        // Turning it on reads it then; turning it off again goes back to Claudette's own for new processes.
        enabled = true;
        environment.Start();
        Assert.NotNull(await environment.GetAsync(TestContext.Current.CancellationToken));
        enabled = false;
        Assert.Null(await environment.GetAsync(TestContext.Current.CancellationToken));
        Assert.Null(environment.Current);
        Assert.Equal(1, shell.Reads);
    }

    [Theory]
    [InlineData(LoginShellOutcome.StartedFromTerminal, "Not used: Claudette was started from a terminal, so it has your shell's environment already.")]
    [InlineData(LoginShellOutcome.NotNeededOnWindows, "Not used: not needed on Windows, where apps get your full environment.")]
    public async Task When_it_is_not_needed_the_shell_is_never_run(LoginShellOutcome reason, string description)
    {
        var shell = new FakeLoginShell(reason);
        var environment = new UserEnvironment(shell, () => true, ownEnvironment: Own);

        environment.Start();

        Assert.Null(await environment.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, shell.Reads);
        Assert.Equal(description, environment.Describe());
    }

    [Fact]
    public async Task A_shell_that_times_out_leaves_Claudettes_own_environment()
    {
        var shell = new FakeLoginShell();
        shell.Finish(new LoginShellResult(LoginShellOutcome.TimedOut, "/bin/zsh", TimeSpan.FromSeconds(10), Detail: "didn't finish within 10 s"));
        var environment = new UserEnvironment(shell, () => true, ownEnvironment: Own);

        Assert.Null(await environment.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Not used: /bin/zsh didn't finish within 10 s. New processes get Claudette's own environment.", environment.Describe());
    }

    [Fact]
    public async Task A_shell_reader_that_throws_leaves_Claudettes_own_environment()
    {
        var shell = new FakeLoginShell { Failure = new InvalidOperationException("boom") };
        var environment = new UserEnvironment(shell, () => true, ownEnvironment: Own);

        Assert.Null(await environment.GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Not used: /bin/zsh failed. New processes get Claudette's own environment.", environment.Describe());
    }

    [Fact]
    public async Task Diagnostics_name_the_shell_the_time_and_what_changed_but_never_a_value()
    {
        var shell = new FakeLoginShell();
        shell.Finish(Used());
        var environment = new UserEnvironment(shell, () => true, ownEnvironment: Own);

        Assert.Equal("Reading the environment of /bin/zsh…", environment.Describe());
        await environment.GetAsync(TestContext.Current.CancellationToken);
        var text = environment.Describe();

        Assert.Equal("Used: /bin/zsh, read in 0.42 s. 11 variables, 3 added or changed: GITHUB_TOKEN, HOMEBREW_PREFIX, PATH.", text);
        foreach (var value in Login.Values.Where(v => v.Length > 1))
        {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        }
    }

    // ---- Where it's used ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_bare_program_is_found_on_the_login_PATH_and_gets_the_environment()
    {
        using var temp = new TempFolder();
        var bin = temp.CreateFolder("brew/bin");
        var git = temp.Write("brew/bin/git", "");
        var environment = await UsedEnvironmentAsync(bin);

        var bare = environment.Apply(new ProcessStartSpec("git", ["status"]));
        var absolute = environment.Apply(new ProcessStartSpec("/usr/bin/git", ["status"]));
        var missing = environment.Apply(new ProcessStartSpec("p4", ["info"]));

        Assert.Equal(git, bare.FileName);
        Assert.Equal(bin, bare.Environment!["PATH"]);
        Assert.Equal("/usr/bin/git", absolute.FileName);
        Assert.Equal("p4", missing.FileName);
        Assert.Equal(git, environment.Probe.FindOnPath("git"));
        Assert.Equal(bin, environment.PathVariable);
    }

    [Fact]
    public void Without_the_login_shell_a_process_starts_as_it_did()
    {
        var spec = new ProcessStartSpec("git", ["status"]);

        Assert.Same(spec, UserEnvironment.Inherited.Apply(spec));
        Assert.Null(UserEnvironment.Inherited.Current);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), UserEnvironment.Inherited.PathVariable);
    }

    [Fact]
    public async Task Claude_is_found_on_the_login_PATH_and_its_version_read_with_the_login_environment()
    {
        using var temp = new TempFolder();
        var bin = temp.CreateFolder("bin");
        var claude = temp.Write("bin/" + (OperatingSystem.IsWindows() ? "claude.exe" : "claude"), "");
        var shell = new FakeLoginShell();
        var environment = new UserEnvironment(shell, () => true, ownEnvironment: new Dictionary<string, string>(Own) { ["CLAUDECODE"] = "1" });
        environment.Start();
        var launcher = new FakeProcessLauncher { Respond = _ => new ProcessResult(0, "2.1.284 (Claude Code)\n", "") };
        var locator = new ClaudeLocator(launcher, new FakeTimeProvider(), environment);

        var locating = locator.LocateAsync(null, TestContext.Current.CancellationToken);
        // The locator is the first claude start: it waits for the login shell.
        Assert.False(locating.IsCompleted);
        Assert.Empty(launcher.Started);
        shell.Finish(Used(path: bin));
        var result = await locating;

        Assert.True(result.IsUsable, result.Detail);
        Assert.Equal(claude, result.Install!.Path);
        var spec = Assert.Single(launcher.Started);
        Assert.Equal(bin, spec.Environment!["PATH"]);
        Assert.Equal("/opt/homebrew", spec.Environment["HOMEBREW_PREFIX"]);
        Assert.False(spec.Environment.ContainsKey("CLAUDECODE"));
    }

    [Fact]
    public async Task Claude_auth_commands_start_from_the_login_environment()
    {
        var environment = await UsedEnvironmentAsync("/opt/homebrew/bin:/usr/bin");
        var launcher = new FakeProcessLauncher { Respond = _ => new ProcessResult(0, """{"loggedIn": false}""", "") };
        var auth = new ClaudeAuth("/opt/claude", launcher, new FakeTimeProvider(), new Dictionary<string, string?> { ["FAKE"] = "1" }, environment);

        await auth.GetStatusAsync(TestContext.Current.CancellationToken);

        var spec = Assert.Single(launcher.Started);
        Assert.Equal("/opt/homebrew/bin:/usr/bin", spec.Environment!["PATH"]);
        Assert.Equal("1", spec.Environment["FAKE"]);
    }

    [Fact]
    public async Task Git_runs_as_found_on_the_login_PATH_with_the_login_environment()
    {
        using var temp = new TempFolder();
        var bin = temp.CreateFolder("bin");
        var git = temp.Write("bin/git", "");
        var environment = await UsedEnvironmentAsync(bin);
        var launcher = new FakeProcessLauncher { Respond = _ => new ProcessResult(0, temp.Path + "\n", "") };
        var tree = new GitWorkingTree(launcher, new FakeTimeProvider(), environment: environment);

        await tree.GetRepositoryRootAsync(temp.Path, TestContext.Current.CancellationToken);

        var spec = Assert.Single(launcher.Started);
        Assert.Equal(git, spec.FileName);
        Assert.Equal("/opt/homebrew", spec.Environment!["HOMEBREW_PREFIX"]);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------

    private static LoginShellResult Used(string? path = null) =>
        new(LoginShellOutcome.Used, "/bin/zsh", TimeSpan.FromMilliseconds(420),
            path is null ? Login : new Dictionary<string, string>(Login) { ["PATH"] = path });

    private static async Task<UserEnvironment> UsedEnvironmentAsync(string path)
    {
        var shell = new FakeLoginShell();
        shell.Finish(Used(path));
        var environment = new UserEnvironment(shell, () => true, ownEnvironment: Own);
        await environment.GetAsync(TestContext.Current.CancellationToken);
        return environment;
    }

    private sealed class FakeLoginShell(LoginShellOutcome? notNeeded = null) : ILoginShell
    {
        private readonly TaskCompletionSource<LoginShellResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public string Shell => "/bin/zsh";

        public LoginShellOutcome? NotNeeded => notNeeded;

        public Exception? Failure { get; init; }

        public int Reads => Volatile.Read(ref _reads);

        public void Finish(LoginShellResult result) => _result.TrySetResult(result);

        public Task<LoginShellResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _reads);
            return Failure is not null ? Task.FromException<LoginShellResult>(Failure) : _result.Task;
        }
    }
}
