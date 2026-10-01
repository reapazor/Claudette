using Claudette.Core.Library;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels.Settings;

/// <summary>
/// Settings → Sessions (DESIGN.md §14): restoring tabs, this machine's name, the session library (DESIGN.md §9) and
/// syncing Claudette's settings through it.
/// </summary>
public sealed partial class SessionsPage(SettingsContext context) : SettingsPage(context, SettingsCategory.Sessions)
{
    private bool _moveLibrary;

    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Also restore unpinned tabs when Claudette starts"),
        Entry("Name for this machine"),
        Entry("Keep library sessions for"),
        Entry("Session library folder"),
        Entry("Move library"),
        Entry("Sync new tabs to the session library"),
        Entry("Sync Claudette's settings through the library"),
    ];

    public bool RestoreUnpinnedTabs
    {
        get => Settings.Sessions.RestoreUnpinnedTabs;
        set => Set(value, v => Settings.Sessions.RestoreUnpinnedTabs = v);
    }

    public IReadOnlyList<RetentionChoice> LibraryRetentionChoices { get; } =
        [.. new[] { RetentionPeriod.OneMonth, RetentionPeriod.OneYear, RetentionPeriod.Forever }.Select(p => new RetentionChoice(p))];

    public RetentionChoice KeepLibrarySessions
    {
        get => LibraryRetentionChoices.FirstOrDefault(c => c.Period == Settings.Sessions.KeepLibrarySessions) ?? LibraryRetentionChoices[^1];
        set => Set(value?.Period ?? RetentionPeriod.Forever, v => Settings.Sessions.KeepLibrarySessions = v);
    }

    /// <summary>Shown in History and in "in use on …" notices. Empty uses the computer name.</summary>
    public string MachineName
    {
        get => Settings.Sessions.MachineName ?? "";
        set => Set(value, v => Settings.Sessions.MachineName = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    public string MachineNamePlaceholder => Environment.MachineName;

    /// <summary>New tabs start syncing to the session library (DESIGN.md §9). Each tab can change it from its menu.</summary>
    public bool SyncNewTabs
    {
        get => Settings.Sessions.SyncNewTabs;
        set => Set(value, v => Settings.Sessions.SyncNewTabs = v);
    }

    // ---- Session library (DESIGN.md §9) ----------------------------------------------------------------------------

    public string LibraryFolder => Services.Library.LibraryFolder;

    public bool IsDefaultLibraryFolder => Settings.Sessions.LibraryFolder is null;

    /// <summary>A folder the user picked, waiting for them to confirm it's fine to sync transcripts there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingLibraryFolder))]
    public partial string? PendingLibraryFolder { get; set; }

    public bool IsConfirmingLibraryFolder => PendingLibraryFolder is not null;

    [ObservableProperty]
    public partial string LibraryConfirmText { get; set; } = "";

    [ObservableProperty]
    public partial string? LibraryStatus { get; set; }

    /// <summary>Browse…: use another folder from now on.</summary>
    [RelayCommand]
    private Task BrowseLibraryFolderAsync() => ChooseLibraryFolderAsync(move: false);

    /// <summary>Move library…: copy the existing sessions to the new folder, then use it.</summary>
    [RelayCommand]
    private Task MoveLibraryAsync() => ChooseLibraryFolderAsync(move: true);

    private async Task ChooseLibraryFolderAsync(bool move)
    {
        if (await Services.Platform.PickFolderAsync(move ? "Move the session library to" : "Choose a folder for the session library") is not { } folder)
        {
            return;
        }
        _moveLibrary = move;
        // Transcripts hold code and command output; say so before they go to a synced folder (DESIGN.md §9, "Privacy").
        if (SessionLibrary.IsInCloudSyncFolder(folder, out var provider))
        {
            LibraryConfirmText = $"This folder is synced by {provider}. Session transcripts contain your code, command output and anything else Claude read in your projects, and they'll be uploaded there. Use it anyway?";
            PendingLibraryFolder = folder;
            return;
        }
        await ApplyLibraryFolderAsync(folder);
    }

    [RelayCommand]
    private async Task ConfirmLibraryFolderAsync()
    {
        if (PendingLibraryFolder is { } folder)
        {
            PendingLibraryFolder = null;
            await ApplyLibraryFolderAsync(folder);
        }
    }

    [RelayCommand]
    private void CancelLibraryFolder() => PendingLibraryFolder = null;

    [RelayCommand]
    private Task UseDefaultLibraryFolderAsync() => ApplyLibraryFolderAsync(null);

    private async Task ApplyLibraryFolderAsync(string? folder)
    {
        var old = Services.Library.LibraryFolder;
        if (_moveLibrary && folder is not null)
        {
            LibraryStatus = "Copying sessions…";
            try
            {
                await SessionLibrary.CopyLibraryAsync(old, folder);
            }
            catch (Exception ex)
            {
                LibraryStatus = $"Couldn't copy the library: {ex.Message}";
                return;
            }
        }
        Settings.Sessions.LibraryFolder = folder;
        Save();
        LibraryStatus = _moveLibrary && folder is not null ? $"Copied the sessions to {folder}. The old folder was left as it was." : null;
        _moveLibrary = false;
        OnPropertyChanged(nameof(LibraryFolder));
        OnPropertyChanged(nameof(IsDefaultLibraryFolder));
    }

    // ---- Settings sync (DESIGN.md §14) -------------------------------------------------------------------------------

    public bool SyncSettings
    {
        get => Settings.Sessions.SyncSettings;
        set
        {
            if (value == Settings.Sessions.SyncSettings)
            {
                return;
            }
            if (value && Services.Library.HasSyncedSettings())
            {
                // The library already has settings from another machine: ask which ones win.
                IsChoosingSyncSource = true;
                OnPropertyChanged();
                return;
            }
            Settings.Sessions.SyncSettings = value;
            Save();
            OnPropertyChanged();
            if (value)
            {
                _ = Services.Library.PublishAllSettingsAsync();
            }
        }
    }

    [ObservableProperty]
    public partial bool IsChoosingSyncSource { get; set; }

    /// <summary>The library's settings win: every page shows them.</summary>
    [RelayCommand]
    private async Task UseSyncedSettingsAsync()
    {
        IsChoosingSyncSource = false;
        Settings.Sessions.SyncSettings = true;
        Services.State.SettingsSync = null;
        Save();
        await Services.Library.SyncSettingsAsync();
        Context.RefreshAllPages();
    }

    [RelayCommand]
    private async Task ReplaceSyncedSettingsAsync()
    {
        IsChoosingSyncSource = false;
        Settings.Sessions.SyncSettings = true;
        Save();
        await Services.Library.PublishAllSettingsAsync();
        OnPropertyChanged(nameof(SyncSettings));
    }

    [RelayCommand]
    private void CancelSyncChoice()
    {
        IsChoosingSyncSource = false;
        OnPropertyChanged(nameof(SyncSettings));
    }

    /// <summary>
    /// The library folder and settings sync are left as they are: changing either moves where sessions and settings
    /// live, which is its own decision with its own buttons.
    /// </summary>
    protected override void ResetSettings()
    {
        var defaults = new SessionSettings();
        Settings.Sessions.RestoreUnpinnedTabs = defaults.RestoreUnpinnedTabs;
        Settings.Sessions.MachineName = defaults.MachineName;
        Settings.Sessions.KeepLibrarySessions = defaults.KeepLibrarySessions;
        Settings.Sessions.SyncNewTabs = defaults.SyncNewTabs;
        Save();
    }
}
