using System.Collections.ObjectModel;
using Claudette.App.Services;
using Claudette.Core.Credentials;
using Claudette.Core.Perforce;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A password source in Settings → Perforce.</summary>
public sealed record PerforcePasswordSourceChoice(PerforcePasswordSource Source, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One per-folder server and user override in Settings → Perforce.</summary>
public sealed class PerforceOverrideEditor(PerforceFolderOverride entry, Action changed) : ObservableObject
{
    public PerforceFolderOverride Entry { get; } = entry;

    public string Folder => Entry.Folder;

    public string Server
    {
        get => Entry.Server ?? "";
        set
        {
            Entry.Server = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            OnPropertyChanged();
            changed();
        }
    }

    public string User
    {
        get => Entry.User ?? "";
        set
        {
            Entry.User = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            OnPropertyChanged();
            changed();
        }
    }
}

/// <summary>Settings → Perforce (DESIGN.md §18). Off by default.</summary>
public sealed partial class SettingsViewModel
{
    private static IEnumerable<SettingsSearchResult> PerforceSearchEntries() =>
    [
        new("Perforce", "Keep Perforce logins fresh"),
        new("Perforce", "Password source"),
        new("Perforce", "Renew the ticket when less than this is left (minutes)"),
        new("Perforce", "Request tickets valid on all hosts"),
        new("Perforce", "Show changelist on tabs"),
        new("Perforce", "Save a Perforce password"),
        new("Perforce", "Forget the Perforce password"),
        new("Perforce", "Per-folder server and user"),
    ];

    private PerforceSettings Perforce => _settings.Perforce;

    public bool IsPerforce => SelectedCategory == "Perforce";

    public bool PerforceEnabled
    {
        get => Perforce.Enabled;
        set => Set(value, v => Perforce.Enabled = v);
    }

    public IReadOnlyList<PerforcePasswordSourceChoice> PerforcePasswordSources { get; } =
    [
        new(PerforcePasswordSource.Stored, "Stored by Claudette (recommended)"),
        new(PerforcePasswordSource.PerforceConfig, "Perforce's own configuration (P4PASSWD)"),
        new(PerforcePasswordSource.AskEachTime, "Ask each time"),
    ];

    public PerforcePasswordSourceChoice SelectedPasswordSource
    {
        get => PerforcePasswordSources.FirstOrDefault(c => c.Source == Perforce.PasswordSource) ?? PerforcePasswordSources[0];
        set
        {
            Set(value?.Source ?? PerforcePasswordSource.Stored, v => Perforce.PasswordSource = v);
            OnPropertyChanged(nameof(IsPerforceConfigSource));
            OnPropertyChanged(nameof(IsStoredPasswordSource));
        }
    }

    /// <summary>P4PASSWD in a P4CONFIG file, <c>p4 set</c> or the environment is plain text; Settings says so.</summary>
    public bool IsPerforceConfigSource => Perforce.PasswordSource == PerforcePasswordSource.PerforceConfig;

    public bool IsStoredPasswordSource => Perforce.PasswordSource == PerforcePasswordSource.Stored;

    public decimal? PerforceRenewBeforeMinutes
    {
        get => Perforce.RenewBeforeMinutes;
        set => Set(value, v => Perforce.RenewBeforeMinutes = Math.Clamp((int)(v ?? PerforceSettings.DefaultRenewBeforeMinutes), 1, 24 * 60));
    }

    public bool PerforceAllHostsTickets
    {
        get => Perforce.AllHostsTickets;
        set => Set(value, v => Perforce.AllHostsTickets = v);
    }

    public bool ShowChangelistOnTabs
    {
        get => Perforce.ShowChangelistOnTabs;
        set => Set(value, v => Perforce.ShowChangelistOnTabs = v);
    }

    [RelayCommand]
    private void ResetPerforce()
    {
        // The per-folder overrides are the user's data, like favorite folders; stored passwords are untouched too.
        _settings.Perforce = new PerforceSettings { FolderOverrides = Perforce.FolderOverrides };
        Save();
        OnPropertyChanged(nameof(PerforceEnabled));
        OnPropertyChanged(nameof(SelectedPasswordSource));
        OnPropertyChanged(nameof(IsPerforceConfigSource));
        OnPropertyChanged(nameof(IsStoredPasswordSource));
        OnPropertyChanged(nameof(PerforceRenewBeforeMinutes));
        OnPropertyChanged(nameof(PerforceAllHostsTickets));
        OnPropertyChanged(nameof(ShowChangelistOnTabs));
    }

    // ---- Per-folder overrides ---------------------------------------------------------------------------------------

    public ObservableCollection<PerforceOverrideEditor> PerforceOverrides => field ??= new(Perforce.FolderOverrides.Select(o => new PerforceOverrideEditor(o, Save)));

    [RelayCommand]
    private async Task AddPerforceOverrideAsync()
    {
        if (await _services.Platform.PickFolderAsync("Choose a folder to set a Perforce server or user for") is not { } folder)
        {
            return;
        }
        var editors = PerforceOverrides;
        if (Perforce.FolderOverrides.All(o => o.Folder != folder))
        {
            var entry = new PerforceFolderOverride { Folder = folder };
            Perforce.FolderOverrides.Add(entry);
            editors.Add(new PerforceOverrideEditor(entry, Save));
            Save();
        }
    }

    [RelayCommand]
    private void RemovePerforceOverride(PerforceOverrideEditor? editor)
    {
        if (editor is not null)
        {
            Perforce.FolderOverrides.Remove(editor.Entry);
            PerforceOverrides.Remove(editor);
            Save();
        }
    }

    // ---- The stored password ("Stored by Claudette") ---------------------------------------------------------------

    private ICredentialStore Credentials => _services.Perforce.Credentials;

    public string CredentialStoreName => Credentials.Name;

    public bool IsCredentialStoreAvailable => Credentials.IsAvailable;

    public string? CredentialStoreUnavailableText => Credentials.IsAvailable ? null : Credentials.UnavailableReason;

    public string StoredPasswordNote =>
        $"Saved in {Credentials.Name} under the server and user, never in Claudette's settings file, and never synced. Claudette also offers to save it the first time a tab logs in.";

    /// <summary>The server to save or forget a password for; starts as the first workspace a tab found.</summary>
    [ObservableProperty]
    public partial string PerforcePasswordServer { get; set; } = "";

    [ObservableProperty]
    public partial string PerforcePasswordUser { get; set; } = "";

    [ObservableProperty]
    public partial string PerforcePassword { get; set; } = "";

    [ObservableProperty]
    public partial string? PerforcePasswordStatus { get; set; }

    [RelayCommand]
    private async Task SavePerforcePasswordAsync()
    {
        var server = PerforcePasswordServer.Trim();
        var user = PerforcePasswordUser.Trim();
        if (server.Length == 0 || user.Length == 0 || PerforcePassword.Length == 0)
        {
            PerforcePasswordStatus = "Enter the server, the user and the password.";
            return;
        }
        try
        {
            await Credentials.WriteAsync(PerforceService.CredentialKey(server, user), PerforceService.CredentialLabel(server, user), PerforcePassword);
            PerforcePasswordStatus = $"Saved the password for {user} @ {server} in {Credentials.Name}.";
        }
        catch (CredentialStoreException ex)
        {
            PerforcePasswordStatus = ex.Message;
        }
        finally
        {
            PerforcePassword = "";
        }
    }

    [RelayCommand]
    private async Task ForgetPerforcePasswordAsync()
    {
        var server = PerforcePasswordServer.Trim();
        var user = PerforcePasswordUser.Trim();
        if (server.Length == 0 || user.Length == 0)
        {
            PerforcePasswordStatus = "Enter the server and the user.";
            return;
        }
        try
        {
            PerforcePasswordStatus = await Credentials.DeleteAsync(PerforceService.CredentialKey(server, user))
                ? $"Removed the password for {user} @ {server} from {Credentials.Name}."
                : $"No password was saved for {user} @ {server}.";
        }
        catch (CredentialStoreException ex)
        {
            PerforcePasswordStatus = ex.Message;
        }
    }

    private void FillPerforceLogin()
    {
        if (_services.Perforce.KnownLogins.FirstOrDefault() is { } login)
        {
            PerforcePasswordServer = login.Server;
            PerforcePasswordUser = login.User;
        }
    }
}
