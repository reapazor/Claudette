using System.Globalization;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// Continuing a task a usage limit stopped, once the limit resets (DESIGN.md §6, "Continuing after a limit resets"):
/// the bar over the composer, the tab's row and info card, and the message that continues it.
/// </summary>
public sealed partial class TabViewModel
{
    private readonly AutoContinueMonitor _autoContinue;

    private AutoContinueMonitor CreateAutoContinue() => new(_services.Time, () => ContinuesAfterLimitReset,
        message => _services.Dispatcher.Post(() => SendAutoContinue(message)),
        () => _services.Dispatcher.Post(OnLimitWaitChanged));

    /// <summary>The tab's own setting, else Settings → Usage.</summary>
    private bool ContinuesAfterLimitReset => State.Overrides.ContinueAfterLimitReset ?? _services.Settings.Usage.ContinueAfterLimitReset;

    /// <summary>A usage limit stopped the last turn, and the tab waits for it to reset; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLimitWait), nameof(LimitWaitText), nameof(WillContinueAfterLimit), nameof(CanContinueAfterLimit),
        nameof(ContinueAfterLimitText), nameof(RowDetail), nameof(InfoRows))]
    public partial LimitWait? LimitWait { get; private set; }

    public bool HasLimitWait => LimitWait is not null;

    /// <summary>It continues by itself at the reset: the bar offers <b>Don't continue</b>.</summary>
    public bool WillContinueAfterLimit => LimitWait is { WillContinue: true };

    /// <summary>It won't: the bar offers to continue, at the reset or now, and to close the bar.</summary>
    public bool CanContinueAfterLimit => LimitWait is { WillContinue: false };

    public string ContinueAfterLimitText => LimitWait is { HasReset: true } ? "Continue" : "Continue when it resets";

    /// <summary>What the bar says.</summary>
    public string? LimitWaitText => LimitWait is not { } wait ? null : wait switch
    {
        { Hold: LimitWaitHold.Missed } =>
            $"Your {wait.LimitName} reset {When(wait.ResetsAt)} while this computer was asleep or Claudette was closed, so the task didn't continue by itself.",
        { HasReset: true } => $"Your {wait.LimitName} has reset.",
        { Hold: LimitWaitHold.None } => $"You've hit your {wait.LimitName}. The task continues by itself when it resets, {When(wait.ResetsAt)}.",
        { Hold: LimitWaitHold.TooFar } =>
            $"You've hit your {wait.LimitName}. It resets {When(wait.ResetsAt)}, more than a day from now, so the task won't continue by itself.",
        { Hold: LimitWaitHold.Repeated } =>
            $"You've hit your {wait.LimitName} again after continuing {AutoContinueMonitor.MaxRepeats + 1} times in a row, so the task won't continue by itself. It resets {When(wait.ResetsAt)}.",
        _ => $"You've hit your {wait.LimitName}. It resets {When(wait.ResetsAt)}.",
    };

    /// <summary>"at 15:45" today, else "on Mon at 09:00", in the clock's time zone.</summary>
    private string When(DateTimeOffset time)
    {
        var local = TimeZoneInfo.ConvertTime(time, _services.Time.LocalTimeZone);
        var today = TimeZoneInfo.ConvertTime(_services.Time.GetUtcNow(), _services.Time.LocalTimeZone).Date;
        return local.Date == today
            ? $"at {local.ToString("t", CultureInfo.CurrentCulture)}"
            : $"on {local.ToString("ddd", CultureInfo.CurrentCulture)} at {local.ToString("t", CultureInfo.CurrentCulture)}";
    }

    /// <summary>The tab's row while it waits to continue, instead of the model (DESIGN.md §4, "Sidebar").</summary>
    private string? LimitWaitRowDetail => LimitWait is { WillContinue: true } wait ? $"Usage limit · continues {When(wait.ResetsAt)}" : null;

    private void AddLimitWaitRows(List<InfoRow> rows)
    {
        if (LimitWaitText is { } text)
        {
            rows.Add(new InfoRow("Usage limit", text));
        }
    }

    /// <summary><b>Don't continue</b>: this reset won't continue the task.</summary>
    [RelayCommand]
    private void DontContinue() => _autoContinue.DontContinue();

    /// <summary><b>Continue when it resets</b>, or <b>Continue</b> once it has.</summary>
    [RelayCommand]
    private void ContinueAfterLimit() => _autoContinue.ContinueAtReset();

    [RelayCommand]
    private void DismissLimitWait() => _autoContinue.Dismiss();

    /// <summary>A wait saved with the tab: it keeps waiting after a restart, or says the limit reset meanwhile.</summary>
    private void RestoreLimitWait()
    {
        if (State.LimitWait is { } saved)
        {
            _autoContinue.Restore(saved);
        }
    }

    private void OnLimitWaitChanged()
    {
        var wait = _autoContinue.Wait;
        if (wait == LimitWait)
        {
            return;
        }
        LimitWait = wait;
        State.LimitWait = wait;
        _services.SaveState();
    }

    /// <summary>Sends the message that continues the task, labeled as sent by Claudette, as a check-in is.</summary>
    private void SendAutoContinue(string message)
    {
        // The user took over meanwhile, or the tab can't run here.
        if (IsWorking || IsReadOnly || IsFolderMissing || IsSessionMissing)
        {
            return;
        }
        _conversation.AddUserMessage(message, isAutoContinue: true);
        _ = SendRawAsync(message);
    }
}
