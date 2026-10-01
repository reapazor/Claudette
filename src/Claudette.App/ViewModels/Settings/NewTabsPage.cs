using Claudette.Core;
using System.Collections.ObjectModel;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels.Settings;

/// <summary>A favorite folder in Settings → New tabs, in the order the new tab picker lists them.</summary>
public sealed record FavoriteFolderRow(string Path, string Name);

/// <summary>
/// Settings → New tabs (DESIGN.md §14): the model, effort and permission mode new tabs start with, from what Claude
/// Code offers, the recent folders, and the favorite folders (DESIGN.md §4, "Opening a tab").
/// </summary>
public sealed partial class NewTabsPage : SettingsPage
{
    /// <summary>Used until a session has told Claudette which models the installed Claude Code offers.</summary>
    private static readonly IReadOnlyList<Choice> FallbackModels =
        [new("opus", "Opus"), new("sonnet", "Sonnet"), new("haiku", "Haiku"), new("fable", "Fable")];

    private static readonly IReadOnlyList<string> FallbackEffortLevels = ["low", "medium", "high", "xhigh", "max"];

    public NewTabsPage(SettingsContext context) : base(context, SettingsCategory.NewTabs) => LoadFavorites();

    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Default model"),
        Entry("Default effort"),
        Entry("Default permission mode"),
        Entry("Recent folders to keep"),
        Entry("Clear recent folders"),
        Entry("Favorite folders"),
    ];

    // ---- Models and effort levels from Claude Code (DESIGN.md §14, "New tabs") ----------------------------------

    public IReadOnlyList<Choice> ModelChoices => field ??= BuildModelChoices();

    public IReadOnlyList<Choice> EffortChoices => field ??= BuildEffortChoices();

    /// <summary>
    /// "Claude Code's default" names the mode it comes to from the user's and managed settings, usually Auto (DESIGN.md
    /// §7, "Starting mode"). A project's settings can still change it for its tabs.
    /// </summary>
    public IReadOnlyList<Choice> ModeChoices => field ??=
    [
        new(null, $"Claude Code's default ({PermissionModeInfo.Label(Services.ReadStartingPermissionMode(null).Expected)})"),
        .. PermissionModeInfo.Choices.Select(m => new Choice(m.Value, m.Label)),
    ];

    public Choice DefaultModel
    {
        get => ModelChoices.FirstOrDefault(c => c.Value == Settings.NewTabs.DefaultModel) ?? ModelChoices[0];
        set => Set(value?.Value, v => Settings.NewTabs.DefaultModel = v);
    }

    public Choice DefaultEffort
    {
        get => EffortChoices.FirstOrDefault(c => c.Value == Settings.NewTabs.DefaultEffort) ?? EffortChoices[0];
        set => Set(value?.Value, v => Settings.NewTabs.DefaultEffort = v);
    }

    public Choice DefaultMode
    {
        get => ModeChoices.FirstOrDefault(c => c.Value == Settings.NewTabs.DefaultPermissionMode) ?? ModeChoices[0];
        set => Set(value?.Value, v => Settings.NewTabs.DefaultPermissionMode = v);
    }

    /// <summary>What Claude Code offered in its last <c>initialize</c> reply, plus the current default if it's not among them.</summary>
    private IReadOnlyList<Choice> BuildModelChoices()
    {
        var known = Services.State.KnownModels;
        IReadOnlyList<Choice> models = known.Count > 0 ? [.. known.Select(m => new Choice(m.Value, m.DisplayName))] : FallbackModels;
        var choices = new List<Choice> { new(null, "Claude Code's default") };
        choices.AddRange(models);
        if (Settings.NewTabs.DefaultModel is { } current && choices.All(c => c.Value != current))
        {
            choices.Add(new Choice(current, current));
        }
        return choices;
    }

    /// <summary>Every effort level a model offers, in Claude Code's order.</summary>
    private IReadOnlyList<Choice> BuildEffortChoices()
    {
        var levels = Services.State.KnownModels.SelectMany(m => m.SupportedEffortLevels).Distinct(StringComparer.Ordinal).ToList();
        if (levels.Count == 0)
        {
            levels = [.. FallbackEffortLevels];
        }
        var choices = new List<Choice> { new(null, "The model's default") };
        choices.AddRange(levels.Select(l => new Choice(l, l)));
        if (Settings.NewTabs.DefaultEffort is { } current && choices.All(c => c.Value != current))
        {
            choices.Add(new Choice(current, current));
        }
        return choices;
    }

    // ---- Recent folders --------------------------------------------------------------------------------------------

    public decimal? RecentFolderLimit
    {
        get => Settings.NewTabs.RecentFolderLimit;
        set => Set(value, v => Settings.NewTabs.RecentFolderLimit = Math.Clamp((int)(v ?? 20), 1, 100));
    }

    [RelayCommand]
    private void ClearRecentFolders()
    {
        Services.State.RecentFolders.RemoveAll(r => !FolderHistory.IsFavorite(Services.State, r.Path));
        Services.SaveState();
    }

    // ---- Favorite folders (DESIGN.md §4, "Opening a tab") -----------------------------------------------------------

    public ObservableCollection<FavoriteFolderRow> Favorites { get; } = [];

    public bool HasFavorites => Favorites.Count > 0;

    private void LoadFavorites()
    {
        Favorites.Clear();
        foreach (var path in Services.State.FavoriteFolders)
        {
            Favorites.Add(new FavoriteFolderRow(path, Formats.FolderName(path)));
        }
        OnPropertyChanged(nameof(HasFavorites));
    }

    [RelayCommand]
    private async Task AddFavoriteAsync()
    {
        if (await Services.Platform.PickFolderAsync("Add a favorite folder") is { } folder)
        {
            FolderHistory.SetFavorite(Services.State, folder, true);
            Services.SaveState();
            LoadFavorites();
        }
    }

    [RelayCommand]
    private void RemoveFavorite(FavoriteFolderRow? row)
    {
        if (row is not null)
        {
            FolderHistory.SetFavorite(Services.State, row.Path, false);
            Services.SaveState();
            LoadFavorites();
        }
    }

    [RelayCommand]
    private void MoveFavoriteUp(FavoriteFolderRow? row) => MoveFavorite(row, -1);

    [RelayCommand]
    private void MoveFavoriteDown(FavoriteFolderRow? row) => MoveFavorite(row, 1);

    private void MoveFavorite(FavoriteFolderRow? row, int by)
    {
        var favorites = Services.State.FavoriteFolders;
        var index = row is null ? -1 : favorites.IndexOf(row.Path);
        var target = index + by;
        if (index < 0 || target < 0 || target >= favorites.Count)
        {
            return;
        }
        (favorites[index], favorites[target]) = (favorites[target], favorites[index]);
        Services.SaveState();
        LoadFavorites();
    }

    /// <summary>Favorites and recent folders are this machine's data, not settings, so they stay.</summary>
    protected override void ResetSettings()
    {
        Settings.NewTabs = new NewTabSettings();
        Save();
    }
}
