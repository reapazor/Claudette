using System.ComponentModel;
using Claudette.Core.Processes;
using Claudette.Platform.LoginShell;
using Claudette.Platform.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Platform.Tests.LoginShell;

/// <summary>Reading the login shell's environment on macOS and Linux (DESIGN.md §13, "Login shell environment").</summary>
public sealed class LoginShellTests : IDisposable
{
    private const string Marker = "_MK_";

    private readonly string _home = Directory.CreateTempSubdirectory("claudette-loginshell-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    // ---- Reading the output ------------------------------------------------------------------------------------

    [Fact]
    public void Reads_the_environment_between_the_markers_and_ignores_what_rc_files_print()
    {
        var output = "Welcome back!\nLast login: Tuesday\n"
            + $"{Marker}AHOME=/home/me\0URL=https://example.com/?a=b&c=d\0MULTI=line one\nline two\n\0EMPTY=\0PATH=/opt/homebrew/bin:/usr/bin\0{Marker}B"
            + $"HOME=/home/me\nPATH=/usr/bin\n{Marker}C"
            + "Goodbye from .zlogout\n";

        var environment = LoginShellOutput.Parse(output, Marker);

        Assert.NotNull(environment);
        Assert.Equal(["EMPTY", "HOME", "MULTI", "PATH", "URL"], environment.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("https://example.com/?a=b&c=d", environment["URL"]);
        Assert.Equal("line one\nline two\n", environment["MULTI"]);
        Assert.Equal("", environment["EMPTY"]);
        // The NUL-separated copy wins over the line-by-line one.
        Assert.Equal("/opt/homebrew/bin:/usr/bin", environment["PATH"]);
    }

    [Fact]
    public void Falls_back_to_one_variable_per_line_when_env_has_no_null_option()
    {
        // BSD env without -0 prints its usage to standard error, so nothing arrives between the first two markers.
        var output = $"junk{Marker}A{Marker}B"
            + "PATH=/usr/local/bin:/usr/bin\nEQUALS=a=b=c\nEMPTY=\nBASH_FUNC_greet%%=() {  echo \"x=1\"\n}\nLAST=value\n"
            + $"{Marker}Cmore junk";

        var environment = LoginShellOutput.Parse(output, Marker);

        Assert.NotNull(environment);
        Assert.Equal("/usr/local/bin:/usr/bin", environment["PATH"]);
        Assert.Equal("a=b=c", environment["EQUALS"]);
        Assert.Equal("", environment["EMPTY"]);
        Assert.Equal("() {  echo \"x=1\"\n}", environment["BASH_FUNC_greet%%"]);
        Assert.Equal("value", environment["LAST"]);
        Assert.Equal(5, environment.Count);
    }

    [Fact]
    public void Output_read_with_Windows_line_endings_keeps_values_whole()
    {
        var output = $"{Marker}AMULTI=one\r\ntwo\0{Marker}B{Marker}C";

        Assert.Equal("one\ntwo", LoginShellOutput.Parse(output, Marker)!["MULTI"]);
    }

    [Theory]
    [InlineData("no markers at all\nPATH=/usr/bin\n")]
    [InlineData("_MK_A_MK_B_MK_C")]
    [InlineData("_MK_APATH=/usr/bin\0")]
    public void Output_without_an_environment_between_the_markers_is_nothing(string output)
    {
        Assert.Null(LoginShellOutput.Parse(output, Marker));
    }

    // ---- The command ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/bin/zsh", "-i -l -c")]
    [InlineData("/bin/bash", "-i -l -c")]
    [InlineData("/usr/bin/dash", "-i -l -c")]
    [InlineData("/bin/sh", "-i -l -c")]
    [InlineData("/opt/homebrew/bin/fish", "-l -i -c")]
    [InlineData("/bin/tcsh", "-i -c")]
    public void Runs_a_login_interactive_shell(string shell, string options)
    {
        var arguments = LoginShellOutput.Arguments(shell, Marker);

        Assert.NotNull(arguments);
        Assert.Equal(options, string.Join(' ', arguments.SkipLast(1)));
        Assert.Equal($"printf {Marker}A; /usr/bin/env -0; printf {Marker}B; /usr/bin/env; printf {Marker}C; exit 0", arguments[^1]);
    }

    [Theory]
    [InlineData("/opt/homebrew/bin/nu")]
    [InlineData("/usr/local/bin/pwsh")]
    [InlineData("/usr/bin/xonsh")]
    [InlineData("/usr/bin/false")]
    public void Other_shells_are_not_run(string shell)
    {
        Assert.Null(LoginShellOutput.Arguments(shell, Marker));
    }

    // ---- When it's needed -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, false, false)] // The Dock, Finder, open, a desktop launcher
    [InlineData("xterm-256color", true, true)] // A terminal, dotnet run
    [InlineData("xterm-256color", false, true)] // The terminal has closed, or startx left TERM in the session
    [InlineData(null, true, true)] // A terminal with TERM removed: the shell could take it over
    [InlineData("", false, false)]
    public void Started_from_a_terminal_when_TERM_is_set_or_there_is_a_controlling_terminal(string? term, bool terminal, bool expected)
    {
        var environment = new Dictionary<string, string> { ["PATH"] = "/usr/bin" };
        if (term is not null)
        {
            environment["TERM"] = term;
        }

        Assert.Equal(expected, LoginShellReader.StartedFromTerminal(environment, () => terminal));
    }

    [Fact]
    public void Windows_never_needs_it()
    {
        var host = Host(new() { ["SHELL"] = "/bin/zsh" }) with { IsWindows = true, HasControllingTerminal = () => throw new InvalidOperationException("Not asked on Windows.") };

        Assert.Equal(LoginShellOutcome.NotNeededOnWindows, LoginShellReader.WhyNotNeeded(host));
    }

    [Fact]
    public void Without_a_terminal_it_is_needed()
    {
        Assert.Null(LoginShellReader.WhyNotNeeded(Host(new() { ["SHELL"] = "/bin/zsh" })));
        Assert.Equal(LoginShellOutcome.StartedFromTerminal, LoginShellReader.WhyNotNeeded(Host(new() { ["TERM"] = "xterm" })));
    }

    [Theory]
    [InlineData("/opt/homebrew/bin/fish", true, "/opt/homebrew/bin/fish")]
    [InlineData(null, true, "/bin/zsh")]
    [InlineData(null, false, "/bin/bash")]
    [InlineData("  ", false, "/bin/bash")]
    public void Runs_SHELL_or_the_OS_default(string? shell, bool isMac, string expected)
    {
        var environment = new Dictionary<string, string>();
        if (shell is not null)
        {
            environment["SHELL"] = shell;
        }

        Assert.Equal(expected, LoginShellReader.ChooseShell(Host(environment) with { IsMacOS = isMac }));
    }

    // ---- Running it, with a fake launcher ---------------------------------------------------------------------

    [Fact]
    public async Task Runs_the_shell_in_the_home_folder_with_Claudettes_environment_and_reads_its_output()
    {
        var launcher = new FakeLauncher();
        var time = new FakeTimeProvider();
        var reader = Reader(launcher, time, new() { ["SHELL"] = "/bin/zsh", ["PATH"] = "/usr/bin:/bin", ["CLAUDECODE"] = "1", ["HOME"] = _home });

        var reading = reader.ReadAsync(TestContext.Current.CancellationToken);
        var spec = Assert.Single(launcher.Specs);
        time.Advance(TimeSpan.FromMilliseconds(420));
        Write(launcher.Started[0], $"zsh greeting\n{Marker}APATH=/opt/homebrew/bin:/usr/bin\0SECRET=hunter2\0{Marker}B{Marker}C");
        launcher.Started[0].Exit(0);
        var result = await reading;

        Assert.Equal("/bin/zsh", spec.FileName);
        Assert.Equal(["-i", "-l", "-c"], spec.Arguments.Take(3));
        Assert.Equal(_home, spec.WorkingDirectory);
        Assert.Equal("1", spec.Environment![LoginShellReader.ResolvingVariable]);
        Assert.Equal("/usr/bin:/bin", spec.Environment["PATH"]);
        // Started from inside Claude Code's terminal or not, the shell doesn't get its session variables.
        Assert.False(spec.Environment.ContainsKey("CLAUDECODE"));
        Assert.True(launcher.Started[0].InputClosed);

        Assert.Equal(LoginShellOutcome.Used, result.Outcome);
        Assert.Equal("/bin/zsh", result.Shell);
        Assert.Equal(TimeSpan.FromMilliseconds(420), result.Duration);
        Assert.Equal("/opt/homebrew/bin:/usr/bin", result.Environment!["PATH"]);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task A_shell_that_does_not_finish_in_time_is_stopped_and_not_used()
    {
        var launcher = new FakeLauncher();
        var time = new FakeTimeProvider();
        var reader = Reader(launcher, time, new() { ["SHELL"] = "/bin/bash" });

        var reading = reader.ReadAsync(TestContext.Current.CancellationToken);
        // An rc file that starts tmux, or waits for something.
        Write(launcher.Started[0], "starting tmux…");
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(reading.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await reading;

        Assert.Equal(LoginShellOutcome.TimedOut, result.Outcome);
        Assert.Equal("didn't finish within 10 s", result.Detail);
        Assert.Null(result.Environment);
        Assert.True(launcher.Started[0].KillCalls > 0);
    }

    [Fact]
    public async Task A_shell_that_cannot_start_is_not_used()
    {
        var reader = Reader(new ThrowingLauncher(new Win32Exception(2)), new FakeTimeProvider(), new() { ["SHELL"] = "/no/such/folder/zsh" });

        var result = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LoginShellOutcome.Failed, result.Outcome);
        Assert.StartsWith("couldn't be started (", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(_home, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shell_that_prints_no_environment_is_not_used()
    {
        var launcher = new FakeLauncher();
        var reader = Reader(launcher, new FakeTimeProvider(), new() { ["SHELL"] = "/bin/bash" });

        var reading = reader.ReadAsync(TestContext.Current.CancellationToken);
        Write(launcher.Started[0], "exec fish from .bashrc");
        launcher.Started[0].Exit(1);
        var result = await reading;

        Assert.Equal(LoginShellOutcome.Failed, result.Outcome);
        Assert.Equal("exited with code 1 without printing its environment", result.Detail);
    }

    [Fact]
    public async Task An_unsupported_shell_or_a_terminal_start_runs_nothing()
    {
        var launcher = new FakeLauncher();

        var nushell = await Reader(launcher, new FakeTimeProvider(), new() { ["SHELL"] = "/opt/homebrew/bin/nu" }).ReadAsync(TestContext.Current.CancellationToken);
        var terminal = await Reader(launcher, new FakeTimeProvider(), new() { ["SHELL"] = "/bin/zsh", ["TERM"] = "xterm-256color" }).ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(LoginShellOutcome.UnsupportedShell, nushell.Outcome);
        Assert.Contains("isn't a shell Claudette can read", nushell.Detail, StringComparison.Ordinal);
        Assert.Equal(LoginShellOutcome.StartedFromTerminal, terminal.Outcome);
        Assert.Empty(launcher.Specs);
    }

    [Fact]
    public void The_controlling_terminal_check_answers_without_throwing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Only macOS and Linux ask.");

        _ = LoginShellHost.Current().HasControllingTerminal();
    }

    // ---- A real shell, with a made-up home folder -------------------------------------------------------------

    [Theory]
    [InlineData("bash", ".bash_profile")]
    [InlineData("sh", ".profile")]
    [InlineData("zsh", ".zprofile")]
    [InlineData("fish", ".config/fish/config.fish")]
    public async Task A_real_shell_reads_the_profile_in_its_home_folder(string name, string profile)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Login shells are read on macOS and Linux.");
        var shell = new[] { "/bin", "/usr/bin", "/usr/local/bin", "/opt/homebrew/bin" }.Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
        Assert.SkipWhen(shell is null, $"{name} isn't installed.");

        var bin = Directory.CreateDirectory(Path.Combine(_home, "bin")).FullName;
        var script = name == "fish"
            ? $"echo 'Hello from config.fish'\nset -gx LOGIN_SHELL_TEST from-profile\nset -gx LOGIN_SHELL_MULTI \"first\nsecond\"\nset -gx LOGIN_SHELL_EQUALS 'a=b'\nset -gx SAW_RESOLVING \"${LoginShellReader.ResolvingVariable}\"\nset -gx PATH {bin} $PATH\n"
            : $"echo 'Hello from {profile}'\nexport LOGIN_SHELL_TEST=from-profile\nexport LOGIN_SHELL_MULTI='first\nsecond'\nexport LOGIN_SHELL_EQUALS='a=b'\nexport SAW_RESOLVING=\"${LoginShellReader.ResolvingVariable}\"\nexport PATH=\"{bin}:$PATH\"\n";
        var profilePath = Path.Combine(_home, profile);
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        await File.WriteAllTextAsync(profilePath, script, TestContext.Current.CancellationToken);
        // Only the made-up home folder: never the real user's rc files.
        var environment = new Dictionary<string, string>
        {
            ["SHELL"] = shell!,
            ["HOME"] = _home,
            ["ZDOTDIR"] = _home,
            ["XDG_CONFIG_HOME"] = Path.Combine(_home, ".config"),
            ["XDG_DATA_HOME"] = Path.Combine(_home, ".local", "share"),
            ["PATH"] = "/usr/bin:/bin",
            ["LANG"] = "C.UTF-8",
        };
        var reader = new LoginShellReader(new NoTerminalLauncher(), TimeProvider.System, Host(environment), TimeSpan.FromSeconds(60));

        var result = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Outcome == LoginShellOutcome.Used, $"{result.Outcome}: {result.Detail}");
        var read = result.Environment!;
        Assert.Equal("from-profile", read["LOGIN_SHELL_TEST"]);
        Assert.Equal("first\nsecond", read["LOGIN_SHELL_MULTI"]);
        Assert.Equal("a=b", read["LOGIN_SHELL_EQUALS"]);
        Assert.Equal("1", read["SAW_RESOLVING"]);
        Assert.StartsWith(bin + ":", read["PATH"], StringComparison.Ordinal);
        Assert.Equal(_home, read["HOME"]);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private LoginShellHost Host(Dictionary<string, string> environment) =>
        new(IsWindows: false, IsMacOS: true, environment, HasControllingTerminal: () => false, HomeDirectory: _home);

    private LoginShellReader Reader(IProcessLauncher launcher, TimeProvider time, Dictionary<string, string> environment) =>
        new(launcher, time, Host(environment)) { NewMarker = () => Marker };

    /// <summary>Writes <paramref name="text"/> a line at a time, as the real launcher reads it.</summary>
    private static void Write(FakeRunningProcess process, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            process.WriteOutput(line);
        }
    }

    private sealed class ThrowingLauncher(Exception exception) : IProcessLauncher
    {
        public IRunningProcess Start(ProcessStartSpec spec) => throw exception;
    }

    /// <summary>
    /// Runs the shell in a session of its own where <c>setsid</c> exists, so an interactive shell can't take over the
    /// terminal the tests were started from. The app never runs one while it has a terminal.
    /// </summary>
    private sealed class NoTerminalLauncher : IProcessLauncher
    {
        private readonly ProcessLauncher _launcher = new();

        public IRunningProcess Start(ProcessStartSpec spec) =>
            _launcher.Start(File.Exists("/usr/bin/setsid") ? spec with { FileName = "/usr/bin/setsid", Arguments = ["--wait", spec.FileName, .. spec.Arguments] } : spec);
    }
}
