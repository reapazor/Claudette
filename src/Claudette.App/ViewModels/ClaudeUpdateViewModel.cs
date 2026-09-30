using System.Text;
using Claudette.App.Services;
using Claudette.Core.Installation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The header's "Claude Code 2.1.290 is ready" badge and its dialog, and the update part of Settings → Claude Code
/// (DESIGN.md §12, "Applying it").
/// </summary>
/// <param name="runningVersions">The Claude Code version each running tab uses.</param>
public sealed partial class ClaudeUpdateViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private readonly ClaudeUpdateService _updates;
    private readonly Func<IReadOnlyCollection<Version>> _runningVersions;
    private readonly StringBuilder _output = new();

    public ClaudeUpdateViewModel(AppServices services, ClaudeUpdateService updates, Func<IReadOnlyCollection<Version>> runningVersions)
    {
        _services = services;
        _updates = updates;
        _runningVersions = runningVersions;
        _updates.Changed += Refresh;
        Refresh();
    }

    /// <summary>The version the badge announces, or null when there's nothing to announce.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge), nameof(BadgeText), nameof(Title))]
    public partial Version? ReadyVersion { get; private set; }

    /// <summary>True when the ready version is already installed (it updated itself); false when it still needs installing.</summary>
    [ObservableProperty]
    public partial bool IsReadyInstalled { get; private set; }

    public bool HasBadge => ReadyVersion is { } ready && !(Dismissed >= ready);

    /// <summary>Saved on this machine, so a dismissed badge and a sent notification stay that way after a restart.</summary>
    private Version? Dismissed => Version.TryParse(_services.State.DismissedClaudeUpdate, out var version) ? version : null;

    private Version? Notified => Version.TryParse(_services.State.NotifiedClaudeUpdate, out var version) ? version : null;

    public string BadgeText => $"Claude Code {ReadyVersion} is ready";

    public string Title => ReadyVersion is null ? "Claude Code" : BadgeText;

    [ObservableProperty]
    public partial string DetailText { get; private set; } = "";

    [ObservableProperty]
    public partial ClaudeUpdatePlan? Plan { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand))]
    public partial bool CanUpdateNow { get; private set; }

    /// <summary>WinGet while a tab is running: the update waits for the next launch.</summary>
    [ObservableProperty]
    public partial bool CanUpdateOnNextLaunch { get; private set; }

    [ObservableProperty]
    public partial bool IsUpdateScheduled { get; private set; }

    /// <summary>A command to run by hand (apt, dnf, apk, or a package manager Claudette couldn't find).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasManualCommand))]
    public partial string? ManualCommand { get; private set; }

    public bool HasManualCommand => ManualCommand is not null;

    /// <summary>Why there's no update action, when there isn't one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? Note { get; private set; }

    public bool HasNote => Note is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateNowCommand), nameof(CheckNowCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>What the update command printed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    public partial string Output { get; private set; } = "";

    public bool HasOutput => Output.Length > 0;

    /// <summary>How the last update went.</summary>
    [ObservableProperty]
    public partial string? ResultText { get; private set; }

    // ---- For Settings → Claude Code ---------------------------------------------------------------------------

    [ObservableProperty]
    public partial string VersionText { get; private set; } = "";

    [ObservableProperty]
    public partial string? AutoUpdateText { get; private set; }

    [ObservableProperty]
    public partial string? LastAttemptText { get; private set; }

    [ObservableProperty]
    public partial string CheckStatusText { get; private set; } = "";

    /// <summary>Problems <c>claude doctor</c> found, with its fixes (DESIGN.md §12: show the fix npm installs suggest).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    public partial IReadOnlyList<DoctorWarning> Warnings { get; private set; } = [];

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Works out what to announce and offer. Also called when tabs start or stop.</summary>
    public void Refresh()
    {
        var check = _updates.LatestCheck;
        var installed = _updates.InstalledVersion;
        var plan = check?.Plan;
        var running = _runningVersions();
        var oldest = running.Count > 0 ? running.Min() : null;

        if (check?.AvailableVersion is { } available && available > installed)
        {
            ReadyVersion = available;
            IsReadyInstalled = false;
            DetailText = $"Claude Code {available} is available from {plan?.Method ?? "your package manager"}. You have {installed}."
                + (installed < ClaudeLocator.LastTestedVersion ? $" Claudette was last tested with {ClaudeLocator.LastTestedVersion}." : "");
        }
        else if (oldest is not null && installed > oldest)
        {
            ReadyVersion = installed;
            IsReadyInstalled = true;
            DetailText = $"Claude Code {installed} is installed. New tabs use it; open tabs keep running {oldest} until you close them. "
                + "To move a tab to the new version, close it and open a new one, or reopen its session from History. Pinned tabs pick it up the next time Claudette starts.";
        }
        else
        {
            ReadyVersion = null;
            IsReadyInstalled = false;
            DetailText = $"Claude Code {installed} is installed.";
        }

        var tabRunning = running.Count > 0;
        Plan = plan;
        IsBusy = _updates.IsChecking || _updates.IsUpdating;
        CanUpdateNow = plan is { CanRun: true } && !(plan.NeedsClaudeStopped && tabRunning);
        CanUpdateOnNextLaunch = plan is { CanRun: true, NeedsClaudeStopped: true } && tabRunning;
        IsUpdateScheduled = _updates.IsUpdateScheduled;
        ManualCommand = plan is { Kind: ClaudeUpdateKind.Manual } ? plan.CommandText : null;
        Note = CanUpdateOnNextLaunch
            ? "WinGet can't replace Claude Code while a tab is running it. Update on next launch updates it the next time Claudette starts, before any tab starts."
            : plan?.Note;

        var doctor = check?.Doctor;
        VersionText = plan is null ? $"Claude Code {installed}" : $"Claude Code {installed}, installed with: {plan.Method}";
        // DESIGN.md §16, "Tested versions": a quiet note either way, nothing more. Only the minimum version is required.
        if (installed > ClaudeLocator.LastTestedVersion)
        {
            VersionText += $". Newer than the last tested version ({ClaudeLocator.LastTestedVersion})";
        }
        else if (installed < ClaudeLocator.LastTestedVersion)
        {
            VersionText += $". Older than the last tested version ({ClaudeLocator.LastTestedVersion}); updating is recommended";
        }
        AutoUpdateText = doctor is null ? null
            : doctor.Channel is { } channel ? $"Auto-updates: {doctor.AutoUpdates ?? "unknown"} · channel: {channel}"
            : $"Auto-updates: {doctor.AutoUpdates ?? "unknown"}";
        LastAttemptText = doctor?.LastUpdateAttempt is { } attempt ? $"Last update attempt: {attempt}" : null;
        Warnings = doctor?.Warnings ?? [];
        CheckStatusText = _updates.IsChecking ? "Checking…"
            : _updates.IsUpdating ? "Updating…"
            : check is null ? (_services.Settings.ClaudeCode.CheckForUpdates ? "Not checked yet." : "Automatic checks are off.")
            : check.Error is { } error ? $"The last check had a problem: {error}"
            : ReadyVersion is { } ready ? $"{ready} is ready. Checked {Ago(check.CheckedAt)}."
            : $"Up to date. Checked {Ago(check.CheckedAt)}.";
        if (_updates.LastResult is { } result && ResultText is null)
        {
            ResultText = result.Message;
        }
        OnPropertyChanged(nameof(HasBadge));
        if (ReadyVersion is { } readyVersion && !(Notified >= readyVersion) && !(Dismissed >= readyVersion))
        {
            // Once per version (DESIGN.md §10).
            _services.State.NotifiedClaudeUpdate = readyVersion.ToString();
            _services.SaveState();
            _services.Notifications.Notify(NotificationKind.UpdateReady, BadgeText, IsReadyInstalled
                ? "New tabs use it. Open tabs keep their version until you close them."
                : $"Update it from Claudette: {Plan?.Method ?? "your package manager"} has it.");
        }
    }

    /// <summary>Asks the sidebar to open the update dialog, for a clicked notification.</summary>
    public event Action? OpenRequested;

    public void RequestOpen() => OpenRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanRunUpdate))]
    private async Task UpdateNowAsync()
    {
        _output.Clear();
        Output = "";
        ResultText = null;
        var result = await _updates.UpdateNowAsync(line =>
        {
            _output.AppendLine(line);
            Output = _output.ToString();
        });
        ResultText = result.Succeeded
            ? $"{result.Message} New tabs use it."
            : result.Message;
        if (!result.Succeeded && Plan is { NeedsClaudeStopped: true })
        {
            // A WinGet upgrade fails while claude is running; offer the next launch instead.
            CanUpdateOnNextLaunch = true;
        }
    }

    private bool CanRunUpdate() => CanUpdateNow && !IsBusy;

    [RelayCommand]
    private void UpdateOnNextLaunch() => _updates.ScheduleUpdateOnNextLaunch();

    [RelayCommand]
    private void CancelUpdateOnNextLaunch() => _updates.ScheduleUpdateOnNextLaunch(false);

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync()
    {
        ResultText = null;
        return _updates.CheckNowAsync();
    }

    private bool CanCheck() => !IsBusy;

    [RelayCommand]
    private Task OpenChangelogAsync() => _services.Platform.OpenUrlAsync(ClaudeUpdateService.ChangelogUrl);

    [RelayCommand]
    private Task CopyManualCommandAsync() => ManualCommand is { } command ? _services.Platform.SetClipboardTextAsync(command) : Task.CompletedTask;

    /// <summary>Hides the badge until a newer version comes along.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        _services.State.DismissedClaudeUpdate = ReadyVersion?.ToString();
        _services.SaveState();
        OnPropertyChanged(nameof(HasBadge));
    }

    private string Ago(DateTimeOffset time)
    {
        var elapsed = _services.Time.GetUtcNow() - time;
        return elapsed < TimeSpan.FromMinutes(1) ? "just now"
            : elapsed < TimeSpan.FromHours(1) ? $"{(int)elapsed.TotalMinutes} min ago"
            : $"at {time.ToLocalTime():t}";
    }

    public void Dispose() => _updates.Changed -= Refresh;
}
