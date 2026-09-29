using Claudette.App.Services;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A choice in a dropdown. A null value means "use the default".</summary>
public sealed record Choice(string? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Tab settings…": settings for one tab that replace the defaults (DESIGN.md §14, "Per-tab overrides").
/// </summary>
public sealed partial class TabSettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly TabViewModel _tab;
    private readonly Action _close;

    public TabSettingsViewModel(AppServices services, TabViewModel tab, Action close)
    {
        _services = services;
        _tab = tab;
        _close = close;
        var overrides = tab.State.Overrides;
        var defaults = services.Settings;

        ModelChoices = [new Choice(null, $"Default ({defaults.NewTabs.DefaultModel ?? "Claude Code's default"})"), .. tab.Models.Select(m => new Choice(m.Value, m.DisplayName))];
        if (overrides.Model is { } model && ModelChoices.All(c => c.Value != model))
        {
            ModelChoices = [.. ModelChoices, new Choice(model, model)];
        }
        EffortChoices = [new Choice(null, $"Default ({defaults.NewTabs.DefaultEffort ?? "model default"})"), .. new[] { "low", "medium", "high", "xhigh", "max" }.Select(e => new Choice(e, e))];
        ModeChoices = [new Choice(null, $"Default ({PermissionModeInfo.Label(defaults.NewTabs.DefaultPermissionMode)})"), .. PermissionModeInfo.Choices.Select(m => new Choice(m.Value, m.Label))];
        MonitorChoices = [new Choice(null, $"Default ({(defaults.Processes.ShowMonitor ? "on" : "off")})"), new Choice(On, "On"), new Choice(Off, "Off")];

        SelectedModel = ModelChoices.First(c => c.Value == overrides.Model);
        SelectedEffort = EffortChoices.FirstOrDefault(c => c.Value == overrides.Effort) ?? EffortChoices[0];
        SelectedMode = ModeChoices.FirstOrDefault(c => c.Value == overrides.PermissionMode) ?? ModeChoices[0];
        SelectedMonitor = MonitorChoices.First(c => c.Value == overrides.ShowProcessMonitor switch { true => On, false => Off, null => null });

        var checkIns = overrides.CheckIns ?? defaults.CheckIns;
        UseCustomCheckIns = overrides.CheckIns is not null;
        CheckInsEnabled = checkIns.Enabled;
        RunTimeMinutes = checkIns.RunTimeMinutes;
        QuietTimeMinutes = checkIns.QuietTimeMinutes;
        CheckInMessage = checkIns.Message;
        NotifyOnCheckIn = checkIns.Notify;
    }

    public string Title => $"Settings for \"{_tab.DisplayName}\"";

    public IReadOnlyList<Choice> ModelChoices { get; }

    public IReadOnlyList<Choice> EffortChoices { get; }

    public IReadOnlyList<Choice> ModeChoices { get; }

    private const string On = "on";
    private const string Off = "off";

    /// <summary>The process monitor for this tab (DESIGN.md §4, "Process monitor").</summary>
    public IReadOnlyList<Choice> MonitorChoices { get; }

    [ObservableProperty]
    public partial Choice SelectedMonitor { get; set; }

    [ObservableProperty]
    public partial Choice SelectedModel { get; set; }

    [ObservableProperty]
    public partial Choice SelectedEffort { get; set; }

    [ObservableProperty]
    public partial Choice SelectedMode { get; set; }

    [ObservableProperty]
    public partial bool UseCustomCheckIns { get; set; }

    [ObservableProperty]
    public partial bool CheckInsEnabled { get; set; }

    [ObservableProperty]
    public partial decimal? RunTimeMinutes { get; set; }

    [ObservableProperty]
    public partial decimal? QuietTimeMinutes { get; set; }

    [ObservableProperty]
    public partial string CheckInMessage { get; set; }

    [ObservableProperty]
    public partial bool NotifyOnCheckIn { get; set; }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var previous = _tab.State.Overrides;
        _tab.State.Overrides = new TabOverrides
        {
            Model = SelectedModel.Value,
            Effort = SelectedEffort.Value,
            PermissionMode = SelectedMode.Value,
            ShowProcessMonitor = SelectedMonitor.Value switch { On => true, Off => false, _ => null },
            CheckIns = UseCustomCheckIns
                ? new CheckInSettings
                {
                    Enabled = CheckInsEnabled,
                    RunTimeMinutes = (int)(RunTimeMinutes ?? 0),
                    QuietTimeMinutes = (int)(QuietTimeMinutes ?? 0),
                    Message = string.IsNullOrWhiteSpace(CheckInMessage) ? CheckInSettings.DefaultMessage : CheckInMessage,
                    Notify = NotifyOnCheckIn,
                }
                : null,
        };
        _close();
        await _tab.ApplyOverridesAsync(previous);
    }

    [RelayCommand]
    private void UseDefaults()
    {
        SelectedModel = ModelChoices[0];
        SelectedEffort = EffortChoices[0];
        SelectedMode = ModeChoices[0];
        SelectedMonitor = MonitorChoices[0];
        UseCustomCheckIns = false;
    }

    [RelayCommand]
    private void Cancel() => _close();
}
