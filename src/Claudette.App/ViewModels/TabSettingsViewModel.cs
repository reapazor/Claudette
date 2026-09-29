using Claudette.App.Services;
using Claudette.Core.Sessions;
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
/// "Tab settings…": settings for one tab that replace the defaults (DESIGN.md §14, "Per-tab overrides"). The folder's
/// custom project actions are in Settings, on the tab's Actions page, which a button here opens.
/// </summary>
public sealed partial class TabSettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly TabViewModel _tab;
    private readonly Action _close;

    private readonly ShellViewModel? _shell;

    public TabSettingsViewModel(AppServices services, TabViewModel tab, Action close, ShellViewModel? shell = null)
    {
        _shell = shell;
        _services = services;
        _tab = tab;
        _close = close;
        var overrides = tab.State.Overrides;
        var defaults = services.Settings;

        // What the tab's Claude Code offers, else what Claude Code last offered on this machine (DESIGN.md §14).
        IReadOnlyList<ModelInfo> models = tab.Models.Count > 0 ? tab.Models : services.State.KnownModels;
        ModelChoices = [new Choice(null, $"Default ({defaults.NewTabs.DefaultModel ?? "Claude Code's default"})"), .. models.Select(m => new Choice(m.Value, m.DisplayName))];
        if (overrides.Model is { } model && ModelChoices.All(c => c.Value != model))
        {
            ModelChoices = [.. ModelChoices, new Choice(model, model)];
        }
        var levels = models.SelectMany(m => m.SupportedEffortLevels).Distinct(StringComparer.Ordinal).ToList();
        if (levels.Count == 0)
        {
            levels = ["low", "medium", "high", "xhigh", "max"];
        }
        if (overrides.Effort is { } effort && !levels.Contains(effort))
        {
            levels.Add(effort);
        }
        EffortChoices = [new Choice(null, $"Default ({defaults.NewTabs.DefaultEffort ?? "model default"})"), .. levels.Select(e => new Choice(e, e))];
        // Claude Code's default is known once the tab has started and read its settings files (DESIGN.md §7).
        var defaultMode = defaults.NewTabs.DefaultPermissionMode ?? tab.ClaudeCodeStartingMode;
        ModeChoices =
        [
            new Choice(null, $"Default ({(defaultMode is null ? "Claude Code's default" : PermissionModeInfo.Label(defaultMode))})"),
            .. PermissionModeInfo.Choices.Select(m => new Choice(m.Value, m.Label)),
        ];
        MonitorChoices = [new Choice(null, $"Default ({(defaults.Processes.ShowMonitor ? "on" : "off")})"), new Choice(On, "On"), new Choice(Off, "Off")];
        AutoContinueChoices = [new Choice(null, $"Default ({(defaults.Usage.ContinueAfterLimitReset ? "on" : "off")})"), new Choice(On, "On"), new Choice(Off, "Off")];

        SelectedModel = ModelChoices.First(c => c.Value == overrides.Model);
        SelectedEffort = EffortChoices.FirstOrDefault(c => c.Value == overrides.Effort) ?? EffortChoices[0];
        SelectedMode = ModeChoices.FirstOrDefault(c => c.Value == overrides.PermissionMode) ?? ModeChoices[0];
        SelectedMonitor = MonitorChoices.First(c => c.Value == overrides.ShowProcessMonitor switch { true => On, false => Off, null => null });
        SelectedAutoContinue = AutoContinueChoices.First(c => c.Value == overrides.ContinueAfterLimitReset switch { true => On, false => Off, null => null });

        var checkIns = overrides.CheckIns ?? defaults.CheckIns;
        UseCustomCheckIns = overrides.CheckIns is not null;
        CheckInsEnabled = checkIns.Enabled;
        RunTimeMinutes = checkIns.RunTimeMinutes;
        QuietTimeMinutes = checkIns.QuietTimeMinutes;
        CheckInMessage = checkIns.Message;
        NotifyOnCheckIn = checkIns.Notify;
        SyncToLibrary = tab.State.SyncToLibrary;
        RemoteControl = tab.State.RemoteControl;
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

    /// <summary>Continuing a task a usage limit stopped, once it resets (DESIGN.md §6, "Continuing after a limit resets").</summary>
    public IReadOnlyList<Choice> AutoContinueChoices { get; }

    [ObservableProperty]
    public partial Choice SelectedAutoContinue { get; set; }

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

    /// <summary>
    /// <b>Sync to other machines</b> (DESIGN.md §9, "Session library"). The tab's own state rather than an override, so
    /// <b>Use defaults</b> leaves it as it is.
    /// </summary>
    [ObservableProperty]
    public partial bool SyncToLibrary { get; set; }

    /// <summary>
    /// <b>Connect to the Claude app</b> (DESIGN.md §18, "Remote Control"). The tab's own state like
    /// <see cref="SyncToLibrary"/>, so <b>Use defaults</b> leaves it as it is.
    /// </summary>
    [ObservableProperty]
    public partial bool RemoteControl { get; set; }

    /// <summary>The account can use Remote Control, or the tab is on and can be turned off.</summary>
    public bool CanChangeRemoteControl => _tab.CanToggleRemoteControl;

    /// <summary>Why the switch is disabled, or null.</summary>
    public string? RemoteControlUnavailableText => CanChangeRemoteControl ? null : _services.RemoteControl.UnavailableReason;

    public bool HasRemoteControlUnavailableText => RemoteControlUnavailableText is not null;

    // ---- Project actions (DESIGN.md §18, "Custom actions"): in Settings, on the tab's Actions page --------------------

    /// <summary>"Project actions are in Settings → NightOwl → Actions": the group is named after the tab's project.</summary>
    public string ProjectActionsText => $"Project actions are in Settings → {ProjectSettingsViewModel.GroupName(_tab)} → Actions.";

    /// <summary>Opens Settings on the tab's Actions page. This dialog stays open behind it, with nothing applied yet.</summary>
    [RelayCommand]
    private Task OpenProjectActionsAsync() =>
        _shell?.OpenProjectSettingsAsync(_tab, SettingsViewModel.ActionsPage) ?? Task.CompletedTask;

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
            ContinueAfterLimitReset = SelectedAutoContinue.Value switch { On => true, Off => false, _ => null },
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
        _tab.SetSyncToLibrary(SyncToLibrary);
        await _tab.SetRemoteControlAsync(RemoteControl);
        await _tab.ApplyOverridesAsync(previous);
    }

    [RelayCommand]
    private void UseDefaults()
    {
        SelectedModel = ModelChoices[0];
        SelectedEffort = EffortChoices[0];
        SelectedMode = ModeChoices[0];
        SelectedMonitor = MonitorChoices[0];
        SelectedAutoContinue = AutoContinueChoices[0];
        UseCustomCheckIns = false;
    }

    [RelayCommand]
    private void Cancel() => _close();
}
