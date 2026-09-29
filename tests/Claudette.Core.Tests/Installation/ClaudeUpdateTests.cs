using Claudette.Core.Diffs;
using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Installation;

/// <summary>Claude Code update handling (DESIGN.md §12): reading <c>claude doctor</c>, picking the update command, checking.</summary>
public class ClaudeUpdateTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "doctor", "2.1.284", name));

    // ---- claude doctor -------------------------------------------------------------------------------------------

    [Fact]
    public void Reads_a_real_doctor_report()
    {
        var report = ClaudeDoctor.Parse(Fixture("native.txt"));

        Assert.Equal(ClaudeInstallType.Native, report.InstallType);
        Assert.Equal(new Version(2, 1, 284), report.Version);
        Assert.Null(report.PackageManager);
        Assert.Equal("not set", report.ConfigInstallMethod);
        Assert.Equal("disabled (set by env: DISABLE_AUTOUPDATER)", report.AutoUpdates);
        Assert.False(report.AutoUpdatesEnabled);
        Assert.False(report.UpdatesBlocked);
        Assert.Equal("latest", report.Channel);
        Assert.Equal("none recorded", report.LastUpdateAttempt);
        Assert.Equal(3, report.Warnings.Count);
        Assert.Equal(new DoctorWarning("Running native installation but config install method is 'not set'", "Run claude install to update configuration"), report.Warnings[0]);
        Assert.Equal("Run: npm -g uninstall @anthropic-ai/claude-code", report.Warnings[2].Fix);
    }

    [Fact]
    public void Reads_a_package_manager_install_with_colors_and_crlf()
    {
        const string output = "Claude Code doctor\r\n\r\n\u001b[1mRunning:\u001b[22m package-manager (2.1.290)\r\nPackage manager: homebrew\r\nPath: /opt/homebrew/Caskroom/claude-code@latest/2.1.290/claude\r\nAuto-updates: Managed by package manager\r\nAuto-update channel: latest\r\nLast update attempt: success → 2.1.290 (2026-09-30)\r\n";

        var report = ClaudeDoctor.Parse(output);

        Assert.Equal(ClaudeInstallType.PackageManager, report.InstallType);
        Assert.Equal("homebrew", report.PackageManager);
        Assert.Equal(new Version(2, 1, 290), report.Version);
        Assert.Equal("success → 2.1.290 (2026-09-30)", report.LastUpdateAttempt);
        Assert.Empty(report.Warnings);
    }

    [Theory]
    [InlineData("native", ClaudeInstallType.Native)]
    [InlineData("npm-global", ClaudeInstallType.NpmGlobal)]
    [InlineData("npm-local", ClaudeInstallType.NpmLocal)]
    [InlineData("package-manager", ClaudeInstallType.PackageManager)]
    [InlineData("development", ClaudeInstallType.Development)]
    [InlineData("something-new", ClaudeInstallType.Unknown)]
    public void Maps_install_types_tolerantly(string text, ClaudeInstallType expected)
    {
        Assert.Equal(expected, ClaudeDoctor.Parse($"Running: {text} (2.1.284)").InstallType);
    }

    [Fact]
    public void Unreadable_output_gives_an_empty_report()
    {
        var report = ClaudeDoctor.Parse("error: unknown command 'doctor'");

        Assert.Equal(ClaudeInstallType.Unknown, report.InstallType);
        Assert.Null(report.Version);
    }

    [Fact]
    public void DISABLE_UPDATES_blocks_updating()
    {
        var report = ClaudeDoctor.Parse("Running: native (2.1.284)\nAuto-updates: disabled (set by env: DISABLE_UPDATES)");
        var plan = ClaudeUpdatePlan.For(report, "/bin/claude", null, new Probe());

        Assert.True(report.UpdatesBlocked);
        Assert.Equal(ClaudeUpdateKind.None, plan.Kind);
        Assert.False(plan.CanRun);
        Assert.Contains("DISABLE_UPDATES", plan.Note);
    }

    // ---- Picking the update command (DESIGN.md §12 table) ------------------------------------------------------

    [Theory]
    [InlineData("native", "Native installer")]
    [InlineData("npm-global", "npm (global)")]
    [InlineData("something-new", "something-new")]
    public void Native_and_npm_run_claude_update(string installType, string method)
    {
        var plan = ClaudeUpdatePlan.For(ClaudeDoctor.Parse($"Running: {installType} (2.1.284)"), "/home/me/.local/bin/claude", null, new Probe());

        Assert.Equal(ClaudeUpdateKind.SelfUpdate, plan.Kind);
        Assert.Equal(method, plan.Method);
        Assert.Equal("/home/me/.local/bin/claude", plan.Command!.FileName);
        Assert.Equal(["update"], plan.Command.Arguments);
        Assert.Null(plan.Check);
        Assert.False(plan.NeedsClaudeStopped);
    }

    [Fact]
    public void A_failed_doctor_still_offers_claude_update()
    {
        var plan = ClaudeUpdatePlan.For(null, "/bin/claude", null, new Probe());

        Assert.Equal(ClaudeUpdateKind.SelfUpdate, plan.Kind);
    }

    [Fact]
    public void Homebrew_upgrades_the_installed_cask_with_the_brew_beside_it()
    {
        var report = ClaudeDoctor.Parse("Running: package-manager (2.1.284)\nPackage manager: homebrew\nPath: /opt/homebrew/Caskroom/claude-code@latest/2.1.284/claude");
        var plan = ClaudeUpdatePlan.For(report, "/opt/homebrew/bin/claude", null, new Probe("/opt/homebrew/bin/brew"));

        Assert.Equal(ClaudeUpdateKind.Homebrew, plan.Kind);
        Assert.Equal("Homebrew (claude-code@latest)", plan.Method);
        Assert.Equal("/opt/homebrew/bin/brew", plan.Command!.FileName);
        Assert.Equal(["upgrade", "claude-code@latest"], plan.Command.Arguments);
        Assert.Equal(["outdated", "--cask", "--greedy", "--json=v2", "claude-code@latest"], plan.Check!.Arguments);
        Assert.Equal("brew upgrade claude-code@latest", plan.CommandText);
    }

    [Fact]
    public void Homebrew_finds_the_cask_from_the_resolved_launcher()
    {
        var report = ClaudeDoctor.Parse("Running: package-manager (2.1.284)\nPackage manager: homebrew\nPath: /usr/local/bin/claude");
        var plan = ClaudeUpdatePlan.For(report, "/usr/local/bin/claude", "/usr/local/Caskroom/claude-code/2.1.284/claude", new Probe("/usr/local/bin/brew"));

        Assert.Equal(["upgrade", "claude-code"], plan.Command!.Arguments);
        Assert.Equal("/usr/local/bin/brew", plan.Command.FileName);
    }

    [Fact]
    public void Homebrew_without_brew_shows_the_command()
    {
        var report = ClaudeDoctor.Parse("Running: package-manager (2.1.284)\nPackage manager: homebrew");
        var plan = ClaudeUpdatePlan.For(report, "/usr/local/bin/claude", null, new Probe());

        Assert.Equal(ClaudeUpdateKind.Manual, plan.Kind);
        Assert.Equal("brew upgrade claude-code", plan.ManualCommand);
    }

    [Fact]
    public void WinGet_upgrades_the_package_and_needs_claude_stopped()
    {
        var report = ClaudeDoctor.Parse("Running: package-manager (2.1.284)\nPackage manager: winget");
        var plan = ClaudeUpdatePlan.For(report, @"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\claude.exe", null, new Probe(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\winget.exe"));

        Assert.Equal(ClaudeUpdateKind.WinGet, plan.Kind);
        Assert.True(plan.NeedsClaudeStopped);
        Assert.Equal(["upgrade", "--id", "Anthropic.ClaudeCode", "--exact", "--accept-source-agreements", "--accept-package-agreements"], plan.Command!.Arguments);
        Assert.Equal(["list", "--id", "Anthropic.ClaudeCode", "--exact", "--upgrade-available", "--accept-source-agreements"], plan.Check!.Arguments);
    }

    [Theory]
    [InlineData("deb", "apt", "sudo apt update && sudo apt upgrade claude-code")]
    [InlineData("rpm", "dnf", "sudo dnf upgrade claude-code")]
    [InlineData("apk", "apk", "apk update && apk upgrade claude-code")]
    public void Linux_package_managers_show_the_command_and_run_nothing(string packageManager, string method, string command)
    {
        var report = ClaudeDoctor.Parse($"Running: package-manager (2.1.284)\nPackage manager: {packageManager}");
        var plan = ClaudeUpdatePlan.For(report, "/usr/bin/claude", null, new Probe());

        Assert.Equal(ClaudeUpdateKind.Manual, plan.Kind);
        Assert.Equal(method, plan.Method);
        Assert.False(plan.CanRun);
        Assert.Equal(command, plan.CommandText);
    }

    [Fact]
    public void Other_package_managers_have_no_action()
    {
        var plan = ClaudeUpdatePlan.For(ClaudeDoctor.Parse("Running: package-manager (2.1.284)\nPackage manager: mise"), "/x/claude", null, new Probe());

        Assert.Equal(ClaudeUpdateKind.None, plan.Kind);
        Assert.Contains("mise", plan.Note);
    }

    // ---- Package manager and claude update output ---------------------------------------------------------------

    [Fact]
    public void Reads_brew_outdated()
    {
        const string json = """{"formulae":[],"casks":[{"name":"claude-code","installed_versions":["2.1.284"],"current_version":"2.1.290"}]}""";

        Assert.Equal(new Version(2, 1, 290), ClaudeUpdateOutput.ParseBrewOutdated(json, "claude-code"));
        Assert.Null(ClaudeUpdateOutput.ParseBrewOutdated("""{"formulae":[],"casks":[]}""", "claude-code"));
        Assert.Null(ClaudeUpdateOutput.ParseBrewOutdated("Error: not json", "claude-code"));
    }

    [Fact]
    public void Reads_winget_list()
    {
        const string output = "   - \r   \\ \r\nName        Id                   Version Available Source\r\n-------------------------------------------------------\r\nClaude Code Anthropic.ClaudeCode 2.1.284 2.1.290   winget\r\n1 upgrades available.\r\n";

        Assert.Equal(new Version(2, 1, 290), ClaudeUpdateOutput.ParseWinGetList(output));
        Assert.Null(ClaudeUpdateOutput.ParseWinGetList("No installed package found matching input criteria."));
    }

    [Theory]
    [InlineData("Current version: 2.1.284\nSuccessfully updated from 2.1.284 to version 2.1.290", "2.1.290")]
    [InlineData("Claude Code is up to date (2.1.290)", "2.1.290")]
    [InlineData("Claude is up to date!", null)]
    public void Reads_claude_update(string output, string? expected)
    {
        Assert.Equal(expected is null ? null : Version.Parse(expected), ClaudeUpdateOutput.ParseClaudeUpdate(output));
    }

    // ---- ClaudeUpdater ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Check_runs_version_and_doctor_in_the_neutral_folder()
    {
        using var temp = new TempFolder();
        var launcher = new FakeProcessLauncher
        {
            Respond = spec => spec.Arguments switch
            {
                ["--version"] => new ProcessResult(0, "2.1.290 (Claude Code)\n", ""),
                ["doctor"] => new ProcessResult(0, Fixture("native.txt"), ""),
                _ => new ProcessResult(1, "", "unexpected"),
            },
        };
        var updater = new ClaudeUpdater("/bin/claude", temp.Path, launcher, new FakeTimeProvider(), new Probe());

        var check = await updater.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(check.Error);
        Assert.Equal(new Version(2, 1, 290), check.InstalledVersion);
        Assert.Equal(ClaudeInstallType.Native, check.Doctor!.InstallType);
        Assert.Equal(ClaudeUpdateKind.SelfUpdate, check.Plan.Kind);
        Assert.Null(check.AvailableVersion);
        Assert.All(launcher.Started, s => Assert.Equal(temp.Path, s.WorkingDirectory));
        Assert.All(launcher.Started, s => Assert.NotNull(s.Environment));
    }

    [Fact]
    public async Task Check_asks_homebrew_for_a_newer_version()
    {
        using var temp = new TempFolder();
        var launcher = new FakeProcessLauncher
        {
            Respond = spec => spec.Arguments switch
            {
                ["--version"] => new ProcessResult(0, "2.1.284 (Claude Code)", ""),
                ["doctor"] => new ProcessResult(0, "Running: package-manager (2.1.284)\nPackage manager: homebrew\nPath: /opt/homebrew/Caskroom/claude-code/2.1.284/claude", ""),
                ["outdated", ..] => new ProcessResult(0, """{"casks":[{"name":"claude-code","current_version":"2.1.290"}]}""", ""),
                _ => new ProcessResult(1, "", ""),
            },
        };
        var updater = new ClaudeUpdater("/opt/homebrew/bin/claude", temp.Path, launcher, new FakeTimeProvider(), new Probe("/opt/homebrew/bin/brew"));

        var check = await updater.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Version(2, 1, 290), check.AvailableVersion);
        Assert.Equal("/opt/homebrew/bin/brew", launcher.Started[^1].FileName);
    }

    [Fact]
    public async Task A_failing_version_command_is_reported()
    {
        using var temp = new TempFolder();
        var launcher = new FakeProcessLauncher { Respond = _ => new ProcessResult(1, "", "boom") };
        var updater = new ClaudeUpdater("/bin/claude", temp.Path, launcher, new FakeTimeProvider(), new Probe());

        var check = await updater.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(check.InstalledVersion);
        Assert.NotNull(check.Error);
    }

    [Fact]
    public async Task Update_streams_output_and_reports_the_new_version()
    {
        using var temp = new TempFolder();
        var updated = false;
        var launcher = new FakeProcessLauncher
        {
            Respond = spec => spec.Arguments switch
            {
                ["update"] => Updated(),
                ["--version"] => new ProcessResult(0, updated ? "2.1.290 (Claude Code)" : "2.1.284 (Claude Code)", ""),
                _ => new ProcessResult(1, "", ""),
            },
        };
        var updater = new ClaudeUpdater("/bin/claude", temp.Path, launcher, new FakeTimeProvider(), new Probe());
        var plan = ClaudeUpdatePlan.For(ClaudeDoctor.Parse("Running: native (2.1.284)"), "/bin/claude", null, new Probe());
        var lines = new List<string>();

        var result = await updater.UpdateAsync(plan, line => { lock (lines) { lines.Add(line); } }, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(new Version(2, 1, 290), result.Version);
        Assert.Contains("Successfully updated from 2.1.284 to version 2.1.290", lines);

        ProcessResult Updated()
        {
            updated = true;
            return new ProcessResult(0, "Current version: 2.1.284\nSuccessfully updated from 2.1.284 to version 2.1.290", "");
        }
    }

    [Fact]
    public async Task A_failed_update_says_why()
    {
        using var temp = new TempFolder();
        var launcher = new FakeProcessLauncher
        {
            Respond = spec => spec.Arguments is ["update"]
                ? new ProcessResult(1, "", "Error: Failed to install native update")
                : new ProcessResult(0, "2.1.284 (Claude Code)", ""),
        };
        var updater = new ClaudeUpdater("/bin/claude", temp.Path, launcher, new FakeTimeProvider(), new Probe());
        var plan = ClaudeUpdatePlan.For(null, "/bin/claude", null, new Probe());

        var result = await updater.UpdateAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("Failed to install native update", result.Message);
    }

    [Fact]
    public async Task A_manual_plan_runs_nothing()
    {
        using var temp = new TempFolder();
        var launcher = new FakeProcessLauncher();
        var updater = new ClaudeUpdater("/bin/claude", temp.Path, launcher, new FakeTimeProvider(), new Probe());
        var plan = ClaudeUpdatePlan.For(ClaudeDoctor.Parse("Running: package-manager (2.1.284)\nPackage manager: deb"), "/usr/bin/claude", null, new Probe());

        var result = await updater.UpdateAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Empty(launcher.Started);
    }

    /// <summary>Files that "exist", found on PATH by name.</summary>
    private sealed class Probe(params string[] files) : IFileProbe
    {
        public bool FileExists(string path) => files.Contains(path);

        public string? FindOnPath(string fileName) => null;

        public string ExpandEnvironmentVariables(string path) => path.Replace("%LOCALAPPDATA%", @"C:\Users\me\AppData\Local", StringComparison.Ordinal);
    }
}
