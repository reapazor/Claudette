using System.Collections.ObjectModel;
using Claudette.App.Services;
using Claudette.Core.Diffs;
using Claudette.Core.Installation;
using Claudette.Core.Library;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A diff tool choice in Settings: built-in, an installed preset, or a custom command.</summary>
public sealed record DiffToolOption(string Kind, string? PresetId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A retention option in a dropdown.</summary>
public sealed record RetentionChoice(RetentionPeriod Period)
{
    public override string ToString() => Period.Label();
}

/// <summary>One quick suffix being edited in Settings.</summary>
public sealed partial class QuickSuffixEditor(QuickSuffix suffix, Action changed) : ObservableObject
{
    public QuickSuffix Suffix { get; } = suffix;

    public string Label
    {
        get => Suffix.Label;
        set
        {
            if (Suffix.Label != value)
            {
                Suffix.Label = value;
                OnPropertyChanged();
                changed();
            }
        }
    }

    public string Text
    {
        get => Suffix.Text;
        set
        {
            if (Suffix.Text != value)
            {
                Suffix.Text = value;
                OnPropertyChanged();
                changed();
            }
        }
    }
}

/// <summary>
/// The Settings window (DESIGN.md §14). Changes apply immediately; there's no Save button. Categories for later
/// milestones (usage, diff tool, notifications, processes, keyboard, Perforce) arrive with those milestones.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    public static readonly IReadOnlyList<string> AllCategories =
        ["General", "Sessions", "Processes", "Claude Code", "New tabs", "Appearance", "Usage", "Quick suffixes", "Check-ins", "Diff tool", "Advanced"];

    private readonly AppServices _services;
    private readonly AppSettings _settings;

    public SettingsViewModel(AppServices services, string? accountText)
    {
        _services = services;
        _settings = services.Settings;
        AccountText = accountText ?? "Not signed in";
        foreach (var suffix in _settings.QuickSuffixes)
        {
            Suffixes.Add(new QuickSuffixEditor(suffix, Save));
        }
        SelectedCategory = AllCategories[0];
    }

    public IReadOnlyList<string> Categories => AllCategories;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneral), nameof(IsClaudeCode), nameof(IsNewTabs), nameof(IsAppearance), nameof(IsSessions), nameof(IsCheckIns), nameof(IsQuickSuffixes), nameof(IsAdvanced))]
    [NotifyPropertyChangedFor(nameof(IsUsage), nameof(IsProcesses), nameof(IsDiffTool))]
    public partial string SelectedCategory { get; set; }

    public bool IsUsage => SelectedCategory == "Usage";

    public bool IsProcesses => SelectedCategory == "Processes";

    public bool IsDiffTool => SelectedCategory == "Diff tool";

    public bool IsGeneral => SelectedCategory == "General";

    public bool IsClaudeCode => SelectedCategory == "Claude Code";

    public bool IsNewTabs => SelectedCategory == "New tabs";

    public bool IsAppearance => SelectedCategory == "Appearance";

    public bool IsSessions => SelectedCategory == "Sessions";

    public bool IsCheckIns => SelectedCategory == "Check-ins";

    public bool IsQuickSuffixes => SelectedCategory == "Quick suffixes";

    public bool IsAdvanced => SelectedCategory == "Advanced";

    // ---- General ---------------------------------------------------------------------------------------------

    public bool ConfirmCloseWorkingTab
    {
        get => _settings.General.ConfirmCloseWorkingTab;
        set => Set(value, v => _settings.General.ConfirmCloseWorkingTab = v);
    }

    public bool RenameInClaudeCode
    {
        get => _settings.General.RenameInClaudeCode;
        set => Set(value, v => _settings.General.RenameInClaudeCode = v);
    }

    [RelayCommand]
    private void ResetGeneral()
    {
        _settings.General = new GeneralSettings();
        Save();
        OnPropertyChanged(nameof(ConfirmCloseWorkingTab));
        OnPropertyChanged(nameof(RenameInClaudeCode));
    }

    // ---- Claude Code -----------------------------------------------------------------------------------------

    public string AccountText { get; }

    public string InstalledText => _services.Install is { } install
        ? $"Claude Code {install.Version} at {install.Path}. Last tested with {ClaudeLocator.LastTestedVersion}{(install.Version > ClaudeLocator.LastTestedVersion ? " (this version is newer)" : "")}."
        : "Claude Code wasn't found.";

    /// <summary>Null (empty) finds Claude Code automatically. Takes effect the next time Claudette starts.</summary>
    public string ClaudePath
    {
        get => _settings.ClaudeCode.ClaudePath ?? "";
        set => Set(value, v => _settings.ClaudeCode.ClaudePath = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    [RelayCommand]
    private async Task BrowseClaudePathAsync()
    {
        if (await _services.Platform.PickFileAsync("Choose the claude executable") is { } path)
        {
            ClaudePath = path;
            OnPropertyChanged(nameof(ClaudePath));
        }
    }

    // ---- New tabs ----------------------------------------------------------------------------------------------

    public IReadOnlyList<Choice> ModelChoices { get; } =
        [new(null, "Claude Code's default"), new("opus", "Opus"), new("sonnet", "Sonnet"), new("haiku", "Haiku"), new("fable", "Fable")];

    public IReadOnlyList<Choice> EffortChoices { get; } =
        [new(null, "The model's default"), new("low", "low"), new("medium", "medium"), new("high", "high"), new("xhigh", "xhigh"), new("max", "max")];

    public IReadOnlyList<Choice> ModeChoices { get; } =
        [new(null, "Claude Code's default"), .. PermissionModeInfo.Choices.Select(m => new Choice(m.Value, m.Label))];

    public Choice DefaultModel
    {
        get => ModelChoices.FirstOrDefault(c => c.Value == _settings.NewTabs.DefaultModel) ?? ModelChoices[0];
        set => Set(value?.Value, v => _settings.NewTabs.DefaultModel = v);
    }

    public Choice DefaultEffort
    {
        get => EffortChoices.FirstOrDefault(c => c.Value == _settings.NewTabs.DefaultEffort) ?? EffortChoices[0];
        set => Set(value?.Value, v => _settings.NewTabs.DefaultEffort = v);
    }

    public Choice DefaultMode
    {
        get => ModeChoices.FirstOrDefault(c => c.Value == _settings.NewTabs.DefaultPermissionMode) ?? ModeChoices[0];
        set => Set(value?.Value, v => _settings.NewTabs.DefaultPermissionMode = v);
    }

    public decimal? RecentFolderLimit
    {
        get => _settings.NewTabs.RecentFolderLimit;
        set => Set(value, v => _settings.NewTabs.RecentFolderLimit = Math.Clamp((int)(v ?? 20), 1, 100));
    }

    [RelayCommand]
    private void ClearRecentFolders()
    {
        _services.State.RecentFolders.RemoveAll(r => !FolderHistory.IsFavorite(_services.State, r.Path));
        _services.SaveState();
    }

    // ---- Appearance ------------------------------------------------------------------------------------------

    public IReadOnlyList<ThemeChoice> Themes { get; } = [ThemeChoice.System, ThemeChoice.Light, ThemeChoice.Dark];

    public ThemeChoice Theme
    {
        get => _settings.Appearance.Theme;
        set => Set(value, v => _settings.Appearance.Theme = v);
    }

    public decimal? ConversationFontSize
    {
        get => (decimal)_settings.Appearance.ConversationFontSize;
        set => Set(value, v => _settings.Appearance.ConversationFontSize = Math.Clamp((double)(v ?? 14), 9, 28));
    }

    public decimal? CodeFontSize
    {
        get => (decimal)_settings.Appearance.CodeFontSize;
        set => Set(value, v => _settings.Appearance.CodeFontSize = Math.Clamp((double)(v ?? 13), 8, 28));
    }

    public bool ExpandThinking
    {
        get => _settings.Appearance.ExpandThinking;
        set => Set(value, v => _settings.Appearance.ExpandThinking = v);
    }

    // ---- Sessions ------------------------------------------------------------------------------------------------

    public bool RestoreUnpinnedTabs
    {
        get => _settings.Sessions.RestoreUnpinnedTabs;
        set => Set(value, v => _settings.Sessions.RestoreUnpinnedTabs = v);
    }

    public IReadOnlyList<RetentionChoice> LibraryRetentionChoices { get; } =
        [.. new[] { RetentionPeriod.OneMonth, RetentionPeriod.OneYear, RetentionPeriod.Forever }.Select(p => new RetentionChoice(p))];

    public RetentionChoice KeepLibrarySessions
    {
        get => LibraryRetentionChoices.FirstOrDefault(c => c.Period == _settings.Sessions.KeepLibrarySessions) ?? LibraryRetentionChoices[^1];
        set => Set(value?.Period ?? RetentionPeriod.Forever, v => _settings.Sessions.KeepLibrarySessions = v);
    }

    /// <summary>Shown in History and in "in use on …" notices. Empty uses the computer name.</summary>
    public string MachineName
    {
        get => _settings.Sessions.MachineName ?? "";
        set => Set(value, v => _settings.Sessions.MachineName = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    public string MachineNamePlaceholder => Environment.MachineName;

    // ---- Session library (DESIGN.md §9) ----------------------------------------------------------------------------

    public string LibraryFolder => _services.Library.LibraryFolder;

    public bool IsDefaultLibraryFolder => _settings.Sessions.LibraryFolder is null;

    /// <summary>A folder the user picked, waiting for them to confirm it's fine to sync transcripts there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmingLibraryFolder))]
    public partial string? PendingLibraryFolder { get; set; }

    public bool IsConfirmingLibraryFolder => PendingLibraryFolder is not null;

    [ObservableProperty]
    public partial string LibraryConfirmText { get; set; } = "";

    [ObservableProperty]
    public partial string? LibraryStatus { get; set; }

    private bool _moveLibrary;

    /// <summary>Browse…: use another folder from now on.</summary>
    [RelayCommand]
    private Task BrowseLibraryFolderAsync() => ChooseLibraryFolderAsync(move: false);

    /// <summary>Move library…: copy the existing sessions to the new folder, then use it.</summary>
    [RelayCommand]
    private Task MoveLibraryAsync() => ChooseLibraryFolderAsync(move: true);

    private async Task ChooseLibraryFolderAsync(bool move)
    {
        if (await _services.Platform.PickFolderAsync(move ? "Move the session library to" : "Choose a folder for the session library") is not { } folder)
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
        var old = _services.Library.LibraryFolder;
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
        _settings.Sessions.LibraryFolder = folder;
        Save();
        LibraryStatus = _moveLibrary && folder is not null ? $"Copied the sessions to {folder}. The old folder was left as it was." : null;
        _moveLibrary = false;
        OnPropertyChanged(nameof(LibraryFolder));
        OnPropertyChanged(nameof(IsDefaultLibraryFolder));
    }

    // ---- Settings sync (DESIGN.md §14) -------------------------------------------------------------------------------

    public bool SyncSettings
    {
        get => _settings.Sessions.SyncSettings;
        set
        {
            if (value == _settings.Sessions.SyncSettings)
            {
                return;
            }
            if (value && _services.Library.HasSyncedSettings())
            {
                // The library already has settings from another machine: ask which ones win.
                IsChoosingSyncSource = true;
                OnPropertyChanged();
                return;
            }
            _settings.Sessions.SyncSettings = value;
            Save();
            OnPropertyChanged();
            if (value)
            {
                _ = _services.Library.PublishAllSettingsAsync();
            }
        }
    }

    [ObservableProperty]
    public partial bool IsChoosingSyncSource { get; set; }

    [RelayCommand]
    private async Task UseSyncedSettingsAsync()
    {
        IsChoosingSyncSource = false;
        _settings.Sessions.SyncSettings = true;
        _services.State.SettingsSync = null;
        Save();
        await _services.Library.SyncSettingsAsync();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private async Task ReplaceSyncedSettingsAsync()
    {
        IsChoosingSyncSource = false;
        _settings.Sessions.SyncSettings = true;
        Save();
        await _services.Library.PublishAllSettingsAsync();
        OnPropertyChanged(nameof(SyncSettings));
    }

    [RelayCommand]
    private void CancelSyncChoice()
    {
        IsChoosingSyncSource = false;
        OnPropertyChanged(nameof(SyncSettings));
    }

    // ---- Diff tool (DESIGN.md §8, "External diff tool") --------------------------------------------------------

    /// <summary>Built-in, each preset whose tool is installed here, and a custom command.</summary>
    public IReadOnlyList<DiffToolOption> DiffToolOptions => _diffToolOptions ??=
    [
        new DiffToolOption("builtIn", null, "Built-in diff view"),
        .. DiffToolDetector.Detect(FileProbe.Instance).Select(d => new DiffToolOption("preset", d.Preset.Id, $"{d.Preset.Name}  ({d.ExecutablePath})")),
        new DiffToolOption("custom", null, "Custom command…"),
    ];

    private IReadOnlyList<DiffToolOption>? _diffToolOptions;

    public DiffToolOption SelectedDiffTool
    {
        get
        {
            var settings = _settings.DiffTool;
            return DiffToolOptions.FirstOrDefault(o => o.Kind == settings.Kind && (o.Kind != "preset" || o.PresetId == settings.PresetId))
                ?? DiffToolOptions[0];
        }
        set
        {
            if (value is null)
            {
                return;
            }
            _settings.DiffTool.Kind = value.Kind;
            _settings.DiffTool.PresetId = value.PresetId;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomDiffTool));
            OnPropertyChanged(nameof(CanTestDiffTool));
            DiffToolTestResult = null;
        }
    }

    public bool IsCustomDiffTool => _settings.DiffTool.Kind == "custom";

    public bool CanTestDiffTool => _settings.DiffTool.Kind != "builtIn";

    public string CustomDiffCommand
    {
        get => _settings.DiffTool.CustomCommand ?? "";
        set
        {
            _settings.DiffTool.CustomCommand = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CustomDiffCommandError));
        }
    }

    public string? CustomDiffCommandError =>
        _settings.DiffTool.CustomCommand is { } command && !DiffToolCommand.TryParse(command, out _, out var error) ? error : null;

    [ObservableProperty]
    public partial string? DiffToolTestResult { get; set; }

    /// <summary>Opens a sample diff so the user can check the tool works.</summary>
    [RelayCommand]
    private async Task TestDiffToolAsync()
    {
        var settings = _settings.DiffTool;
        var choice = settings.Kind == "custom"
            ? new DiffToolChoice(DiffToolKind.Custom, CustomCommand: settings.CustomCommand)
            : new DiffToolChoice(DiffToolKind.Preset, settings.PresetId);
        try
        {
            await new DiffToolLauncher(_services.Launcher, _services.Time).TestAsync(choice, Path.Combine(_services.Paths.DiffTempDirectory, "test"));
            DiffToolTestResult = "Opened a sample diff. If nothing appeared, check the command.";
        }
        catch (Exception ex)
        {
            DiffToolTestResult = $"Couldn't open it: {ex.Message}";
        }
    }

    // ---- Processes (DESIGN.md §4, "Process monitor") ------------------------------------------------------------

    public bool ShowProcessMonitor
    {
        get => _settings.Processes.ShowMonitor;
        set => Set(value, v => _settings.Processes.ShowMonitor = v);
    }

    public decimal? ProcessRefreshSeconds
    {
        get => _settings.Processes.RefreshSeconds;
        set => Set(value, v => _settings.Processes.RefreshSeconds = Math.Clamp((int)(v ?? 2), 1, 60));
    }

    public bool ShowCommandLines
    {
        get => _settings.Processes.ShowCommandLines;
        set => Set(value, v => _settings.Processes.ShowCommandLines = v);
    }

    [RelayCommand]
    private void ResetProcesses()
    {
        _settings.Processes = new ProcessSettings();
        Save();
        OnPropertyChanged(nameof(ShowProcessMonitor));
        OnPropertyChanged(nameof(ProcessRefreshSeconds));
        OnPropertyChanged(nameof(ShowCommandLines));
    }

    // ---- Usage (DESIGN.md §6) -------------------------------------------------------------------------------------

    public decimal? WarnPercent
    {
        get => (decimal)_settings.Usage.WarnPercent;
        set => Set(value, v => _settings.Usage.WarnPercent = Math.Clamp((double)(v ?? 75), 1, 100));
    }

    public decimal? CriticalPercent
    {
        get => (decimal)_settings.Usage.CriticalPercent;
        set => Set(value, v => _settings.Usage.CriticalPercent = Math.Clamp((double)(v ?? 90), 1, 100));
    }

    public decimal? BurnRateWindowMinutes
    {
        get => _settings.Usage.BurnRateWindowMinutes;
        set => Set(value, v => _settings.Usage.BurnRateWindowMinutes = Math.Clamp((int)(v ?? 30), 5, 300));
    }

    public bool ShowModelMeters
    {
        get => _settings.Usage.ShowModelMeters;
        set => Set(value, v => _settings.Usage.ShowModelMeters = v);
    }

    public bool UseUsageCommandFallback
    {
        get => _settings.Usage.UseUsageCommandFallback;
        set => Set(value, v => _settings.Usage.UseUsageCommandFallback = v);
    }

    public IReadOnlyList<RetentionChoice> HistoryRetentionChoices { get; } =
        [.. Enum.GetValues<RetentionPeriod>().Select(p => new RetentionChoice(p))];

    public RetentionChoice KeepUsageHistory
    {
        get => HistoryRetentionChoices.FirstOrDefault(c => c.Period == _settings.Usage.KeepHistory) ?? HistoryRetentionChoices[2];
        set => Set(value?.Period ?? RetentionPeriod.OneMonth, v => _settings.Usage.KeepHistory = v);
    }

    /// <summary>"Clear usage history" waiting for confirmation (DESIGN.md §6, "Usage history").</summary>
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
        await _services.ClearUsageHistoryAsync(AlsoResetTabTotals);
        UsageClearedText = AlsoResetTabTotals ? "Usage history and tab token totals cleared." : "Usage history cleared.";
    }

    [RelayCommand]
    private void CancelClearUsage() => IsConfirmingClearUsage = false;

    [RelayCommand]
    private void ResetUsage()
    {
        var keep = _settings.Usage.KeepHistory;
        _settings.Usage = new UsageSettings { KeepHistory = keep };
        Save();
        OnPropertyChanged(nameof(WarnPercent));
        OnPropertyChanged(nameof(CriticalPercent));
        OnPropertyChanged(nameof(BurnRateWindowMinutes));
        OnPropertyChanged(nameof(ShowModelMeters));
        OnPropertyChanged(nameof(UseUsageCommandFallback));
    }

    // ---- Check-ins ------------------------------------------------------------------------------------------------

    public bool CheckInsEnabled
    {
        get => _settings.CheckIns.Enabled;
        set => Set(value, v => _settings.CheckIns.Enabled = v);
    }

    public decimal? CheckInRunTime
    {
        get => _settings.CheckIns.RunTimeMinutes;
        set => Set(value, v => _settings.CheckIns.RunTimeMinutes = Math.Max(0, (int)(v ?? 0)));
    }

    public decimal? CheckInQuietTime
    {
        get => _settings.CheckIns.QuietTimeMinutes;
        set => Set(value, v => _settings.CheckIns.QuietTimeMinutes = Math.Max(0, (int)(v ?? 0)));
    }

    public string CheckInMessage
    {
        get => _settings.CheckIns.Message;
        set => Set(value, v => _settings.CheckIns.Message = string.IsNullOrWhiteSpace(v) ? CheckInSettings.DefaultMessage : v);
    }

    [RelayCommand]
    private void ResetCheckIns()
    {
        _settings.CheckIns = new CheckInSettings();
        Save();
        OnPropertyChanged(nameof(CheckInsEnabled));
        OnPropertyChanged(nameof(CheckInRunTime));
        OnPropertyChanged(nameof(CheckInQuietTime));
        OnPropertyChanged(nameof(CheckInMessage));
    }

    // ---- Quick suffixes ------------------------------------------------------------------------------------------

    public ObservableCollection<QuickSuffixEditor> Suffixes { get; } = [];

    [RelayCommand]
    private void AddSuffix()
    {
        var suffix = new QuickSuffix { Label = "New suffix", Text = "" };
        _settings.QuickSuffixes.Add(suffix);
        Suffixes.Add(new QuickSuffixEditor(suffix, Save));
        Save();
    }

    [RelayCommand]
    private void RemoveSuffix(QuickSuffixEditor? editor)
    {
        if (editor is not null)
        {
            _settings.QuickSuffixes.Remove(editor.Suffix);
            Suffixes.Remove(editor);
            Save();
        }
    }

    [RelayCommand]
    private void MoveSuffixUp(QuickSuffixEditor? editor) => MoveSuffix(editor, -1);

    [RelayCommand]
    private void MoveSuffixDown(QuickSuffixEditor? editor) => MoveSuffix(editor, 1);

    private void MoveSuffix(QuickSuffixEditor? editor, int offset)
    {
        if (editor is null)
        {
            return;
        }
        var from = Suffixes.IndexOf(editor);
        var to = from + offset;
        if (to < 0 || to >= Suffixes.Count)
        {
            return;
        }
        Suffixes.Move(from, to);
        _settings.QuickSuffixes = Suffixes.Select(e => e.Suffix).ToList();
        Save();
    }

    [RelayCommand]
    private void ResetSuffixes()
    {
        _settings.QuickSuffixes = QuickSuffix.Defaults();
        Suffixes.Clear();
        foreach (var suffix in _settings.QuickSuffixes)
        {
            Suffixes.Add(new QuickSuffixEditor(suffix, Save));
        }
        Save();
    }

    // ---- Advanced --------------------------------------------------------------------------------------------------

    public string ExtraArguments
    {
        get => _settings.Advanced.ExtraArguments;
        set => Set(value, v => _settings.Advanced.ExtraArguments = v ?? "");
    }

    [RelayCommand]
    private Task OpenDataFolderAsync() => _services.Platform.RevealFolderAsync(_services.Paths.DataDirectory);

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private void Set<T>(T value, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        apply(value);
        OnPropertyChanged(property);
        Save();
    }

    private void Save() => _services.SaveSettings();
}
