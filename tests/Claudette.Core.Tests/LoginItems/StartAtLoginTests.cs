using System.Text.Json;
using Claudette.Core.LoginItems;
using Claudette.Core.Settings;
using Claudette.Core.Tests.Support;
using Claudette.Core.Updates;

namespace Claudette.Core.Tests.LoginItems;

/// <summary>
/// Starting Claudette at login (DESIGN.md §9, "Starting at login"): which copy the entry starts when a machine has an
/// installed release, other builds and source builds, and how each copy turns it on, off and tidies it up.
/// </summary>
public sealed class StartAtLoginTests
{
    private static readonly ClaudetteCopy SourceBuild = new(AppInstallKind.SourceBuild, Path.Combine("D:", "src", "Claudette", "bin"), "0.3.0");
    private static readonly ClaudetteCopy OtherCheckout = new(AppInstallKind.SourceBuild, Path.Combine("D:", "src", "Claudette-2", "bin"), "0.3.0");
    private static readonly ClaudetteCopy Msix = new(AppInstallKind.Msix, "reapazor.Claudette_1a2b3c4d5e6f7", "0.2.0");
    private static readonly ClaudetteCopy MacApp = new(AppInstallKind.MacApp, "/Applications/Claudette.app", "0.2.0");
    private static readonly ClaudetteCopy Published = new(AppInstallKind.Other, Path.Combine("C:", "Tools", "Claudette"), "0.2.0");

    private readonly FakeLoginItems _items = new();
    private readonly LoginItemRecord _record = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private StartAtLogin As(ClaudetteCopy copy) => new(_items, copy, _record, save: () => { });

    [Fact]
    public async Task A_source_build_with_nothing_installed_starts_itself()
    {
        var status = await As(SourceBuild).SetAsync(true, Ct);

        Assert.Equal(new LoginItemStatus(true, true, SourceBuild), status);
        Assert.Equal(SourceBuild, _items.EntryStarts);
        Assert.Equal(SourceBuild, _record.Target);
        Assert.Null(As(SourceBuild).HandOverAtLogin());

        status = await As(SourceBuild).SetAsync(false, Ct);

        Assert.Equal(new LoginItemStatus(false, true, SourceBuild), status);
        Assert.Equal(LoginEntryState.Missing, _items.Entry);
        Assert.Null(_record.Target);
    }

    [Fact]
    public async Task A_source_build_starts_the_installed_app_instead_of_itself()
    {
        _items.Install(MacApp);
        _record.Installed = MacApp;

        var status = await As(SourceBuild).SetAsync(true, Ct);

        Assert.True(status.IsOn);
        Assert.Equal(MacApp, status.Starts);
        Assert.Equal(MacApp, _items.EntryStarts);
        // Had the entry still started the source build, it would hand over to the app at login.
        Assert.Equal(MacApp, As(SourceBuild).HandOverAtLogin());
    }

    [Fact]
    public async Task A_source_build_hands_over_to_the_MSIX_at_login_until_the_MSIX_turns_on_its_own_task()
    {
        _items.Install(Msix);
        _record.Installed = Msix;

        // Only the MSIX can turn its task on, so the entry starts the source build, which hands over at login.
        var status = await As(SourceBuild).SetAsync(true, Ct);
        Assert.Equal(new LoginItemStatus(true, true, Msix), status);
        Assert.Equal(SourceBuild, _items.EntryStarts);
        Assert.Equal(Msix, As(SourceBuild).HandOverAtLogin());

        // The MSIX starts: its task takes over from the entry.
        _items.MsixTask = new FakePackageTask();
        await As(Msix).RefreshAtLaunchAsync(Ct);
        Assert.Equal(PackageTaskState.Enabled, _items.MsixTask.State);
        Assert.Equal(LoginEntryState.Missing, _items.Entry);
        Assert.True(_record.InstalledTaskEnabled);
        Assert.Null(_record.Target);

        // Back in the source build, which can't turn the MSIX's task off.
        _items.MsixTask = null;
        status = await As(SourceBuild).ReadAsync(Ct);
        Assert.True(status.IsOn);
        Assert.False(status.CanChange);
        Assert.Equal(Msix, status.Starts);
        Assert.Equal("The installed Claudette 0.2.0 starts at login. Turn it off in that Claudette, or in Task Manager's Startup apps.", status.Note);
    }

    [Fact]
    public async Task The_MSIX_turns_its_own_task_on_and_off_and_notes_it_for_source_builds()
    {
        _items.MsixTask = new FakePackageTask();
        await As(Msix).RefreshAtLaunchAsync(Ct);
        Assert.Equal(Msix, _record.Installed);

        var status = await As(Msix).SetAsync(true, Ct);
        Assert.Equal(new LoginItemStatus(true, true, Msix), status);
        Assert.True(_record.InstalledTaskEnabled);
        Assert.Equal(0, _items.Writes);

        status = await As(Msix).SetAsync(false, Ct);
        Assert.Equal(new LoginItemStatus(false, true, Msix), status);
        Assert.Equal(PackageTaskState.Disabled, _items.MsixTask.State);
        Assert.False(_record.InstalledTaskEnabled);
    }

    [Theory]
    [InlineData(PackageTaskState.DisabledByUser, false, "Turned off in Task Manager's Startup apps. Turn it on there.")]
    [InlineData(PackageTaskState.DisabledByPolicy, false, "Turned off by your organization's policy.")]
    [InlineData(PackageTaskState.EnabledByPolicy, true, "Turned on by your organization's policy.")]
    public async Task The_MSIX_says_when_its_task_is_held_on_or_off_outside_Claudette(PackageTaskState state, bool on, string note)
    {
        _items.MsixTask = new FakePackageTask(state);

        var status = await As(Msix).SetAsync(!on, Ct);

        Assert.Equal(new LoginItemStatus(on, false, Msix, note), status);
        Assert.Equal(state, _items.MsixTask.State);
    }

    [Fact]
    public async Task An_entry_turned_off_in_Task_Manager_is_off_and_turned_on_there()
    {
        _items.Entry = LoginEntryState.DisabledByUser;

        var status = await As(SourceBuild).ReadAsync(Ct);

        Assert.Equal(new LoginItemStatus(false, false, SourceBuild, "Turned off in Task Manager's Startup apps. Turn it on there."), status);
    }

    [Fact]
    public async Task An_installed_Claudette_notes_itself_and_takes_over_a_source_builds_entry()
    {
        _items.Install(SourceBuild);
        _items.Entry = LoginEntryState.Enabled;
        _record.Target = SourceBuild;

        await As(MacApp).RefreshAtLaunchAsync(Ct);

        Assert.Equal(MacApp, _record.Installed);
        Assert.Equal(MacApp, _items.EntryStarts);
        Assert.Equal(MacApp, _record.Target);
    }

    [Fact]
    public async Task An_entry_whose_copy_is_gone_starts_this_one_instead()
    {
        _items.Entry = LoginEntryState.Enabled;
        _record.Target = OtherCheckout;

        await As(SourceBuild).RefreshAtLaunchAsync(Ct);

        Assert.Equal(SourceBuild, _items.EntryStarts);
        Assert.Equal(SourceBuild, _record.Target);
    }

    [Fact]
    public async Task A_source_build_leaves_another_checkouts_entry_alone()
    {
        _items.Install(OtherCheckout);
        _items.Entry = LoginEntryState.Enabled;
        _record.Target = OtherCheckout;

        await As(SourceBuild).RefreshAtLaunchAsync(Ct);

        Assert.Equal(0, _items.Writes);
        Assert.Equal(OtherCheckout, _record.Target);
        Assert.Equal(OtherCheckout, (await As(SourceBuild).ReadAsync(Ct)).Starts);
        Assert.Null(As(SourceBuild).HandOverAtLogin());
    }

    [Fact]
    public async Task Another_build_is_noted_only_when_no_installed_release_is_still_here()
    {
        _items.Install(Msix);
        _record.Installed = Msix;

        await As(Published).RefreshAtLaunchAsync(Ct);
        Assert.Equal(Msix, _record.Installed);

        _items.Uninstall(Msix);
        await As(Published).RefreshAtLaunchAsync(Ct);
        Assert.Equal(Published, _record.Installed);
    }

    [Fact]
    public async Task Refreshing_never_turns_it_on()
    {
        _items.Install(MacApp);
        _record.Installed = MacApp;

        await As(SourceBuild).RefreshAtLaunchAsync(Ct);
        await As(MacApp).RefreshAtLaunchAsync(Ct);

        Assert.Equal(LoginEntryState.Missing, _items.Entry);
        Assert.Equal(0, _items.Writes);
    }

    [Fact]
    public async Task Nothing_changes_where_Claudette_cant_start_at_login()
    {
        _items.UnavailableReason = "Not while CLAUDETTE_HOME is set.";

        var status = await As(SourceBuild).SetAsync(true, Ct);

        Assert.Equal(new LoginItemStatus(false, false, null, "Not while CLAUDETTE_HOME is set."), status);
        Assert.Equal(0, _items.Writes);
        Assert.Null(As(SourceBuild).HandOverAtLogin());
    }

    [Fact]
    public async Task A_change_that_fails_says_why()
    {
        _items.WriteFailure = new UnauthorizedAccessException("Access to the registry key is denied.");

        var status = await As(SourceBuild).SetAsync(true, Ct);

        Assert.False(status.IsOn);
        Assert.True(status.CanChange);
        Assert.Equal("Couldn't turn it on: Access to the registry key is denied.", status.Error);
    }

    [Fact]
    public void The_record_is_kept_in_the_machines_state()
    {
        var state = new AppState();
        state.LoginItem.Target = SourceBuild;
        state.LoginItem.Installed = Msix;
        state.LoginItem.InstalledTaskEnabled = true;

        var json = JsonFileStore<AppState>.Serialize(state);
        var read = JsonSerializer.Deserialize<AppState>(json, JsonFileStore<AppState>.Options)!;

        Assert.Contains("\"kind\": \"sourceBuild\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"rank\"", json, StringComparison.Ordinal);
        Assert.Equal(SourceBuild, read.LoginItem.Target);
        Assert.Equal(Msix, read.LoginItem.Installed);
        Assert.True(read.LoginItem.InstalledTaskEnabled);
    }

    [Fact]
    public void Releases_are_preferred_to_other_builds_and_those_to_source_builds()
    {
        Assert.True(Msix.Rank > Published.Rank);
        Assert.True(MacApp.Rank > Published.Rank);
        Assert.True(Published.Rank > SourceBuild.Rank);
        Assert.True(Msix.IsSameCopy(Msix with { Version = "0.4.0" }));
        Assert.False(SourceBuild.IsSameCopy(OtherCheckout));
    }
}
