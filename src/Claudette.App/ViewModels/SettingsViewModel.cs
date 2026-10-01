using System.Collections.ObjectModel;
using Claudette.App.Services;
using Claudette.App.ViewModels.Settings;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Claudette.App.ViewModels;

/// <summary>A setting found by the Settings search box (DESIGN.md §14).</summary>
public sealed record SettingsSearchResult(string Category, string Label)
{
    /// <summary>The sidebar group the category is in, for the tab's project pages: "NightOwl".</summary>
    public string? Group { get; init; }

    /// <summary>Where the setting is, as the results list says: "Appearance", or "NightOwl → Links".</summary>
    public string Where => Group is null ? Category : $"{Group} → {Category}";

    /// <summary>
    /// The page's own words for the setting, or for the part of the page it's in, when the label puts it differently:
    /// "Save password" for "Save a Perforce password". Not searched; SettingsWindowTests checks that the page shows
    /// them, or the label when this is null, so the index can't point at something the page no longer has.
    /// </summary>
    public string? PageText { get; init; }
}

/// <summary>A choice in one of Settings' dropdowns, with its label.</summary>
public sealed record SettingChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Settings window (DESIGN.md §14): the categories, the search box and the page being shown. Each category is a
/// page of its own (<see cref="SettingsPage"/>), with its settings, its search entries and <b>Reset to defaults</b>;
/// below the categories, the selected tab's project has its pages too (<see cref="Project"/>). Changes apply
/// immediately; there's no Save button. Dispose it when the window closes.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase, IDisposable
{
    public static readonly IReadOnlyList<string> AllCategories =
    [
        SettingsCategory.General, SettingsCategory.Sessions, SettingsCategory.Processes, SettingsCategory.ClaudeCode, SettingsCategory.NewTabs,
        SettingsCategory.Appearance, SettingsCategory.Usage, SettingsCategory.QuickSuffixes, SettingsCategory.CheckIns, SettingsCategory.DiffTool,
        SettingsCategory.ProjectTools, SettingsCategory.Notifications, SettingsCategory.Keyboard, SettingsCategory.Perforce, SettingsCategory.Advanced,
    ];

    private readonly AppServices _services;
    private readonly SettingsContext _context;

    /// <param name="opening">
    /// Where the window opens, and the project pages of the tab that was selected (DESIGN.md §14). Without it, it opens
    /// at General, with no project pages.
    /// </param>
    public SettingsViewModel(AppServices services, string? accountText, ClaudeUpdateViewModel? updates = null, SettingsOpening? opening = null)
    {
        _services = services;
        _context = new SettingsContext(services);
        Project = opening?.Project;
        General = new GeneralPage(_context);
        Sessions = new SessionsPage(_context);
        Processes = new ProcessesPage(_context);
        NewTabs = new NewTabsPage(_context);
        ClaudeCode = new ClaudeCodePage(_context, accountText, updates, NewTabs);
        Appearance = new AppearancePage(_context);
        Usage = new UsagePage(_context);
        QuickSuffixes = new QuickSuffixesPage(_context);
        CheckIns = new CheckInsPage(_context);
        DiffTool = new DiffToolPage(_context);
        ProjectTools = new ProjectToolsPage(_context);
        Notifications = new NotificationsPage(_context);
        Keyboard = new KeyboardPage(_context);
        Perforce = new PerforcePage(_context);
        Advanced = new AdvancedPage(_context, ClaudeCode);
        Pages =
        [
            General, Sessions, Processes, ClaudeCode, NewTabs, Appearance, Usage, QuickSuffixes, CheckIns, DiffTool, ProjectTools, Notifications, Keyboard, Perforce, Advanced,
            .. Project is { } project ? [new ProjectLinksPage(_context, project), new ProjectActionsPage(_context, project), new ProjectToolChoicesPage(_context, project)] : Array.Empty<SettingsPage>(),
        ];
        SelectedCategory = AllCategories[0];
        if (opening?.Category is { } category && (AllCategories.Contains(category) || Project is not null && ProjectPages.Contains(category)))
        {
            SelectedCategory = category;
        }
        if (opening?.StartNew == true)
        {
            if (CurrentPage is ProjectLinksPage)
            {
                Project?.StartNewLink();
            }
            else if (CurrentPage is ProjectActionsPage)
            {
                Project?.StartNewAction();
            }
        }
    }

    public void Dispose()
    {
        ClaudeCode.Dispose();
        Project?.Dispose();
    }

    public IReadOnlyList<string> Categories => AllCategories;

    // ---- The pages ----------------------------------------------------------------------------------------------------

    public GeneralPage General { get; }

    public SessionsPage Sessions { get; }

    public ProcessesPage Processes { get; }

    public ClaudeCodePage ClaudeCode { get; }

    public NewTabsPage NewTabs { get; }

    public AppearancePage Appearance { get; }

    public UsagePage Usage { get; }

    public QuickSuffixesPage QuickSuffixes { get; }

    public CheckInsPage CheckIns { get; }

    public DiffToolPage DiffTool { get; }

    public ProjectToolsPage ProjectTools { get; }

    public NotificationsPage Notifications { get; }

    public KeyboardPage Keyboard { get; }

    public PerforcePage Perforce { get; }

    public AdvancedPage Advanced { get; }

    /// <summary>
    /// Every page, in the sidebar's order: the categories' (<see cref="AllCategories"/>), then the selected tab's project
    /// pages (<see cref="ProjectPages"/>), which there are none of with no tab open.
    /// </summary>
    public IReadOnlyList<SettingsPage> Pages { get; }

    /// <summary>The account in Claude Code, with <b>Sign in</b> and <b>Sign out</b> wired to the header's (DESIGN.md §11). Null in some tests.</summary>
    public AccountViewModel? Account
    {
        get => ClaudeCode.Account;
        init => ClaudeCode.Account = value;
    }

    /// <summary>Claudette's version, update status and actions in General, the same as the sidebar's badge. Null in some tests.</summary>
    public AppUpdateViewModel? AppUpdates
    {
        get => General.AppUpdates;
        init => General.AppUpdates = value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(SelectedMainCategory), nameof(SelectedProjectPage))]
    public partial string SelectedCategory { get; set; }

    /// <summary>The page the window shows: the selected category's, or one of the project's. Null for a page there isn't.</summary>
    public SettingsPage? CurrentPage => Pages.FirstOrDefault(p => p.Title == SelectedCategory);

    // ---- Search (DESIGN.md §14: "A search box filters settings by name") -------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    public partial string SearchText { get; set; } = "";

    public bool IsSearching => SearchText.Trim().Length > 0;

    public ObservableCollection<SettingsSearchResult> SearchResults { get; } = [];

    /// <summary>
    /// Each page's entries, in the sidebar's order, then the project pages', named with the project's group ("NightOwl →
    /// Links"). The Tools page's depend on the project's kind, so they're worked out as the search runs.
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        SearchResults.Clear();
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return;
        }
        foreach (var result in Pages.SelectMany(p => p.SearchEntries).Where(r => words.All(w =>
            r.Label.Contains(w, StringComparison.OrdinalIgnoreCase) || r.Category.Contains(w, StringComparison.OrdinalIgnoreCase)
            || r.Group?.Contains(w, StringComparison.OrdinalIgnoreCase) == true)))
        {
            SearchResults.Add(result);
        }
        if (SearchResults.Count > 0 && SearchResults.All(r => r.Category != SelectedCategory))
        {
            SelectedCategory = SearchResults[0].Category;
        }
    }

    /// <summary>Picking a result shows its category.</summary>
    [ObservableProperty]
    public partial SettingsSearchResult? SelectedSearchResult { get; set; }

    partial void OnSelectedSearchResultChanged(SettingsSearchResult? value)
    {
        if (value is not null)
        {
            SelectedCategory = value.Category;
        }
    }

    // ---- Recording a shortcut (Keyboard, Quick suffixes) ----------------------------------------------------------------

    /// <summary>A shortcut is being recorded: the window passes the next key press to <see cref="RecordShortcut"/>.</summary>
    public bool IsRecordingShortcut => _context.Shortcuts.IsRecording;

    public void CancelRecording() => _context.Shortcuts.Cancel();

    /// <inheritdoc cref="ShortcutRecorder.Record"/>
    public void RecordShortcut(KeyChord chord) => _context.Shortcuts.Record(chord);
}
