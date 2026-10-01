using Claudette.Core.Installation;
using Claudette.Core.Processes;
using Claudette.IntegrationTests.Support;

namespace Claudette.IntegrationTests;

/// <summary>
/// Checking for and applying Claude Code updates with real processes (DESIGN.md §12): <c>fake-claude</c> plays the
/// install, and one test reads the real <c>claude doctor</c>. Nothing here ever updates the real Claude Code.
/// </summary>
public sealed class ClaudeUpdaterTests : IDisposable
{
    private readonly TempFolder _root = new("claudette-updates");

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task Checks_and_updates_a_native_install()
    {
        var versionFile = _root.Combine("version.txt");
        await File.WriteAllTextAsync(versionFile, "2.1.284", TestContext.Current.CancellationToken);
        var updater = FakeUpdater(new Dictionary<string, string?>
        {
            ["FAKE_CLAUDE_VERSION_FILE"] = versionFile,
            ["FAKE_CLAUDE_UPDATE_TO"] = "2.1.290",
        });

        var before = await updater.CheckAsync(TestContext.Current.CancellationToken);
        var lines = new List<string>();
        var result = await updater.UpdateAsync(before.Plan, line => { lock (lines) { lines.Add(line); } }, TestContext.Current.CancellationToken);
        var after = await updater.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(before.Error);
        Assert.Equal(new Version(2, 1, 284), before.InstalledVersion);
        Assert.Equal(ClaudeInstallType.Native, before.Doctor!.InstallType);
        Assert.Equal(ClaudeUpdateKind.SelfUpdate, before.Plan.Kind);
        Assert.Equal([new DoctorWarning("This is fake-claude", "Nothing to fix")], before.Doctor.Warnings);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(new Version(2, 1, 290), result.Version);
        Assert.Contains("Successfully updated from 2.1.284 to version 2.1.290", lines);
        Assert.Equal(new Version(2, 1, 290), after.InstalledVersion);
    }

    [Fact]
    public async Task A_failing_update_is_reported()
    {
        var updater = FakeUpdater(new Dictionary<string, string?> { ["FAKE_CLAUDE_UPDATE_FAIL"] = "1" });

        var check = await updater.CheckAsync(TestContext.Current.CancellationToken);
        var result = await updater.UpdateAsync(check.Plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("Failed to install native update", result.Message);
        Assert.Equal(new Version(2, 1, 284), result.Version);
    }

    [Fact]
    public async Task Updates_turned_off_offer_no_update()
    {
        var updater = FakeUpdater(new Dictionary<string, string?> { ["FAKE_CLAUDE_AUTO_UPDATES"] = "disabled (set by env: DISABLE_UPDATES)" });

        var check = await updater.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ClaudeUpdateKind.None, check.Plan.Kind);
        Assert.Equal(new Version(2, 1, 284), check.InstalledVersion);
    }

    /// <summary>
    /// The real <c>claude doctor</c> still prints what Claudette reads. Read-only: it never runs <c>claude update</c>.
    /// </summary>
    [Fact]
    [Trait("Category", "RealCli")]
    public async Task Reads_the_real_claude_doctor()
    {
        var located = await new ClaudeLocator(new ProcessLauncher(), TimeProvider.System).LocateAsync(null, TestContext.Current.CancellationToken);
        RealCli.SkipUnlessInstalled(located.IsUsable);
        var config = _root.Combine("config");
        Directory.CreateDirectory(config);
        var updater = new ClaudeUpdater(located.Install!.Path, _root.Combine("work"), new ProcessLauncher(), TimeProvider.System, environmentOverrides: new Dictionary<string, string?>
        {
            ["CLAUDE_CONFIG_DIR"] = config,
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
        });

        var check = await updater.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(check.Error);
        Assert.NotNull(check.Doctor);
        Assert.Equal(located.Install.Version, check.InstalledVersion);
        Assert.Equal(located.Install.Version, check.Doctor.Version);
        Assert.NotEqual(ClaudeInstallType.Unknown, check.Doctor.InstallType);
        Assert.NotNull(check.Doctor.AutoUpdates);
        Assert.NotNull(check.Doctor.Channel);
    }

    private ClaudeUpdater FakeUpdater(Dictionary<string, string?> environment) =>
        new(FakeClaude.Path, _root.Combine("work"), new ProcessLauncher(), TimeProvider.System, environmentOverrides: environment);
}
