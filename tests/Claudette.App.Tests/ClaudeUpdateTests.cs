using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Installation;

namespace Claudette.App.Tests;

/// <summary>Claude Code updates in the app (DESIGN.md §12): the header badge, Update now, the tab note and scheduling.</summary>
public class ClaudeUpdateTests
{
    private static readonly Version Old = new(2, 1, 284);
    private static readonly Version New = new(2, 1, 290);

    [Fact]
    public async Task A_newer_installed_version_is_announced_while_a_tab_runs_the_old_one()
    {
        var updater = new FakeClaudeUpdater(Old);
        await using var h = new TabTestHarness(updater: updater);
        var updates = Updates(h);
        var tab = await h.OpenTabAsync();

        // The native installer updated itself in the background.
        updater.Installed = New;
        await h.Services.ClaudeUpdates!.CheckNowAsync();

        Assert.Equal(Old, tab.RunningVersion);
        Assert.Equal(New, updates.ReadyVersion);
        Assert.True(updates.IsReadyInstalled);
        Assert.True(updates.HasBadge);
        Assert.Equal("Claude Code 2.1.290 is ready", updates.BadgeText);
        Assert.Contains("open tabs keep running 2.1.284", updates.DetailText);
        Assert.True(updates.CanUpdateNow);
        Assert.Contains(tab.InfoRows, r => r is { Label: "Claude Code", Value: "Running 2.1.284; 2.1.290 is installed. New tabs use 2.1.290." });
    }

    [Fact]
    public async Task Nothing_is_announced_when_tabs_run_the_installed_version()
    {
        var updater = new FakeClaudeUpdater(Old);
        await using var h = new TabTestHarness(updater: updater);
        var updates = Updates(h);
        var tab = await h.OpenTabAsync();

        await h.Services.ClaudeUpdates!.CheckNowAsync();

        Assert.Null(updates.ReadyVersion);
        Assert.False(updates.HasBadge);
        Assert.Contains(tab.InfoRows, r => r is { Label: "Claude Code", Value: "2.1.284" });
        Assert.Equal("Up to date. Checked just now.", updates.CheckStatusText);
    }

    [Fact]
    public async Task The_badge_goes_away_when_the_old_tab_closes()
    {
        var updater = new FakeClaudeUpdater(Old);
        await using var h = new TabTestHarness(updater: updater);
        var updates = Updates(h);
        var tab = await h.OpenTabAsync();
        updater.Installed = New;
        await h.Services.ClaudeUpdates!.CheckNowAsync();

        await h.Shell.CloseTabCommand.ExecuteAsync(tab);
        await TabTestHarness.Eventually(() => !updates.HasBadge, "the badge to go");

        Assert.Empty(h.Shell.RunningVersions);
    }

    [Fact]
    public async Task A_package_manager_update_is_offered_and_applied()
    {
        var updater = new FakeClaudeUpdater(Old) { Kind = ClaudeUpdateKind.Homebrew, Available = New };
        await using var h = new TabTestHarness(updater: updater);
        var updates = Updates(h);
        await h.Services.ClaudeUpdates!.CheckNowAsync();

        Assert.Equal(New, updates.ReadyVersion);
        Assert.False(updates.IsReadyInstalled);
        Assert.Contains("available from Homebrew (claude-code). You have 2.1.284", updates.DetailText);
        // Older than the version Claudette was last tested with, so the badge says so (DESIGN.md §16).
        Assert.EndsWith($"Claudette was last tested with {ClaudeLocator.LastTestedVersion}.", updates.DetailText);
        Assert.True(updates.CanUpdateNow);

        await updates.UpdateNowCommand.ExecuteAsync(null);

        Assert.Single(updater.Updates);
        Assert.Contains("Successfully updated from 2.1.284 to version 2.1.290", updates.Output);
        Assert.Equal("Claude Code 2.1.290 is installed. New tabs use it.", updates.ResultText);
        Assert.Equal(New, h.Services.InstalledClaudeVersion);
        Assert.False(updates.HasBadge);
    }

    [Fact]
    public async Task Settings_says_whether_the_installed_version_is_older_or_newer_than_the_last_tested_one()
    {
        await using var h = new TabTestHarness();

        h.Services.UseInstall(new ClaudeInstall("claude", Old));
        Assert.EndsWith($"Last tested with {ClaudeLocator.LastTestedVersion} (this version is older; updating is recommended).",
            new SettingsViewModel(h.Services, null).ClaudeCode.InstalledText);

        h.Services.UseInstall(new ClaudeInstall("claude", ClaudeLocator.LastTestedVersion));
        Assert.EndsWith($"Last tested with {ClaudeLocator.LastTestedVersion}.", new SettingsViewModel(h.Services, null).ClaudeCode.InstalledText);

        h.Services.UseInstall(new ClaudeInstall("claude", new Version(ClaudeLocator.LastTestedVersion.Major, ClaudeLocator.LastTestedVersion.Minor + 1, 0)));
        Assert.EndsWith("(this version is newer).", new SettingsViewModel(h.Services, null).ClaudeCode.InstalledText);
    }

    [Fact]
    public async Task A_failed_update_says_why()
    {
        var updater = new FakeClaudeUpdater(Old) { UpdateFails = true };
        await using var h = new TabTestHarness(updater: updater);
        var updates = Updates(h);
        await h.Services.ClaudeUpdates!.CheckNowAsync();

        await updates.UpdateNowCommand.ExecuteAsync(null);

        Assert.Contains("Failed to install native update", updates.ResultText);
        Assert.Equal(Old, h.Services.InstalledClaudeVersion);
    }

    [Fact]
    public async Task WinGet_waits_for_the_next_launch_while_a_tab_runs()
    {
        var updater = new FakeClaudeUpdater(Old) { Kind = ClaudeUpdateKind.WinGet, Available = New };
        await using var h = new TabTestHarness(updater: updater);
        var updates = Updates(h);
        await h.OpenTabAsync();
        await h.Services.ClaudeUpdates!.CheckNowAsync();

        Assert.False(updates.CanUpdateNow);
        Assert.True(updates.CanUpdateOnNextLaunch);
        Assert.Contains("WinGet can't replace Claude Code while a tab is running it", updates.Note);

        updates.UpdateOnNextLaunchCommand.Execute(null);

        Assert.True(h.Services.State.UpdateClaudeOnNextLaunch);
        Assert.True(updates.IsUpdateScheduled);

        updates.CancelUpdateOnNextLaunchCommand.Execute(null);

        Assert.False(h.Services.State.UpdateClaudeOnNextLaunch);
    }

    [Fact]
    public async Task WinGet_updates_now_when_no_tab_runs()
    {
        var updater = new FakeClaudeUpdater(Old) { Kind = ClaudeUpdateKind.WinGet, Available = New };
        await using var h = new TabTestHarness(updater: updater);
        var updates = Updates(h);
        await h.Services.ClaudeUpdates!.CheckNowAsync();

        Assert.True(updates.CanUpdateNow);
        Assert.False(updates.CanUpdateOnNextLaunch);
    }

    [Fact]
    public async Task Linux_packages_show_the_command_to_run()
    {
        var updater = new FakeClaudeUpdater(Old) { Kind = ClaudeUpdateKind.Manual };
        await using var h = new TabTestHarness(updater: updater);
        var updates = Updates(h);
        await h.Services.ClaudeUpdates!.CheckNowAsync();

        Assert.False(updates.CanUpdateNow);
        Assert.Equal("sudo apt update && sudo apt upgrade claude-code", updates.ManualCommand);

        await updates.CopyManualCommandCommand.ExecuteAsync(null);

        Assert.Equal("sudo apt update && sudo apt upgrade claude-code", h.Platform.Clipboard);
    }

    [Fact]
    public async Task Turned_off_updates_show_the_version_but_offer_nothing()
    {
        var updater = new FakeClaudeUpdater(Old) { Kind = ClaudeUpdateKind.None };
        await using var h = new TabTestHarness(updater: updater);
        var updates = Updates(h);
        await h.Services.ClaudeUpdates!.CheckNowAsync();

        Assert.False(updates.CanUpdateNow);
        Assert.False(updates.HasManualCommand);
        Assert.Contains("DISABLE_UPDATES", updates.Note);
        // Older than the version Claudette was last tested with: a nudge, not a requirement (DESIGN.md §16).
        Assert.Equal($"Claude Code 2.1.284, installed with: Native installer. Older than the last tested version ({ClaudeLocator.LastTestedVersion}); updating is recommended",
            updates.VersionText);
    }

    [Fact]
    public async Task Settings_shows_doctor_details_and_warnings()
    {
        var updater = new FakeClaudeUpdater(Old);
        await using var h = new TabTestHarness(updater: updater);
        var updates = Updates(h);
        await h.Services.ClaudeUpdates!.CheckNowAsync();
        var settings = new SettingsViewModel(h.Services, "me@example.com", updates);

        Assert.True(settings.ClaudeCode.HasUpdates);
        Assert.Equal("Auto-updates: enabled · channel: latest", updates.AutoUpdateText);
        Assert.Equal([new DoctorWarning("Something is off", "Run claude install")], updates.Warnings);
    }

    [Fact]
    public async Task Dismissing_hides_the_badge_until_a_newer_version()
    {
        var updater = new FakeClaudeUpdater(Old) { Kind = ClaudeUpdateKind.Homebrew, Available = New };
        await using var h = new TabTestHarness(updater: updater);
        var updates = Updates(h);
        await h.Services.ClaudeUpdates!.CheckNowAsync();

        updates.DismissCommand.Execute(null);
        Assert.False(updates.HasBadge);
        // Saved on this machine, so it stays dismissed after a restart.
        Assert.Equal("2.1.290", h.Services.State.DismissedClaudeUpdate);
        Assert.False(Updates(h).HasBadge);

        updater.Available = new Version(2, 1, 300);
        await h.Services.ClaudeUpdates.CheckNowAsync();
        Assert.True(updates.HasBadge);
    }

    [Fact]
    public async Task Checks_run_at_launch_and_every_few_hours_while_turned_on()
    {
        var updater = new FakeClaudeUpdater(Old);
        await using var h = new TabTestHarness(updater: updater);
        var service = h.Services.ClaudeUpdates!;

        service.Start();
        await TabTestHarness.Eventually(() => updater.Checks == 1, "the launch check");
        // A tick while a check is still finishing joins it instead of starting another, so let it finish first.
        await service.LastCheck!;
        h.Time.Advance(ClaudeUpdateService.CheckInterval);
        await TabTestHarness.Eventually(() => updater.Checks == 2, "the next check");
        await service.LastCheck!;

        h.Services.Settings.ClaudeCode.CheckForUpdates = false;
        h.Services.SaveSettings();
        h.Time.Advance(ClaudeUpdateService.CheckInterval * 2);
        // A tick starts its check at once, so a check it started is the last one.
        await service.LastCheck!;

        Assert.Equal(2, updater.Checks);
    }

    [Fact]
    public async Task The_changelog_opens()
    {
        var updater = new FakeClaudeUpdater(Old);
        await using var h = new TabTestHarness(updater: updater);

        await Updates(h).OpenChangelogCommand.ExecuteAsync(null);

        Assert.Equal([ClaudeUpdateService.ChangelogUrl], h.Platform.OpenedUrls);
    }

    [Fact]
    public async Task The_setup_screen_updates_a_version_that_is_too_old()
    {
        var rechecked = false;
        var setup = new SetupViewModel(
            new ClaudeLocateResult(new ClaudeInstall("claude", new Version(2, 1, 100)), ClaudeInstallProblem.TooOld, "Too old", "claude"),
            new NoPlatform(),
            _ => Task.CompletedTask,
            output =>
            {
                output("Successfully updated from 2.1.100 to version 2.1.290");
                return Task.FromResult(new ClaudeUpdateResult(true, New, "Claude Code 2.1.290 is installed."));
            },
            () =>
            {
                rechecked = true;
                return Task.CompletedTask;
            });

        Assert.True(setup.CanUpdate);
        await setup.UpdateNowCommand.ExecuteAsync(null);

        Assert.True(rechecked);
        Assert.Contains("Successfully updated", setup.Output);
        Assert.Equal("Claude Code 2.1.290 is installed.", setup.UpdateResult);
    }

    [Fact]
    public void The_setup_screen_offers_no_update_when_claude_is_missing()
    {
        var setup = new SetupViewModel(new ClaudeLocateResult(null, ClaudeInstallProblem.NotFound, "Not found"), new NoPlatform(), _ => Task.CompletedTask);

        Assert.False(setup.CanUpdate);
    }

    private static ClaudeUpdateViewModel Updates(TabTestHarness h)
    {
        var viewModel = new ClaudeUpdateViewModel(h.Services, h.Services.ClaudeUpdates!, () => h.Shell.RunningVersions);
        h.Shell.RunningVersionsChanged += viewModel.Refresh;
        return viewModel;
    }
}
