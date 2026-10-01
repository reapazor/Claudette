using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels.Settings;

/// <summary>
/// Settings → Usage (DESIGN.md §6): the warning levels, the burn rate, the model meters, continuing after a limit
/// resets, the usage history and sharing usage with other machines.
/// </summary>
public sealed partial class UsagePage(SettingsContext context) : SettingsPage(context, SettingsCategory.Usage)
{
    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Warn at (% of session used)"),
        Entry("Alert at (% of session used)"),
        Entry("Burn rate window (minutes)"),
        Entry("Show model-specific weekly limits"),
        Entry("Read model limits from /usage"),
        Entry("Continue tasks when a usage limit resets"),
        Entry("Keep usage history"),
        Entry("Clear usage history"),
        Entry("Share usage with my other machines"),
    ];

    public decimal? WarnPercent
    {
        get => (decimal)Settings.Usage.WarnPercent;
        set => Set(value, v => Settings.Usage.WarnPercent = Math.Clamp((double)(v ?? 75), 1, 100));
    }

    public decimal? CriticalPercent
    {
        get => (decimal)Settings.Usage.CriticalPercent;
        set => Set(value, v => Settings.Usage.CriticalPercent = Math.Clamp((double)(v ?? 90), 1, 100));
    }

    public decimal? BurnRateWindowMinutes
    {
        get => Settings.Usage.BurnRateWindowMinutes;
        set => Set(value, v => Settings.Usage.BurnRateWindowMinutes = Math.Clamp((int)(v ?? 30), 5, 300));
    }

    public bool ShowModelMeters
    {
        get => Settings.Usage.ShowModelMeters;
        set => Set(value, v => Settings.Usage.ShowModelMeters = v);
    }

    public bool UseUsageCommandFallback
    {
        get => Settings.Usage.UseUsageCommandFallback;
        set => Set(value, v => Settings.Usage.UseUsageCommandFallback = v);
    }

    /// <summary>DESIGN.md §6, "Continuing after a limit resets". Each tab can override it in Tab settings.</summary>
    public bool ContinueAfterLimitReset
    {
        get => Settings.Usage.ContinueAfterLimitReset;
        set => Set(value, v => Settings.Usage.ContinueAfterLimitReset = v);
    }

    /// <summary>DESIGN.md §6, "Sharing across machines".</summary>
    public bool ShareUsageThroughLibrary
    {
        get => Settings.Usage.ShareThroughLibrary;
        set => Set(value, v => Settings.Usage.ShareThroughLibrary = v);
    }

    // ---- Usage history (DESIGN.md §6, "Usage history") ----------------------------------------------------------

    public IReadOnlyList<RetentionChoice> HistoryRetentionChoices { get; } =
        [.. Enum.GetValues<RetentionPeriod>().Select(p => new RetentionChoice(p))];

    public RetentionChoice KeepUsageHistory
    {
        get => HistoryRetentionChoices.FirstOrDefault(c => c.Period == Settings.Usage.KeepHistory) ?? HistoryRetentionChoices[2];
        set => Set(value?.Period ?? RetentionPeriod.OneMonth, v => Settings.Usage.KeepHistory = v);
    }

    /// <summary>"Clear usage history" waiting for confirmation.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingClearUsage { get; set; }

    /// <summary>The confirmation's checkbox: also reset the token totals saved with each tab.</summary>
    [ObservableProperty]
    public partial bool AlsoResetTabTotals { get; set; }

    [ObservableProperty]
    public partial string? UsageClearedText { get; set; }

    [RelayCommand]
    private void ClearUsageHistory()
    {
        AlsoResetTabTotals = false;
        UsageClearedText = null;
        IsConfirmingClearUsage = true;
    }

    [RelayCommand]
    private async Task ConfirmClearUsageAsync()
    {
        IsConfirmingClearUsage = false;
        await Services.ClearUsageHistoryAsync(AlsoResetTabTotals);
        UsageClearedText = AlsoResetTabTotals ? "Usage history and tab token totals cleared." : "Usage history cleared.";
    }

    [RelayCommand]
    private void CancelClearUsage() => IsConfirmingClearUsage = false;

    /// <summary><b>Keep usage history</b> stays as it is.</summary>
    protected override void ResetSettings()
    {
        var keep = Settings.Usage.KeepHistory;
        Settings.Usage = new UsageSettings { KeepHistory = keep };
        Save();
    }
}
