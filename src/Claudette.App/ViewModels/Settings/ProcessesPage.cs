using Claudette.Core.Settings;

namespace Claudette.App.ViewModels.Settings;

/// <summary>Settings → Processes (DESIGN.md §4, "Process monitor").</summary>
public sealed class ProcessesPage(SettingsContext context) : SettingsPage(context, SettingsCategory.Processes)
{
    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Show the process monitor"),
        Entry("Refresh the panel every (seconds)"),
        Entry("Show command lines"),
    ];

    public bool ShowProcessMonitor
    {
        get => Settings.Processes.ShowMonitor;
        set => Set(value, v => Settings.Processes.ShowMonitor = v);
    }

    public decimal? ProcessRefreshSeconds
    {
        get => Settings.Processes.RefreshSeconds;
        set => Set(value, v => Settings.Processes.RefreshSeconds = Math.Clamp((int)(v ?? 2), 1, 60));
    }

    public bool ShowCommandLines
    {
        get => Settings.Processes.ShowCommandLines;
        set => Set(value, v => Settings.Processes.ShowCommandLines = v);
    }

    protected override void ResetSettings()
    {
        Settings.Processes = new ProcessSettings();
        Save();
    }
}
