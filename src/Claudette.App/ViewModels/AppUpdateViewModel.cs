using System.Globalization;
using Claudette.App.Services;
using Claudette.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The sidebar's "Claudette 1.3.0 is available" badge, its dialog, and the updates part of Settings → General
/// (DESIGN.md §2, "Updating Claudette").
/// </summary>
public sealed partial class AppUpdateViewModel : ObservableObject, IDisposable
{
    /// <summary>Release notes longer than this are cut, with the release page for the rest.</summary>
    private const int MaxNotesLength = 4000;

    private readonly AppUpdateService _updates;

    public AppUpdateViewModel(AppUpdateService updates)
    {
        _updates = updates;
        _updates.Changed += Refresh;
    }

    private AvailableUpdate? Available => _updates.Available;

    private string NewVersion => Available?.Version.ToString() ?? "";

    public bool HasBadge => !_updates.IsSourceBuild
        && ((Available is not null && !_updates.IsSkipped) || _updates.LastError is not null || _updates.IsDownloading || _updates.IsInstalling);

    public string BadgeText =>
        _updates.IsInstalling && !_updates.IsDownloading ? $"Installing Claudette {NewVersion}…"
        : _updates.IsWaitingForIdle ? "Updating when idle…"
        : _updates.IsDownloading ? $"Downloading Claudette {NewVersion}… {DownloadPercent:0}%"
        : Available is null && _updates.LastError is not null ? "Update didn't install"
        : _updates.DownloadedPath is not null ? $"Restart to update to {NewVersion}"
        : $"Claudette {NewVersion} is available";

    public string Title => Available is null ? "Claudette updates" : $"Claudette {NewVersion} is available";

    public string CurrentVersionText => _updates.UpdatedFrom is { } from
        ? $"You have Claudette {_updates.CurrentVersion}, updated from {from}."
        : $"You have Claudette {_updates.CurrentVersion}.";

    public string? ReleasedText => Available?.Release.PublishedAt is { } at
        ? $"Released {at.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}{(Available.Release.IsPrerelease ? " as a pre-release" : "")}."
        : null;

    public string NotesText => Available?.Release.Notes.Trim() is { Length: > 0 } notes
        ? notes.Length > MaxNotesLength ? notes[..MaxNotesLength].TrimEnd() + "…" : notes
        : "";

    public bool HasNotes => NotesText.Length > 0;

    /// <summary>Why there's nothing to install here, when there isn't.</summary>
    public string? ManualText => Available is null ? null
        : _updates.Kind == AppInstallKind.Other ? "This copy of Claudette can't update itself. Download the new version from its release page."
        : Available.Asset is null ? "This release has no package for this computer yet. See its release page."
        : Available.Asset.Sha256 is null ? "GitHub published no checksum for this release's package, so Claudette can't check it. Download it from its release page."
        : null;

    public bool HasManualText => ManualText is not null;

    public bool IsDownloading => _updates.IsDownloading;

    public double DownloadPercent => _updates.DownloadProgress * 100;

    public bool IsBusy => _updates.IsInstalling || _updates.IsDownloading;

    /// <summary>Checking and installing the package, after any download.</summary>
    public bool IsInstalling => _updates.IsInstalling && !_updates.IsDownloading;

    private bool Installable => _updates.CanInstall && Available?.Asset is { Sha256: not null };

    /// <summary>Download now and install later, from the badge or Settings.</summary>
    public bool CanDownload => Installable && _updates.DownloadedPath is null && !IsBusy;

    public bool CanRestartToUpdate => Installable && !IsBusy;

    /// <summary>One click either way: download first when needed, then restart into the new version.</summary>
    public string RestartLabel => _updates.DownloadedPath is null ? "Download and restart" : "Restart to update";

    public bool CanUpdateWhenIdle => CanRestartToUpdate && _updates.AnyTabWorking && !_updates.IsWaitingForIdle;

    public bool IsWaitingForIdle => _updates.IsWaitingForIdle;

    public string? WorkingText => CanRestartToUpdate && _updates.AnyTabWorking
        ? "A tab is working. Restarting interrupts it, and it resumes after the update; Update when idle waits for it to finish."
        : null;

    public string? ErrorText => _updates.LastError;

    public bool HasError => ErrorText is not null;

    public bool CanOpenPackage => _updates.CanOpenPackage && !IsBusy;

    public string OpenPackageLabel => _updates.Kind == AppInstallKind.Msix ? "Open the installer" : "Open the disk image";

    public bool CanSkip => Available is not null && !_updates.IsSkipped && !IsBusy;

    public bool HasRelease => Available is not null;

    // ---- For Settings → General ---------------------------------------------------------------------------------

    public bool IsSourceBuild => _updates.IsSourceBuild;

    public string CheckStatusText =>
        _updates.IsSourceBuild ? "This is a source build, so it doesn't check for releases: it restarts into each new build of its checkout instead."
        : _updates.IsChecking ? "Checking for updates…"
        : _updates.CheckError is { } error ? error
        : _updates.LastChecked is not { } at ? "Not checked yet."
        : Available is null ? $"Claudette is up to date. Checked at {at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}."
        : _updates.IsSkipped ? $"Claudette {NewVersion} is available; you skipped it."
        : $"Claudette {NewVersion} is available.";

    public bool CanCheck => !_updates.IsSourceBuild && !_updates.IsChecking;

    [RelayCommand]
    private Task CheckNowAsync() => _updates.CheckNowAsync();

    [RelayCommand]
    private Task DownloadAsync() => _updates.DownloadAsync();

    [RelayCommand]
    private void CancelDownload() => _updates.CancelDownload();

    [RelayCommand]
    private Task RestartToUpdateAsync() => _updates.InstallAsync();

    [RelayCommand]
    private void UpdateWhenIdle() => _updates.InstallWhenIdle();

    [RelayCommand]
    private void CancelWaiting() => _updates.CancelWaiting();

    [RelayCommand]
    private void Skip() => _updates.Skip();

    [RelayCommand]
    private Task OpenReleasePageAsync() => _updates.OpenReleasePageAsync();

    [RelayCommand]
    private Task OpenPackageAsync() => _updates.OpenPackageAsync();

    private void Refresh() => OnPropertyChanged(string.Empty);

    public void Dispose() => _updates.Changed -= Refresh;
}
