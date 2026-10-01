using Claudette.Core.LoginItems;
using Claudette.Core.Settings;
using Claudette.Core.Updates;

namespace Claudette.App.ViewModels.Settings;

/// <summary>
/// Settings → General (DESIGN.md §14): starting at login, closing and renaming tabs, Claude's service status, and
/// Claudette's own updates.
/// </summary>
public sealed class GeneralPage : SettingsPage
{
    private LoginItemStatus? _loginItem;
    private bool _changingLoginItem;

    public GeneralPage(SettingsContext context) : base(context, SettingsCategory.General) => _ = RefreshStartAtLoginAsync();

    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Start Claudette when I log in"),
        Entry("Confirm before closing a tab where Claude is working"),
        Entry("Also rename the session in Claude Code when I rename a tab"),
        Entry("Show Claude's service status"),
        Entry("Claudette version", pageText: "Claudette updates"),
        Entry("Check for Claudette updates automatically"),
        Entry("Include pre-releases"),
        Entry("Check for Claudette updates now", pageText: "Check now"),
    ];

    // ---- Starting at login (DESIGN.md §9, "Starting at login") ----------------------------------------------------

    /// <summary>
    /// Reads whether Claudette starts at login, as the window opens: Task Manager, System Settings or another copy of
    /// Claudette may have changed it since (DESIGN.md §9, "Starting at login").
    /// </summary>
    public async Task RefreshStartAtLoginAsync() => UseLoginItem(await Services.StartAtLogin.ReadAsync());

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
            var current = Services.ThisCopy;
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
            UseLoginItem(await Services.StartAtLogin.SetAsync(on));
        }
        finally
        {
            _changingLoginItem = false;
            OnPropertyChanged(nameof(CanChangeStartAtLogin));
        }
    }

    /// <summary>What the OS says now: everything the switch and its notes show follows it.</summary>
    private void UseLoginItem(LoginItemStatus status)
    {
        _loginItem = status;
        OnPropertyChanged(string.Empty);
    }

    // ---- Tabs and service status ------------------------------------------------------------------------------------

    public bool ConfirmCloseWorkingTab
    {
        get => Settings.General.ConfirmCloseWorkingTab;
        set => Set(value, v => Settings.General.ConfirmCloseWorkingTab = v);
    }

    public bool RenameInClaudeCode
    {
        get => Settings.General.RenameInClaudeCode;
        set => Set(value, v => Settings.General.RenameInClaudeCode = v);
    }

    /// <summary>
    /// The header's dot and the banner, from status.claude.com (DESIGN.md §18, "Service status"). Off stops the checks.
    /// </summary>
    public bool ShowServiceStatus
    {
        get => Settings.General.ShowServiceStatus;
        set => Set(value, v => Settings.General.ShowServiceStatus = v);
    }

    // ---- Claudette's own updates (DESIGN.md §2, "Updating Claudette") ---------------------------------------------

    /// <summary>Check GitHub for new Claudette releases.</summary>
    public bool CheckForAppUpdates
    {
        get => Settings.General.CheckForAppUpdates;
        set => Set(value, v => Settings.General.CheckForAppUpdates = v);
    }

    public bool IncludePrereleases
    {
        get => Settings.General.IncludePrereleases;
        set
        {
            Set(value, v => Settings.General.IncludePrereleases = v);
            // Takes effect with the next check: do that now.
            AppUpdates?.CheckNowCommand.Execute(null);
        }
    }

    /// <summary>Claudette's version, update status and actions, the same as the sidebar's badge. Null in some tests.</summary>
    public AppUpdateViewModel? AppUpdates { get; internal set; }

    public bool HasAppUpdates => AppUpdates is not null;

    /// <summary>Starting at login is the OS's, not a setting, so it stays as it is.</summary>
    protected override void ResetSettings()
    {
        Settings.General = new GeneralSettings();
        Save();
    }
}
