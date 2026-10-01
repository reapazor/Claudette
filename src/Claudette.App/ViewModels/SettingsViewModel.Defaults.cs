using System.Collections.ObjectModel;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A favorite folder in Settings → New tabs, in the order the new tab picker lists them.</summary>
public sealed record FavoriteFolderRow(string Path, string Name);

/// <summary>
/// Settings (DESIGN.md §14): <b>Reset to defaults</b> for the categories that don't have their own section, favorite
/// folders, fonts, and model and effort lists taken from what Claude Code offers.
/// </summary>
public sealed partial class SettingsViewModel
{
    // ---- Reset to defaults ---------------------------------------------------------------------------------------

    /// <summary>
    /// Sessions → <b>Reset to defaults</b>. The library folder and settings sync are left as they are: changing either
    /// moves where sessions and settings live, which is its own decision with its own buttons.
    /// </summary>
    [RelayCommand]
    private void ResetSessions()
    {
        var defaults = new SessionSettings();
        _settings.Sessions.RestoreUnpinnedTabs = defaults.RestoreUnpinnedTabs;
        _settings.Sessions.MachineName = defaults.MachineName;
        _settings.Sessions.KeepLibrarySessions = defaults.KeepLibrarySessions;
        _settings.Sessions.SyncNewTabs = defaults.SyncNewTabs;
        Save();
        OnPropertyChanged(nameof(RestoreUnpinnedTabs));
        OnPropertyChanged(nameof(MachineName));
        OnPropertyChanged(nameof(KeepLibrarySessions));
        OnPropertyChanged(nameof(SyncNewTabs));
    }

    [RelayCommand]
    private void ResetClaudeCode()
    {
        _settings.ClaudeCode = new ClaudeCodeSettings();
        Save();
        OnPropertyChanged(nameof(CheckForUpdates));
        OnPropertyChanged(nameof(ClaudePath));
        OnPropertyChanged(nameof(UseLoginShellEnvironment));
        OnPropertyChanged(nameof(ConnectNewTabsToClaudeApp));
        OnPropertyChanged(nameof(KeepAwakeWhileConnected));
        OnPropertyChanged(nameof(FallbackModel));
        OnPropertyChanged(nameof(KeepFileCheckpoints));
        OnPropertyChanged(nameof(ShowAllHookRuns));
        OnPropertyChanged(nameof(AskBeforeUsingFolderSettings));
    }

    /// <summary>New tabs → <b>Reset to defaults</b>. Favorites and recent folders are this machine's data, not settings.</summary>
    [RelayCommand]
    private void ResetNewTabs()
    {
        _settings.NewTabs = new NewTabSettings();
        Save();
        OnPropertyChanged(nameof(DefaultModel));
        OnPropertyChanged(nameof(DefaultEffort));
        OnPropertyChanged(nameof(DefaultMode));
        OnPropertyChanged(nameof(RecentFolderLimit));
    }

    [RelayCommand]
    private void ResetAppearance()
    {
        _settings.Appearance = new AppearanceSettings();
        Save();
        DetailedUsageHeader = false;
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(Style));
        OnPropertyChanged(nameof(ConversationFontSize));
        OnPropertyChanged(nameof(CodeFontSize));
        OnPropertyChanged(nameof(ConversationFont));
        OnPropertyChanged(nameof(CodeFont));
        OnPropertyChanged(nameof(ExpandThinking));
        OnPropertyChanged(nameof(FunWorkingWords));
        OnPropertyChanged(nameof(ShowToolInWorkingLine));
        OnPropertyChanged(nameof(ShowContextOnTabs));
        OnPropertyChanged(nameof(Density));
    }

    [RelayCommand]
    private void ResetDiffTool()
    {
        _settings.DiffTool = new DiffToolSettings();
        Save();
        OnPropertyChanged(nameof(SelectedDiffTool));
        OnPropertyChanged(nameof(CustomDiffCommand));
        OnPropertyChanged(nameof(IsCustomDiffTool));
        OnPropertyChanged(nameof(CanTestDiffTool));
    }

    [RelayCommand]
    private void ResetAdvanced()
    {
        _settings.Advanced = new AdvancedSettings();
        Save();
        OnPropertyChanged(nameof(ExtraArguments));
        OnPropertyChanged(nameof(LogProtocol));
    }

    // ---- Favorite folders (DESIGN.md §4, "Opening a tab") -----------------------------------------------------------

    public ObservableCollection<FavoriteFolderRow> Favorites { get; } = [];

    public bool HasFavorites => Favorites.Count > 0;

    private void LoadFavorites()
    {
        Favorites.Clear();
        foreach (var path in _services.State.FavoriteFolders)
        {
            Favorites.Add(new FavoriteFolderRow(path, System.IO.Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : path));
        }
        OnPropertyChanged(nameof(HasFavorites));
    }

    [RelayCommand]
    private async Task AddFavoriteAsync()
    {
        if (await _services.Platform.PickFolderAsync("Add a favorite folder") is { } folder)
        {
            FolderHistory.SetFavorite(_services.State, folder, true);
            _services.SaveState();
            LoadFavorites();
        }
    }

    [RelayCommand]
    private void RemoveFavorite(FavoriteFolderRow? row)
    {
        if (row is not null)
        {
            FolderHistory.SetFavorite(_services.State, row.Path, false);
            _services.SaveState();
            LoadFavorites();
        }
    }

    [RelayCommand]
    private void MoveFavoriteUp(FavoriteFolderRow? row) => MoveFavorite(row, -1);

    [RelayCommand]
    private void MoveFavoriteDown(FavoriteFolderRow? row) => MoveFavorite(row, 1);

    private void MoveFavorite(FavoriteFolderRow? row, int by)
    {
        var favorites = _services.State.FavoriteFolders;
        var index = row is null ? -1 : favorites.IndexOf(row.Path);
        var target = index + by;
        if (index < 0 || target < 0 || target >= favorites.Count)
        {
            return;
        }
        (favorites[index], favorites[target]) = (favorites[target], favorites[index]);
        _services.SaveState();
        LoadFavorites();
    }

    // ---- Fonts (DESIGN.md §14, "Appearance") ----------------------------------------------------------------------

    /// <summary>The fonts installed here, to pick from; any other name can be typed.</summary>
    public IReadOnlyList<string> InstalledFonts => field ??= _services.Platform.InstalledFonts();

    /// <summary>Empty for Claudette's own font.</summary>
    public string ConversationFont
    {
        get => _settings.Appearance.ConversationFont ?? "";
        set => Set(value, v => _settings.Appearance.ConversationFont = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    /// <summary>Empty for the default monospace fonts.</summary>
    public string CodeFont
    {
        get => _settings.Appearance.CodeFont ?? "";
        set => Set(value, v => _settings.Appearance.CodeFont = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
    }

    // ---- Models and effort levels from Claude Code (DESIGN.md §14, "New tabs") ----------------------------------

    /// <summary>Used until a session has told Claudette which models the installed Claude Code offers.</summary>
    private static readonly IReadOnlyList<Choice> FallbackModels =
        [new("opus", "Opus"), new("sonnet", "Sonnet"), new("haiku", "Haiku"), new("fable", "Fable")];

    private static readonly IReadOnlyList<string> FallbackEffortLevels = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>What Claude Code offered in its last <c>initialize</c> reply, plus the current default if it's not among them.</summary>
    private IReadOnlyList<Choice> BuildModelChoices()
    {
        var known = _services.State.KnownModels;
        IReadOnlyList<Choice> models = known.Count > 0 ? [.. known.Select(m => new Choice(m.Value, m.DisplayName))] : FallbackModels;
        var choices = new List<Choice> { new(null, "Claude Code's default") };
        choices.AddRange(models);
        if (_settings.NewTabs.DefaultModel is { } current && choices.All(c => c.Value != current))
        {
            choices.Add(new Choice(current, current));
        }
        return choices;
    }

    /// <summary>Every effort level a model offers, in Claude Code's order.</summary>
    private IReadOnlyList<Choice> BuildEffortChoices()
    {
        var levels = _services.State.KnownModels.SelectMany(m => m.SupportedEffortLevels).Distinct(StringComparer.Ordinal).ToList();
        if (levels.Count == 0)
        {
            levels = [.. FallbackEffortLevels];
        }
        var choices = new List<Choice> { new(null, "The model's default") };
        choices.AddRange(levels.Select(l => new Choice(l, l)));
        if (_settings.NewTabs.DefaultEffort is { } current && choices.All(c => c.Value != current))
        {
            choices.Add(new Choice(current, current));
        }
        return choices;
    }
}
