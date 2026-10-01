using System.Collections.ObjectModel;
using Claudette.App.Services;
using Claudette.Core.Diffs;
using Claudette.Core.Installation;
using Claudette.Core.Library;
using Claudette.Core.LoginItems;
using Claudette.Core.Settings;
using Claudette.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A diff tool choice in Settings: built-in, an installed preset, or a custom command.</summary>
public sealed record DiffToolOption(string Kind, string? PresetId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A setting found by the Settings search box (DESIGN.md §14).</summary>
public sealed record SettingsSearchResult(string Category, string Label)
{
    /// <summary>The sidebar group the category is in, for the tab's project pages: "NightOwl".</summary>
    public string? Group { get; init; }

    /// <summary>Where the setting is, as the results list says: "Appearance", or "NightOwl → Links".</summary>
    public string Where => Group is null ? Category : $"{Group} → {Category}";
}

/// <summary>A Style choice in Settings → Appearance (DESIGN.md §3, "Visual style").</summary>
public sealed record StyleOption(AppStyle Style, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A retention option in a dropdown.</summary>
public sealed record RetentionChoice(RetentionPeriod Period)
{
    public override string ToString() => Period.Label();
}

/// <summary>Something whose keyboard shortcut is being set in Settings: a command, or a quick suffix.</summary>
public abstract partial class ShortcutEditor : ObservableObject
{
    /// <summary>Waiting for the user to press the new shortcut.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortcutText))]
    public partial bool IsRecording { get; set; }

    /// <summary>Why the last keys pressed can't be used.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    public abstract KeyChord? Shortcut { get; }

    public string ShortcutText => IsRecording ? "Press keys…" : Shortcut?.Display(Shortcuts.IsMac) ?? "None";

    public void Refresh()
    {
        OnPropertyChanged(nameof(ShortcutText));
        OnPropertyChanged(nameof(Shortcut));
        OnRefresh();
    }

    protected virtual void OnRefresh()
    {
    }
}

/// <summary>One command in Settings → Keyboard (DESIGN.md §14).</summary>
public sealed class ShortcutRow(ShortcutCommand command, KeyboardSettings settings) : ShortcutEditor
{
    public ShortcutCommand Command { get; } = command;

    public string Label => Command.Label;

    public override KeyChord? Shortcut => KeyboardShortcuts.Resolve(settings, Command.Id);

    /// <summary>Changed from the default, so <b>Reset</b> applies.</summary>
    public bool IsCustomized => settings.Bindings.ContainsKey(Command.Id);

    protected override void OnRefresh() => OnPropertyChanged(nameof(IsCustomized));
}

/// <summary>One quick suffix being edited in Settings.</summary>
public sealed partial class QuickSuffixEditor(QuickSuffix suffix, Action changed) : ShortcutEditor
{
    public QuickSuffix Suffix { get; } = suffix;

    /// <summary>Adds the suffix straight from the keyboard (DESIGN.md §5, "Own shortcut").</summary>
    public override KeyChord? Shortcut => KeyChord.TryParse(Suffix.Shortcut, out var chord) ? chord : null;

    public bool HasShortcut => Shortcut is not null;

    protected override void OnRefresh() => OnPropertyChanged(nameof(HasShortcut));

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
/// The Settings window (DESIGN.md §14). Changes apply immediately; there's no Save button. Below the categories, the
/// selected tab's project has its own pages (<see cref="Project"/>). Dispose it when the window closes.
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
    private readonly AppSettings _settings;

    /// <param name="opening">
    /// Where the window opens, and the project pages of the tab that was selected (DESIGN.md §14). Without it, it opens
    /// at General, with no project pages.
    /// </param>
    public SettingsViewModel(AppServices services, string? accountText, ClaudeUpdateViewModel? updates = null, SettingsOpening? opening = null)
    {
        _services = services;
        Project = opening?.Project;
        _settings = services.Settings;
        AccountText = accountText ?? "Not signed in";
        Updates = updates;
        foreach (var suffix in _settings.QuickSuffixes)
        {
            Suffixes.Add(new QuickSuffixEditor(suffix, Save));
        }
        foreach (var command in KeyboardShortcuts.All)
        {
            ShortcutRows.Add(new ShortcutRow(command, _settings.Keyboard));
        }
        LoadFavorites();
        SelectedCategory = AllCategories[0];
        if (opening?.Category is { } category && (AllCategories.Contains(category) || Project is not null && ProjectPages.Contains(category)))
        {
            SelectedCategory = category;
        }
        if (opening?.StartNew == true)
        {
            if (IsLinksPage)
            {
                Project?.StartNewLink();
            }
            else if (IsActionsPage)
            {
                Project?.StartNewAction();
            }
        }
        FillPerforceLogin();
        // Signing in from this window can make the Claude app available, or not (DESIGN.md §18).
        _services.RemoteControl.AvailabilityChanged += OnRemoteControlAvailabilityChanged;
        _ = RefreshStartAtLoginAsync();
    }

    public void Dispose()
    {
        _services.RemoteControl.AvailabilityChanged -= OnRemoteControlAvailabilityChanged;
        Project?.Dispose();
    }

    public IReadOnlyList<string> Categories => AllCategories;

    // ---- Search (DESIGN.md §14: "A search box filters settings by name") -------------------------------------------

    /// <summary>
    /// The settings by category, as their labels read in the window. Keep in step with SettingsWindow.axaml. The tab's
    /// project pages add their own (<see cref="ProjectSearchEntries"/>).
    /// </summary>
    private static readonly IReadOnlyList<SettingsSearchResult> SearchIndex =
    [
        new(SettingsCategory.General, "Start Claudette when I log in"),
        new(SettingsCategory.General, "Confirm before closing a tab where Claude is working"),
        new(SettingsCategory.General, "Also rename the session in Claude Code when I rename a tab"),
        new(SettingsCategory.General, "Show Claude's service status"),
        new(SettingsCategory.General, "Claudette version"),
        new(SettingsCategory.General, "Check for Claudette updates automatically"),
        new(SettingsCategory.General, "Include pre-releases"),
        new(SettingsCategory.General, "Check for Claudette updates now"),
        new(SettingsCategory.Sessions, "Also restore unpinned tabs when Claudette starts"),
        new(SettingsCategory.Sessions, "Name for this machine"),
        new(SettingsCategory.Sessions, "Keep library sessions for"),
        new(SettingsCategory.Sessions, "Session library folder"),
        new(SettingsCategory.Sessions, "Move library"),
        new(SettingsCategory.Sessions, "Sync new tabs to the session library"),
        new(SettingsCategory.Sessions, "Sync Claudette's settings through the library"),
        new(SettingsCategory.Processes, "Show the process monitor"),
        new(SettingsCategory.Processes, "Refresh the panel every (seconds)"),
        new(SettingsCategory.Processes, "Show command lines"),
        new(SettingsCategory.ClaudeCode, "Installed version and install method"),
        new(SettingsCategory.ClaudeCode, "Check for Claude Code updates automatically"),
        new(SettingsCategory.ClaudeCode, "Update Claude Code"),
        new(SettingsCategory.ClaudeCode, "Signed-in account"),
        new(SettingsCategory.ClaudeCode, "Sign in"),
        new(SettingsCategory.ClaudeCode, "Sign out"),
        new(SettingsCategory.ClaudeCode, "Path to claude"),
        .. LoginShellSearchEntries(),
        new(SettingsCategory.ClaudeCode, "Connect new tabs to the Claude app (Remote Control)"),
        new(SettingsCategory.ClaudeCode, "Push notifications on your phone"),
        new(SettingsCategory.ClaudeCode, "Keep this computer awake while tabs are connected"),
        new(SettingsCategory.NewTabs, "Default model"),
        new(SettingsCategory.NewTabs, "Default effort"),
        new(SettingsCategory.NewTabs, "Default permission mode"),
        new(SettingsCategory.NewTabs, "Recent folders to keep"),
        new(SettingsCategory.NewTabs, "Clear recent folders"),
        new(SettingsCategory.NewTabs, "Favorite folders"),
        new(SettingsCategory.Appearance, "Theme"),
        new(SettingsCategory.Appearance, "Style"),
        new(SettingsCategory.Appearance, "Conversation font"),
        new(SettingsCategory.Appearance, "Conversation font size"),
        new(SettingsCategory.Appearance, "Code font"),
        new(SettingsCategory.Appearance, "Code font size"),
        new(SettingsCategory.Appearance, "Show thinking expanded"),
        new(SettingsCategory.Appearance, "Show fun words while Claude works"),
        new(SettingsCategory.Appearance, "Show what Claude is doing while it works"),
        new(SettingsCategory.Appearance, "Detailed usage header"),
        new(SettingsCategory.Appearance, "Show context on tab rows"),
        new(SettingsCategory.Appearance, "Density"),
        new(SettingsCategory.Usage, "Warn at (% of session used)"),
        new(SettingsCategory.Usage, "Alert at (% of session used)"),
        new(SettingsCategory.Usage, "Burn rate window (minutes)"),
        new(SettingsCategory.Usage, "Show model-specific weekly limits"),
        new(SettingsCategory.Usage, "Read model limits from /usage"),
        new(SettingsCategory.Usage, "Continue tasks when a usage limit resets"),
        new(SettingsCategory.Usage, "Keep usage history"),
        new(SettingsCategory.Usage, "Clear usage history"),
        new(SettingsCategory.Usage, "Share usage with my other machines"),
        new(SettingsCategory.QuickSuffixes, "Add suffix"),
        new(SettingsCategory.QuickSuffixes, "Suffix shortcuts"),
        new(SettingsCategory.CheckIns, "Check in on long turns"),
        new(SettingsCategory.CheckIns, "After the turn has run (minutes)"),
        new(SettingsCategory.CheckIns, "After no output for (minutes)"),
        new(SettingsCategory.CheckIns, "Check-in message"),
        new(SettingsCategory.CheckIns, "Notify me when a check-in is sent"),
        new(SettingsCategory.DiffTool, "Diff tool"),
        new(SettingsCategory.DiffTool, "Custom diff command"),
        new(SettingsCategory.DiffTool, "Test the diff tool"),
        .. ProjectToolsSearchEntries(),
        new(SettingsCategory.Notifications, "A tab finishes its turn"),
        new(SettingsCategory.Notifications, "A tab needs permission or an answer"),
        new(SettingsCategory.Notifications, "A tab's Claude Code stops with an error"),
        new(SettingsCategory.Notifications, "Usage alerts"),
        new(SettingsCategory.Notifications, "Claude Code needs me to sign in"),
        new(SettingsCategory.Notifications, "A Claude Code update is ready"),
        new(SettingsCategory.Notifications, "A project action finishes"),
        new(SettingsCategory.Notifications, "Dock or taskbar badge"),
        new(SettingsCategory.Notifications, "Animate the Dock or taskbar icon"),
        .. KeyboardShortcuts.All.Select(c => new SettingsSearchResult("Keyboard", $"{c.Label} shortcut")),
        .. PerforceSearchEntries(),
        new(SettingsCategory.Advanced, "Extra arguments for every claude process"),
        new(SettingsCategory.Advanced, "Log protocol traffic"),
        new(SettingsCategory.Advanced, "Open log folder"),
        new(SettingsCategory.Advanced, "Diagnostics"),
        new(SettingsCategory.Advanced, "Copy diagnostics"),
        new(SettingsCategory.Advanced, "Minimum supported Claude Code version"),
        new(SettingsCategory.Advanced, "Open data folder"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    public partial string SearchText { get; set; } = "";

    public bool IsSearching => SearchText.Trim().Length > 0;

    public ObservableCollection<SettingsSearchResult> SearchResults { get; } = [];

    partial void OnSearchTextChanged(string value)
    {
        SearchResults.Clear();
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return;
        }
        foreach (var result in SearchIndex.Concat(ProjectSearchEntries()).Where(r => words.All(w =>
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneral), nameof(IsClaudeCode), nameof(IsNewTabs), nameof(IsAppearance), nameof(IsSessions), nameof(IsCheckIns), nameof(IsQuickSuffixes), nameof(IsAdvanced))]
    [NotifyPropertyChangedFor(nameof(IsUsage), nameof(IsProcesses), nameof(IsDiffTool), nameof(IsNotifications), nameof(IsKeyboard), nameof(IsProjectTools))]
    public partial string SelectedCategory { get; set; }

    public bool IsKeyboard => SelectedCategory == SettingsCategory.Keyboard;

    public bool IsNotifications => SelectedCategory == SettingsCategory.Notifications;

    public bool IsUsage => SelectedCategory == SettingsCategory.Usage;

    public bool IsProcesses => SelectedCategory == SettingsCategory.Processes;

    public bool IsDiffTool => SelectedCategory == SettingsCategory.DiffTool;

    public bool IsGeneral => SelectedCategory == SettingsCategory.General;

    public bool IsClaudeCode => SelectedCategory == SettingsCategory.ClaudeCode;

    public bool IsNewTabs => SelectedCategory == SettingsCategory.NewTabs;

    public bool IsAppearance => SelectedCategory == SettingsCategory.Appearance;

    public bool IsSessions => SelectedCategory == SettingsCategory.Sessions;

    public bool IsCheckIns => SelectedCategory == SettingsCategory.CheckIns;

    public bool IsQuickSuffixes => SelectedCategory == SettingsCategory.QuickSuffixes;

    public bool IsAdvanced => SelectedCategory == SettingsCategory.Advanced;

    // ---- General ---------------------------------------------------------------------------------------------

    private LoginItemStatus? _loginItem;
    private bool _changingLoginItem;

    /// <summary>
    /// Reads whether Claudette starts at login, as the window opens: Task Manager, System Settings or another copy of
    /// Claudette may have changed it since (DESIGN.md §9, "Starting at login").
    /// </summary>
    public async Task RefreshStartAtLoginAsync() => UseLoginItem(await _services.StartAtLogin.ReadAsync());

    /// <summary>
    /// <b>Start Claudette when I log in</b>. It's this machine's, and lives in the OS rather than in settings.json, so
    /// Reset to defaults leaves it.
    /// </summary>
    public bool StartsAtLogin
    {
        get => _loginItem?.IsOn == true;
        set
        {
            if (value != StartsAtLogin && CanChangeStartAtLogin)
            {
                _ = SetStartAtLoginAsync(value);
            }
        }
    }

    public bool CanChangeStartAtLogin => _loginItem?.CanChange == true && !_changingLoginItem;

    /// <summary>What happens at login, and which copy of Claudette starts when it isn't simply this one.</summary>
    public string StartAtLoginText
    {
        get
        {
            const string opens = "Claudette opens minimized when you log in to this computer.";
            var current = _services.ThisCopy;
            return _loginItem?.Starts switch
            {
                { } starts when !starts.IsSameCopy(current) => $"{opens} It starts {starts.Describe()} rather than this {current.KindName}.",
                { Kind: AppInstallKind.SourceBuild } => $"{opens} It starts this source build's newest build. Once Claudette is installed, the installed one starts instead.",
                _ => opens,
            };
        }
    }

    /// <summary>Why the switch can't be changed here, such as being turned off in Task Manager.</summary>
    public string? StartAtLoginNote => _loginItem?.Note;

    public bool HasStartAtLoginNote => StartAtLoginNote is not null;

    public string? StartAtLoginError => _loginItem?.Error;

    public bool HasStartAtLoginError => StartAtLoginError is not null;

    private async Task SetStartAtLoginAsync(bool on)
    {
        _changingLoginItem = true;
        OnPropertyChanged(nameof(CanChangeStartAtLogin));
        try
        {
            UseLoginItem(await _services.StartAtLogin.SetAsync(on));
        }
        finally
        {
            _changingLoginItem = false;
            OnPropertyChanged(nameof(CanChangeStartAtLogin));
        }
    }

    private void UseLoginItem(LoginItemStatus status)
    {
        _loginItem = status;
        OnPropertyChanged(nameof(StartsAtLogin));
        OnPropertyChanged(nameof(CanChangeStartAtLogin));
        OnPropertyChanged(nameof(StartAtLoginText));
        OnPropertyChanged(nameof(StartAtLoginNote));
        OnPropertyChanged(nameof(HasStartAtLoginNote));
        OnPropertyChanged(nameof(StartAtLoginError));
        OnPropertyChanged(nameof(HasStartAtLoginError));
    }

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

    /// <summary>
    /// The header's dot and the banner, from status.claude.com (DESIGN.md §18, "Service status"). Off stops the checks.
    /// </summary>
    public bool ShowServiceStatus
    {
        get => _settings.General.ShowServiceStatus;
        set => Set(value, v => _settings.General.ShowServiceStatus = v);
    }

    /// <summary>Check GitHub for new Claudette releases (DESIGN.md §2, "Updating Claudette").</summary>
    public bool CheckForAppUpdates
    {
        get => _settings.General.CheckForAppUpdates;
        set => Set(value, v => _settings.General.CheckForAppUpdates = v);
    }

    public bool IncludePrereleases
    {
        get => _settings.General.IncludePrereleases;
        set
        {
            Set(value, v => _settings.General.IncludePrereleases = v);
            // Takes effect with the next check: do that now.
            AppUpdates?.CheckNowCommand.Execute(null);
        }
    }

    /// <summary>Claudette's version, update status and actions, the same as the sidebar's badge. Null in some tests.</summary>
    public AppUpdateViewModel? AppUpdates { get; init; }

    public bool HasAppUpdates => AppUpdates is not null;

    [RelayCommand]
    private void ResetGeneral()
    {
        _settings.General = new GeneralSettings();
        Save();
        OnPropertyChanged(nameof(ConfirmCloseWorkingTab));
        OnPropertyChanged(nameof(RenameInClaudeCode));
        OnPropertyChanged(nameof(ShowServiceStatus));
        OnPropertyChanged(nameof(CheckForAppUpdates));
        OnPropertyChanged(nameof(IncludePrereleases));
    }

    // ---- Claude Code -----------------------------------------------------------------------------------------

    public string AccountText { get; }

    /// <summary>The account, with <b>Sign in</b> and <b>Sign out</b> wired to the header's (DESIGN.md §11). Null in some tests.</summary>
    public AccountViewModel? Account { get; init; }

    public bool HasAccount => Account is not null;

    /// <summary>Version, install method, update checks and <b>Update now</b> (DESIGN.md §12, §14). Null before Claude Code is found.</summary>
    public ClaudeUpdateViewModel? Updates { get; }

    public bool HasUpdates => Updates is not null;

    public bool CheckForUpdates
    {
        get => _settings.ClaudeCode.CheckForUpdates;
        set => Set(value, v => _settings.ClaudeCode.CheckForUpdates = v);
    }

    public string InstalledText => _services.Install is { } install
        ? $"Claude Code {install.Version} at {install.Path}. Last tested with {ClaudeLocator.LastTestedVersion}"
            + (install.Version > ClaudeLocator.LastTestedVersion ? " (this version is newer)."
                : install.Version < ClaudeLocator.LastTestedVersion ? " (this version is older; updating is recommended)."
                : ".")
        : "Claude Code wasn't found.";

    /// <summary>
    /// Settings → Claude Code → <b>Use my login shell's environment</b> (DESIGN.md §13, "Login shell environment"). Turning
    /// it on reads the login shell then, if this run hasn't yet; either way it applies to processes started after.
    /// </summary>
    public bool UseLoginShellEnvironment
    {
        get => _settings.ClaudeCode.UseLoginShellEnvironment;
        set => Set(value, v => _settings.ClaudeCode.UseLoginShellEnvironment = v);
    }

    /// <summary>Only macOS and Linux have the setting: on Windows, apps get the user's full environment.</summary>
    public static bool ShowLoginShellSetting => !OperatingSystem.IsWindows();

    private static IEnumerable<SettingsSearchResult> LoginShellSearchEntries() =>
        ShowLoginShellSetting ? [new(SettingsCategory.ClaudeCode, "Use my login shell's environment")] : [];

    // ---- The Claude app (DESIGN.md §18, "Remote Control") --------------------------------------------------------

    /// <summary>The docs' steps for pushes to the phone. Claudette doesn't change Claude Code's settings for them.</summary>
    public const string PushNotificationsDocs = "https://code.claude.com/docs/en/remote-control#mobile-push-notifications";

    /// <summary>
    /// <b>Connect new tabs to the Claude app</b>: the switch new tabs start with, like <b>Sync new tabs</b>. Open tabs
    /// keep theirs; each has <b>Connect to the Claude app</b> in its menu.
    /// </summary>
    public bool ConnectNewTabsToClaudeApp
    {
        get => _settings.ClaudeCode.ConnectNewTabsToClaudeApp;
        set => Set(value, v => _settings.ClaudeCode.ConnectNewTabsToClaudeApp = v);
    }

    /// <summary><b>Keep this computer awake while tabs are connected</b>: system sleep only; the display can still sleep.</summary>
    public bool KeepAwakeWhileConnected
    {
        get => _settings.ClaudeCode.KeepAwakeWhileConnected;
        set => Set(value, v => _settings.ClaudeCode.KeepAwakeWhileConnected = v);
    }

    /// <summary>The account can use Remote Control. When it can't, the setting and the tabs' switches are disabled.</summary>
    public bool CanUseRemoteControl => _services.RemoteControl.IsAvailable;

    /// <summary>Why the account can't use Remote Control, from <c>claude auth status</c> and the environment.</summary>
    public string? RemoteControlUnavailableText => _services.RemoteControl.UnavailableReason is { } reason ? $"Not available: {reason}" : null;

    public bool HasRemoteControlUnavailableText => RemoteControlUnavailableText is not null;

    [RelayCommand]
    private Task OpenPushNotificationsDocsAsync() => _services.Platform.OpenUrlAsync(PushNotificationsDocs);

    private void OnRemoteControlAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanUseRemoteControl));
        OnPropertyChanged(nameof(RemoteControlUnavailableText));
        OnPropertyChanged(nameof(HasRemoteControlUnavailableText));
    }

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

    public IReadOnlyList<Choice> ModelChoices => field ??= BuildModelChoices();

    public IReadOnlyList<Choice> EffortChoices => field ??= BuildEffortChoices();

    /// <summary>
    /// "Claude Code's default" names the mode it comes to from the user's and managed settings, usually Auto (DESIGN.md
    /// §7, "Starting mode"). A project's settings can still change it for its tabs.
    /// </summary>
    public IReadOnlyList<Choice> ModeChoices => field ??=
    [
        new(null, $"Claude Code's default ({PermissionModeInfo.Label(_services.ReadStartingPermissionMode(null).Expected)})"),
        .. PermissionModeInfo.Choices.Select(m => new Choice(m.Value, m.Label)),
    ];

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

    /// <summary>Settings → Appearance → Style (DESIGN.md §3, "Visual style"), named as the list shows them.</summary>
    public IReadOnlyList<StyleOption> StyleOptions { get; } =
        [new(AppStyle.Standard, "Standard"), new(AppStyle.Claude, "Claude")];

    public StyleOption Style
    {
        get => StyleOptions.FirstOrDefault(o => o.Style == _settings.Appearance.Style) ?? StyleOptions[0];
        set => Set(value, v => _settings.Appearance.Style = v?.Style ?? AppStyle.Standard);
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

    /// <summary>The working line's twinkling glyph and fun verbs (DESIGN.md §5, "Working line").</summary>
    public bool FunWorkingWords
    {
        get => _settings.Appearance.FunWorkingWords;
        set => Set(value, v => _settings.Appearance.FunWorkingWords = v);
    }

    /// <summary>The working line says what the running tool is doing (DESIGN.md §5, "Working line").</summary>
    public bool ShowToolInWorkingLine
    {
        get => _settings.Appearance.ShowToolInWorkingLine;
        set => Set(value, v => _settings.Appearance.ShowToolInWorkingLine = v);
    }

    /// <summary>
    /// The usage header drawn taller with charts (DESIGN.md §6, "Detailed header"), the same switch as its chevron. It's
    /// this machine's state rather than a setting, like the sidebar's collapsed state, so it doesn't sync.
    /// </summary>
    public bool DetailedUsageHeader
    {
        get => _services.State.DetailedUsageHeader;
        set
        {
            if (_services.State.DetailedUsageHeader != value)
            {
                _services.State.DetailedUsageHeader = value;
                _services.SaveState();
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The context ring on each tab's row (DESIGN.md §4, "Sidebar").</summary>
    public bool ShowContextOnTabs
    {
        get => _settings.Appearance.ShowContextOnTabs;
        set => Set(value, v => _settings.Appearance.ShowContextOnTabs = v);
    }

    public IReadOnlyList<Density> Densities { get; } = [Density.Comfortable, Density.Compact];

    /// <summary>Compact tightens the conversation, the sidebar's rows and the composer (DESIGN.md §14).</summary>
    public Density Density
    {
        get => _settings.Appearance.Density;
        set => Set(value, v => _settings.Appearance.Density = v);
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

    /// <summary>New tabs start syncing to the session library (DESIGN.md §9). Each tab can change it from its menu.</summary>
    public bool SyncNewTabs
    {
        get => _settings.Sessions.SyncNewTabs;
        set => Set(value, v => _settings.Sessions.SyncNewTabs = v);
    }

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
        .. DiffToolDetector.Detect(_services.UserEnvironment.Probe).Select(d => new DiffToolOption("preset", d.Preset.Id, $"{d.Preset.Name}  ({d.ExecutablePath})")),
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
            await new DiffToolLauncher(_services.Launcher, _services.Time, environment: _services.UserEnvironment).TestAsync(choice, Path.Combine(_services.Paths.DiffTempDirectory, "test"));
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

    /// <summary>DESIGN.md §6, "Continuing after a limit resets". Each tab can override it in Tab settings.</summary>
    public bool ContinueAfterLimitReset
    {
        get => _settings.Usage.ContinueAfterLimitReset;
        set => Set(value, v => _settings.Usage.ContinueAfterLimitReset = v);
    }

    /// <summary>DESIGN.md §6, "Sharing across machines".</summary>
    public bool ShareUsageThroughLibrary
    {
        get => _settings.Usage.ShareThroughLibrary;
        set => Set(value, v => _settings.Usage.ShareThroughLibrary = v);
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
        OnPropertyChanged(nameof(ContinueAfterLimitReset));
        OnPropertyChanged(nameof(ShareUsageThroughLibrary));
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

    public bool NotifyOnCheckIn
    {
        get => _settings.CheckIns.Notify;
        set => Set(value, v => _settings.CheckIns.Notify = v);
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
        OnPropertyChanged(nameof(NotifyOnCheckIn));
    }

    // ---- Notifications (DESIGN.md §10) ------------------------------------------------------------------------------

    /// <summary>False when this machine can't show OS notifications, for example a macOS build run outside its app bundle.</summary>
    public bool NotificationsAvailable => _services.Notifications.IsAvailable;

    public bool NotifyTurnFinished
    {
        get => _settings.Notifications.TurnFinished;
        set => Set(value, v => _settings.Notifications.TurnFinished = v);
    }

    public bool NotifyNeedsInput
    {
        get => _settings.Notifications.NeedsInput;
        set => Set(value, v => _settings.Notifications.NeedsInput = v);
    }

    public bool NotifyProcessErrors
    {
        get => _settings.Notifications.ProcessErrors;
        set => Set(value, v => _settings.Notifications.ProcessErrors = v);
    }

    public bool NotifyUsageAlerts
    {
        get => _settings.Notifications.UsageAlerts;
        set => Set(value, v => _settings.Notifications.UsageAlerts = v);
    }

    public bool NotifySignIn
    {
        get => _settings.Notifications.SignIn;
        set => Set(value, v => _settings.Notifications.SignIn = v);
    }

    public bool NotifyUpdateReady
    {
        get => _settings.Notifications.UpdateReady;
        set => Set(value, v => _settings.Notifications.UpdateReady = v);
    }

    public bool ShowBadge
    {
        get => _settings.Notifications.Badge;
        set => Set(value, v => _settings.Notifications.Badge = v);
    }

    public bool AnimateIcon
    {
        get => _settings.Notifications.AnimateIcon;
        set => Set(value, v => _settings.Notifications.AnimateIcon = v);
    }

    [RelayCommand]
    private void ResetNotifications()
    {
        _settings.Notifications = new NotificationSettings();
        Save();
        OnPropertyChanged(nameof(NotifyTurnFinished));
        OnPropertyChanged(nameof(NotifyNeedsInput));
        OnPropertyChanged(nameof(NotifyProcessErrors));
        OnPropertyChanged(nameof(NotifyUsageAlerts));
        OnPropertyChanged(nameof(NotifySignIn));
        OnPropertyChanged(nameof(NotifyUpdateReady));
        OnPropertyChanged(nameof(NotifyProjectActions));
        OnPropertyChanged(nameof(ShowBadge));
        OnPropertyChanged(nameof(AnimateIcon));
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

    // ---- Keyboard (DESIGN.md §14) ------------------------------------------------------------------------------------

    public ObservableCollection<ShortcutRow> ShortcutRows { get; } = [];

    public string SuffixesShortcutText => _services.Tips.Text(KeyboardShortcuts.Suffixes) ?? "no shortcut";

    private ShortcutEditor? _recording;

    /// <summary>A shortcut is being recorded: the window passes the next key press to <see cref="RecordShortcut"/>.</summary>
    public bool IsRecordingShortcut => _recording is not null;

    [RelayCommand]
    private void StartRecording(ShortcutEditor? editor)
    {
        CancelRecording();
        if (editor is not null)
        {
            editor.Error = null;
            editor.IsRecording = true;
            _recording = editor;
        }
    }

    public void CancelRecording()
    {
        if (_recording is not null)
        {
            _recording.IsRecording = false;
            _recording = null;
        }
    }

    /// <summary>
    /// The keys pressed while recording. Refused, with the reason shown, when another command or suffix already uses
    /// them, or when they'd get in the way of typing.
    /// </summary>
    public void RecordShortcut(KeyChord chord)
    {
        if (_recording is not { } editor)
        {
            return;
        }
        var goToTab = editor is ShortcutRow { Command.Id: KeyboardShortcuts.GoToTab };
        if (goToTab)
        {
            if (chord.Key.Length != 2 || chord.Key[0] != 'D' || chord.Key[1] is < '1' or > '9')
            {
                editor.Error = "Press a number key from 1 to 9, with the modifier keys you want.";
                return;
            }
            chord = chord with { Key = "D1" };
        }
        if (!AllowedWhileTyping(chord))
        {
            editor.Error = "Add Ctrl, Alt or Cmd, so typing still works.";
            return;
        }
        var id = editor switch
        {
            ShortcutRow row => row.Command.Id,
            QuickSuffixEditor suffix => suffix.Suffix.Id,
            _ => null,
        };
        if (KeyboardShortcuts.FindConflict(_settings, chord, id, Shortcuts.IsMac) is { } conflict)
        {
            editor.Error = $"{chord.Display(Shortcuts.IsMac)} is already used by {conflict}.";
            return;
        }
        switch (editor)
        {
            case ShortcutRow row when chord == row.Command.Default:
                _settings.Keyboard.Bindings.Remove(row.Command.Id);
                break;
            case ShortcutRow row:
                _settings.Keyboard.Bindings[row.Command.Id] = chord.ToString();
                break;
            case QuickSuffixEditor suffix:
                suffix.Suffix.Shortcut = chord.ToString();
                break;
        }
        editor.Error = null;
        CancelRecording();
        SaveShortcuts();
    }

    /// <summary>Letters, digits and punctuation need Ctrl, Alt or Cmd; Escape, Tab, Enter and F-keys don't.</summary>
    private static bool AllowedWhileTyping(KeyChord chord) =>
        (chord.Modifiers & (ChordModifiers.Primary | ChordModifiers.Ctrl | ChordModifiers.Alt)) != 0
        || chord.Key is "Escape" or "Tab" or "Enter" or "Back" or "Delete" || (chord.Key.Length > 1 && chord.Key[0] == 'F' && char.IsDigit(chord.Key[1]));

    [RelayCommand]
    private void ResetShortcut(ShortcutRow? row)
    {
        if (row is not null && _settings.Keyboard.Bindings.Remove(row.Command.Id))
        {
            SaveShortcuts();
        }
    }

    /// <summary>Removes a shortcut: the command is then only in menus and buttons.</summary>
    [RelayCommand]
    private void ClearShortcut(ShortcutEditor? editor)
    {
        switch (editor)
        {
            case ShortcutRow row:
                _settings.Keyboard.Bindings[row.Command.Id] = "";
                break;
            case QuickSuffixEditor suffix:
                suffix.Suffix.Shortcut = null;
                break;
            default:
                return;
        }
        SaveShortcuts();
    }

    [RelayCommand]
    private void ResetKeyboard()
    {
        CancelRecording();
        _settings.Keyboard = new KeyboardSettings();
        ShortcutRows.Clear();
        foreach (var command in KeyboardShortcuts.All)
        {
            ShortcutRows.Add(new ShortcutRow(command, _settings.Keyboard));
        }
        SaveShortcuts();
    }

    private void SaveShortcuts()
    {
        Save();
        foreach (var row in ShortcutRows)
        {
            row.Refresh();
        }
        foreach (var suffix in Suffixes)
        {
            suffix.Refresh();
        }
        OnPropertyChanged(nameof(SuffixesShortcutText));
    }

    // ---- Advanced --------------------------------------------------------------------------------------------------

    public string ExtraArguments
    {
        get => _settings.Advanced.ExtraArguments;
        set => Set(value, v => _settings.Advanced.ExtraArguments = v ?? "");
    }

    [RelayCommand]
    private Task OpenDataFolderAsync() => _services.Platform.RevealFolderAsync(_services.Paths.DataDirectory);

    /// <summary>Each session's raw protocol traffic, to the log folder (DESIGN.md §13, "Logging").</summary>
    public bool LogProtocol
    {
        get => _settings.Advanced.LogProtocol;
        set => Set(value, v => _settings.Advanced.LogProtocol = v);
    }

    [RelayCommand]
    private Task OpenLogFolderAsync()
    {
        Directory.CreateDirectory(_services.Paths.ProtocolLogDirectory);
        return _services.Platform.RevealFolderAsync(_services.Paths.ProtocolLogDirectory);
    }

    // ---- Diagnostics (DESIGN.md §16, "Staying tolerant at runtime") -------------------------------------------------

    public string MinimumVersionText => ClaudeLocator.MinimumVersion.ToString();

    public string InstalledVersionText => _services.InstalledClaudeVersion?.ToString() ?? "Not found";

    /// <summary>Whether the login shell's environment is used, which shell and how long it took, or why not. Never values.</summary>
    public string LoginShellText => _services.UserEnvironment.Describe();

    /// <summary>Whether the computer is kept awake for tabs connected to the Claude app, or why not (DESIGN.md §18).</summary>
    public string KeepAwakeText => _services.RemoteControl.DescribeKeepAwake();

    /// <summary>What Claude Code has sent this run that Claudette doesn't know, in words.</summary>
    public string DiagnosticsText => DiagnosticsReport(includeHeader: false);

    [RelayCommand]
    private void RefreshDiagnostics()
    {
        OnPropertyChanged(nameof(DiagnosticsText));
        OnPropertyChanged(nameof(InstalledVersionText));
        OnPropertyChanged(nameof(LoginShellText));
        OnPropertyChanged(nameof(KeepAwakeText));
    }

    [RelayCommand]
    private Task CopyDiagnosticsAsync() => _services.Platform.SetClipboardTextAsync(DiagnosticsReport(includeHeader: true));

    /// <summary>The Diagnostics page's contents, and with the header, the report copied for a bug report.</summary>
    internal string DiagnosticsReport(bool includeHeader)
    {
        var snapshot = _services.Diagnostics.Snapshot();
        var lines = new List<string>();
        if (includeHeader)
        {
            lines.Add($"Claudette {_services.Build.Description}");
            lines.Add($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier})");
            lines.Add($"Claude Code: {InstalledVersionText}{(_services.Install is { } install ? $" at {install.Path}" : "")}");
            lines.Add($"Minimum supported Claude Code: {MinimumVersionText}");
            lines.Add($"Login shell environment: {LoginShellText}");
            lines.Add($"Claude app (Remote Control): new tabs {(ConnectNewTabsToClaudeApp ? "connect" : "don't connect")}; "
                + (RemoteControlUnavailableText ?? "available for this account") + ".");
            lines.Add($"Keeping the computer awake: {KeepAwakeText}");
            lines.Add($"Protocol logging: {(LogProtocol ? "on" : "off")}");
            lines.Add("");
        }
        lines.Add(snapshot.UnknownMessageCount == 0
            ? "Unknown message types: none"
            : $"Unknown message types ({snapshot.UnknownMessageCount} skipped): " + string.Join(", ", snapshot.UnknownMessageTypes.Select(t => $"{t.Key} ×{t.Value}")));
        lines.Add(snapshot.UnknownFields.Count == 0
            ? "New fields: none"
            : "New fields: " + string.Join(", ", snapshot.UnknownFields.Select(f => $"{f.Key} ×{f.Value}")));
        lines.Add($"Lines that couldn't be read: {snapshot.ParseErrors}");
        return string.Join(Environment.NewLine, lines);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private void Set<T>(T value, Action<T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        apply(value);
        OnPropertyChanged(property);
        Save();
    }

    private void Save() => _services.SaveSettings();
}
