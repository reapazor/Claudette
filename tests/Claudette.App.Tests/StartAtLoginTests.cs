using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.LoginItems;
using Claudette.Core.Updates;

namespace Claudette.App.Tests;

/// <summary>
/// Settings → General's <b>Start Claudette when I log in</b>, and launches with <c>--login</c> (DESIGN.md §9, "Starting
/// at login"). The login entry is the harness's fake.
/// </summary>
public sealed class StartAtLoginTests
{
    private static readonly ClaudetteCopy MacApp = new(AppInstallKind.MacApp, "/Applications/Claudette.app", "0.2.0");
    private static readonly ClaudetteCopy Msix = new(AppInstallKind.Msix, "reapazor.Claudette_1a2b3c4d5e6f7", "0.2.0");

    private static ClaudetteCopy SourceBuild(TabTestHarness h) => new(AppInstallKind.SourceBuild, Path.Combine(h.Root, "bin"), "0.3.0");

    [Fact]
    public async Task The_switch_turns_starting_at_login_on_and_off()
    {
        await using var h = new TabTestHarness();
        h.Services.ThisCopy = SourceBuild(h);
        var settings = new SettingsViewModel(h.Services, null);
        Assert.False(settings.General.StartsAtLogin);
        Assert.True(settings.General.CanChangeStartAtLogin);
        Assert.Equal(
            "Claudette opens minimized when you log in to this computer. It starts this source build's newest build. Once Claudette is installed, the installed one starts instead.",
            settings.General.StartAtLoginText);

        settings.General.StartsAtLogin = true;

        Assert.True(settings.General.StartsAtLogin);
        Assert.Equal(h.Services.ThisCopy, h.LoginItems.EntryStarts);
        Assert.Equal(h.Services.ThisCopy, h.Services.State.LoginItem.Target);

        settings.General.StartsAtLogin = false;

        Assert.False(settings.General.StartsAtLogin);
        Assert.Equal(LoginEntryState.Missing, h.LoginItems.Entry);
    }

    [Fact]
    public async Task A_source_build_starts_the_installed_Claudette_and_says_so()
    {
        await using var h = new TabTestHarness();
        h.Services.ThisCopy = SourceBuild(h);
        h.LoginItems.Install(MacApp);
        h.Services.State.LoginItem.Installed = MacApp;
        var settings = new SettingsViewModel(h.Services, null);

        settings.General.StartsAtLogin = true;

        Assert.Equal(MacApp, h.LoginItems.EntryStarts);
        Assert.Equal(
            "Claudette opens minimized when you log in to this computer. It starts the installed Claudette 0.2.0 rather than this source build.",
            settings.General.StartAtLoginText);
    }

    [Fact]
    public async Task A_source_build_cant_turn_off_the_MSIXs_own_task()
    {
        await using var h = new TabTestHarness();
        h.Services.ThisCopy = SourceBuild(h);
        h.LoginItems.Install(Msix);
        h.Services.State.LoginItem.Installed = Msix;
        h.Services.State.LoginItem.InstalledTaskEnabled = true;

        var settings = new SettingsViewModel(h.Services, null);

        Assert.True(settings.General.StartsAtLogin);
        Assert.False(settings.General.CanChangeStartAtLogin);
        Assert.Equal("The installed Claudette 0.2.0 starts at login. Turn it off in that Claudette, or in Task Manager's Startup apps.", settings.General.StartAtLoginNote);
    }

    [Fact]
    public async Task A_change_that_fails_says_why_and_the_switch_stays_off()
    {
        await using var h = new TabTestHarness();
        h.LoginItems.WriteFailure = new UnauthorizedAccessException("Access to the registry key is denied.");
        var settings = new SettingsViewModel(h.Services, null);

        settings.General.StartsAtLogin = true;

        Assert.False(settings.General.StartsAtLogin);
        Assert.True(settings.General.HasStartAtLoginError);
        Assert.Equal("Couldn't turn it on: Access to the registry key is denied.", settings.General.StartAtLoginError);
    }

    [Fact]
    public async Task Reset_to_defaults_leaves_it_as_it_is()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);
        settings.General.StartsAtLogin = true;

        settings.General.ResetCommand.Execute(null);

        Assert.True(settings.General.StartsAtLogin);
        Assert.Equal(LoginEntryState.Enabled, h.LoginItems.Entry);
    }

    [Fact]
    public async Task The_search_finds_it()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null) { SearchText = "log in" };

        Assert.Contains(settings.SearchResults, r => r is { Category: "General", Label: "Start Claudette when I log in" });
    }

    [Fact]
    public async Task A_login_launch_while_Claudette_runs_leaves_it_where_it_is()
    {
        await using var h = new TabTestHarness();
        var main = new MainWindowViewModel(h.Services);
        var broughtToFront = 0;
        main.BringToFrontRequested += () => broughtToFront++;

        main.OnLaunchedAgain([LaunchArguments.LoginOption]);
        Assert.Equal(0, broughtToFront);

        main.OnLaunchedAgain([]);
        Assert.Equal(1, broughtToFront);
    }

    [Fact]
    public void Login_is_kept_when_a_source_build_starts_its_copy()
    {
        string[] args = [LaunchArguments.LoginOption, LaunchArguments.SourceBuildOption, "bin/Debug"];

        Assert.True(LaunchArguments.IsLogin(args));
        Assert.False(LaunchArguments.IsLogin(["--folder", "work"]));
        Assert.Equal([LaunchArguments.LoginOption], LaunchArguments.WithoutDevelopmentOptions(args));
    }

    [Fact]
    public void A_source_build_is_its_build_output_rather_than_the_copy_it_runs_from()
    {
        var output = Path.GetFullPath(Path.Combine("checkout", "src", "Claudette.App", "bin", "Debug", "net10.0"));

        var copy = LoginLaunch.CurrentCopy([LaunchArguments.SourceBuildOption, output], new AppVersion(0, 3, 0));

        Assert.Equal(new ClaudetteCopy(AppInstallKind.SourceBuild, output, "0.3.0"), copy);
    }
}
