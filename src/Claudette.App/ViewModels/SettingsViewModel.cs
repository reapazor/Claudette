using System.Collections.ObjectModel;
using Claudette.App.Services;
using Claudette.Core.Installation;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

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
        ["General", "Claude Code", "New tabs", "Appearance", "Sessions", "Check-ins", "Quick suffixes", "Advanced"];

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
    public partial string SelectedCategory { get; set; }

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
        [new(null, "Claude Code's default"), .. TabViewModel.PermissionModes.Select(m => new Choice(m, m))];

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
