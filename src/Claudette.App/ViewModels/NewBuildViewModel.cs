using System.Globalization;
using Claudette.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The sidebar's entry for a new build of Claudette itself, when running from a source build (DESIGN.md §9, "Working
/// on Claudette"), and its dialog.
/// </summary>
public sealed partial class NewBuildViewModel : ObservableObject, IDisposable
{
    private readonly RestartService _restarts;

    public NewBuildViewModel(RestartService restarts)
    {
        _restarts = restarts;
        _restarts.Changed += Refresh;
    }

    public bool HasBadge => _restarts.ReadyStamp is not null || _restarts.LastError is not null;

    public string BadgeText =>
        _restarts.IsRestarting ? "Restarting…"
        : _restarts.IsWaitingForIdle ? "Restarting when idle…"
        : _restarts.ReadyStamp is null && _restarts.LastError is not null ? "Restart didn't happen"
        : "New build ready";

    public string Title => _restarts.ReadyStamp is null ? "Claudette's build" : "A new build of Claudette is ready";

    public string DetailText => _restarts.ReadyStamp is { } stamp
        ? $"Built at {Time(stamp)} in {_restarts.Build.SourceOutput}. Restarting opens every tab again, resumes its session and keeps what you've typed."
        : $"Watching {_restarts.Build.SourceOutput} for a new build.";

    public string RunningText => $"Running the build from {Time(_restarts.Build.RunningStamp)}.";

    public string? WorkingText => _restarts.AnyTabWorking && _restarts.ReadyStamp is not null && !_restarts.IsRestarting
        ? "A tab is working. Restart now interrupts it, and it resumes after the restart; Restart when idle waits for it to finish."
        : null;

    public string? ErrorText => _restarts.LastError;

    public bool IsRestarting => _restarts.IsRestarting;

    public bool IsWaitingForIdle => _restarts.IsWaitingForIdle;

    public bool CanRestart => _restarts.ReadyStamp is not null && !_restarts.IsRestarting;

    public bool CanRestartWhenIdle => CanRestart && !_restarts.IsWaitingForIdle && _restarts.AnyTabWorking;

    public bool RestartAutomatically
    {
        get => _restarts.RestartAutomatically;
        set
        {
            _restarts.RestartAutomatically = value;
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private Task RestartNowAsync() => _restarts.RestartAsync();

    [RelayCommand]
    private void RestartWhenIdle() => _restarts.RestartWhenIdle();

    [RelayCommand]
    private void CancelWaiting() => _restarts.CancelWaiting();

    private static string Time(DateTime utc) => utc.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);

    private void Refresh() => OnPropertyChanged(string.Empty);

    public void Dispose() => _restarts.Changed -= Refresh;
}
