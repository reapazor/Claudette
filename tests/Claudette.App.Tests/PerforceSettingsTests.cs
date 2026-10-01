using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.Perforce;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>Settings → Perforce (DESIGN.md §18): off by default, and the password never in settings.json.</summary>
public class PerforceSettingsTests
{
    [Fact]
    public async Task Perforce_has_its_own_category_found_by_search()
    {
        await using var h = new TabTestHarness();
        var settings = new SettingsViewModel(h.Services, null);

        Assert.Contains("Perforce", settings.Categories);
        settings.SearchText = "changelist";

        Assert.Equal([("Perforce", "Show changelist on tabs")], settings.SearchResults.Select(r => (r.Category, r.Label)));
        Assert.Equal("Perforce", settings.SelectedCategory);
        Assert.Same(settings.Perforce, settings.CurrentPage);
    }

    [Fact]
    public async Task The_options_change_the_settings_and_reset_keeps_the_folder_overrides()
    {
        await using var h = new TabTestHarness();
        h.Platform.FolderToPick = h.WorkFolder;
        var settings = new SettingsViewModel(h.Services, null);
        var perforce = h.Services.Settings.Perforce;
        Assert.False(settings.Perforce.PerforceEnabled);
        Assert.Equal(30, settings.Perforce.PerforceRenewBeforeMinutes);
        Assert.Equal(PerforcePasswordSource.Stored, settings.Perforce.SelectedPasswordSource.Source);

        settings.Perforce.PerforceEnabled = true;
        settings.Perforce.SelectedPasswordSource = settings.Perforce.PerforcePasswordSources.Single(c => c.Source == PerforcePasswordSource.PerforceConfig);
        settings.Perforce.PerforceRenewBeforeMinutes = 45;
        settings.Perforce.PerforceAllHostsTickets = true;
        settings.Perforce.ShowChangelistOnTabs = true;
        await settings.Perforce.AddPerforceOverrideCommand.ExecuteAsync(null);
        settings.Perforce.PerforceOverrides.Single().Server = "other:1666";
        settings.Perforce.PerforceOverrides.Single().User = " build ";

        Assert.True(perforce.Enabled);
        Assert.True(settings.Perforce.IsPerforceConfigSource);
        Assert.Equal(PerforcePasswordSource.PerforceConfig, perforce.PasswordSource);
        Assert.Equal(45, perforce.RenewBeforeMinutes);
        Assert.True(perforce.AllHostsTickets && perforce.ShowChangelistOnTabs);
        Assert.Equal((h.WorkFolder, "other:1666", "build"), (perforce.FolderOverrides.Single().Folder, perforce.FolderOverrides.Single().Server, perforce.FolderOverrides.Single().User));
        Assert.Same(perforce.FolderOverrides.Single(), perforce.OverrideFor(Path.Combine(h.WorkFolder, "sub")));
        Assert.Equal(new PerforceTarget(h.WorkFolder, "other:1666", "build"), h.Services.Perforce.TargetFor(h.WorkFolder));

        settings.Perforce.ResetCommand.Execute(null);

        perforce = h.Services.Settings.Perforce;
        Assert.False(perforce.Enabled);
        Assert.Equal(PerforcePasswordSource.Stored, perforce.PasswordSource);
        Assert.Equal(30, perforce.RenewBeforeMinutes);
        Assert.False(perforce.ShowChangelistOnTabs);
        Assert.Single(perforce.FolderOverrides);

        settings.Perforce.RemovePerforceOverrideCommand.Execute(settings.Perforce.PerforceOverrides.Single());
        Assert.Empty(perforce.FolderOverrides);
        Assert.Null(perforce.OverrideFor(h.WorkFolder));
    }

    [Fact]
    public async Task A_saved_password_goes_to_the_credential_store_never_settings()
    {
        await using var h = new TabTestHarness();
        var store = new FakeCredentialStore();
        h.Services.Perforce.Credentials = store;
        h.Services.Perforce.AddKnownLogin("ssl:perforce:1666", "matt");
        var settings = new SettingsViewModel(h.Services, null);
        Assert.Equal(("ssl:perforce:1666", "matt"), (settings.Perforce.PerforcePasswordServer, settings.Perforce.PerforcePasswordUser));
        Assert.Equal("Test Keychain", settings.Perforce.CredentialStoreName);

        settings.Perforce.PerforcePassword = "s3cret";
        await settings.Perforce.SavePerforcePasswordCommand.ExecuteAsync(null);

        Assert.Equal("s3cret", store.Secrets["perforce/ssl:perforce:1666/matt"]);
        Assert.Equal("", settings.Perforce.PerforcePassword);
        Assert.Equal("Saved the password for matt @ ssl:perforce:1666 in Test Keychain.", settings.Perforce.PerforcePasswordStatus);
        Assert.DoesNotContain("s3cret", JsonFileStore<AppSettings>.Serialize(h.Services.Settings), StringComparison.Ordinal);

        await settings.Perforce.ForgetPerforcePasswordCommand.ExecuteAsync(null);
        Assert.Empty(store.Secrets);
        await settings.Perforce.ForgetPerforcePasswordCommand.ExecuteAsync(null);
        Assert.Equal("No password was saved for matt @ ssl:perforce:1666.", settings.Perforce.PerforcePasswordStatus);
    }

    [Fact]
    public async Task Without_a_credential_store_settings_say_why()
    {
        await using var h = new TabTestHarness();
        h.Services.Perforce.Credentials = new FakeCredentialStore { IsAvailable = false };
        var settings = new SettingsViewModel(h.Services, null);

        Assert.False(settings.Perforce.IsCredentialStoreAvailable);
        Assert.Equal("Not available in this test.", settings.Perforce.CredentialStoreUnavailableText);
    }

    [Fact]
    public async Task Perforce_settings_stay_on_this_machine()
    {
        await using var h = new TabTestHarness();
        h.Services.Settings.Perforce.Enabled = true;
        h.Services.Settings.Perforce.FolderOverrides.Add(new PerforceFolderOverride { Folder = h.WorkFolder, Server = "other:1666" });
        h.Services.Settings.Sessions.SyncSettings = true;

        await h.Services.Library.PublishAllSettingsAsync();

        var synced = await File.ReadAllTextAsync(h.Services.Library.Library.SettingsSyncFile, TestContext.Current.CancellationToken);
        Assert.Contains("appearance.theme", synced, StringComparison.Ordinal);
        Assert.DoesNotContain("perforce", synced, StringComparison.OrdinalIgnoreCase);
    }
}
