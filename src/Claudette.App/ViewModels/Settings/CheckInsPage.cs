using Claudette.Core.Settings;

namespace Claudette.App.ViewModels.Settings;

/// <summary>Settings → Check-ins (DESIGN.md §5, "Check-ins on long turns"). Each tab can override them in Tab settings.</summary>
public sealed class CheckInsPage(SettingsContext context) : SettingsPage(context, SettingsCategory.CheckIns)
{
    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Check in on long turns"),
        Entry("After the turn has run (minutes)"),
        Entry("After no output for (minutes)"),
        Entry("Check-in message", pageText: "Message"),
        Entry("Notify me when a check-in is sent"),
    ];

    public bool CheckInsEnabled
    {
        get => Settings.CheckIns.Enabled;
        set => Set(value, v => Settings.CheckIns.Enabled = v);
    }

    public decimal? CheckInRunTime
    {
        get => Settings.CheckIns.RunTimeMinutes;
        set => Set(value, v => Settings.CheckIns.RunTimeMinutes = Math.Max(0, (int)(v ?? 0)));
    }

    public decimal? CheckInQuietTime
    {
        get => Settings.CheckIns.QuietTimeMinutes;
        set => Set(value, v => Settings.CheckIns.QuietTimeMinutes = Math.Max(0, (int)(v ?? 0)));
    }

    public string CheckInMessage
    {
        get => Settings.CheckIns.Message;
        set => Set(value, v => Settings.CheckIns.Message = string.IsNullOrWhiteSpace(v) ? CheckInSettings.DefaultMessage : v);
    }

    public bool NotifyOnCheckIn
    {
        get => Settings.CheckIns.Notify;
        set => Set(value, v => Settings.CheckIns.Notify = v);
    }

    protected override void ResetSettings()
    {
        Settings.CheckIns = new CheckInSettings();
        Save();
    }
}
