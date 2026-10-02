using Claudette.Core.LoginItems;
using Claudette.Core.Settings;
using Claudette.Core.Updates;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels.Settings;

/// <summary>A choice for <b>Remove worktrees no tab has used for</b>.</summary>
public sealed record InactiveDaysChoice(int Days)
{
    public override string ToString() => Days == 0 ? "Never" : $"{Days} days";
}

/// <summary>
/// Settings → General (DESIGN.md §14): starting at login, closing and renaming tabs, Claude's service status, messages
/// sent while Claude works, cleaning up worktrees, and Claudette's own updates.
/// </summary>
public sealed partial class GeneralPage : SettingsPage
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
        Entry("Messages sent while Claude works", pageText: "Messages sent while Claude works"),
        Entry("Remove worktrees once their work is merged", pageText: "Remove worktrees once everything in them is merged"),
        Entry("Remove worktrees no tab has used", pageText: "Remove worktrees no tab has used for"),
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

    // ---- Messages sent while Claude works (DESIGN.md §5, "Queued messages") --------------------------------------

    /// <summary>Sent at once, for Claude to read at its next step.</summary>
    public bool SteerWhileWorking
    {
        get => Settings.General.MessagesWhileWorking == WhileWorking.Steer;
        set
        {
            if (value)
            {
                SetWhileWorking(WhileWorking.Steer);
            }
        }
    }

    /// <summary>Held until the turn ends, then sent as the next turn.</summary>
    public bool QueueWhileWorking
    {
        get => Settings.General.MessagesWhileWorking == WhileWorking.Queue;
        set
        {
            if (value)
            {
                SetWhileWorking(WhileWorking.Queue);
            }
        }
    }

    private void SetWhileWorking(WhileWorking choice)
    {
        if (Settings.General.MessagesWhileWorking == choice)
        {
            return;
        }
        Settings.General.MessagesWhileWorking = choice;
        OnPropertyChanged(nameof(SteerWhileWorking));
        OnPropertyChanged(nameof(QueueWhileWorking));
        Save();
    }

    // ---- Cleaning up worktrees (DESIGN.md §4, "Cleaning up worktrees") ---------------------------------------------

    public bool RemoveMergedWorktrees
    {
        get => Settings.General.RemoveMergedWorktrees;
        set
        {
            Set(value, v => Settings.General.RemoveMergedWorktrees = v);
            OnPropertyChanged(nameof(CanCleanUpWorktrees));
        }
    }

    /// <summary>The choices for <b>Remove worktrees no tab has used for</b>; 0 is never.</summary>
    public IReadOnlyList<InactiveDaysChoice> InactiveDaysChoices { get; } = [.. new[] { 0, 7, 14, 30, 90 }.Select(d => new InactiveDaysChoice(d))];

    public InactiveDaysChoice RemoveInactiveWorktreesAfter
    {
        get => InactiveDaysChoices.FirstOrDefault(c => c.Days == Settings.General.RemoveInactiveWorktreesAfterDays)
            ?? new InactiveDaysChoice(Settings.General.RemoveInactiveWorktreesAfterDays);
        set
        {
            if (value is not null && value.Days != Settings.General.RemoveInactiveWorktreesAfterDays)
            {
                Set(value.Days, v => Settings.General.RemoveInactiveWorktreesAfterDays = v);
                OnPropertyChanged(nameof(CanCleanUpWorktrees));
            }
        }
    }

    /// <summary>What the last cleanup removed, and when: "Last removed brisk-otter and calm-heron, 2 h ago."</summary>
    public string WorktreeCleanupText
    {
        get
        {
            if (_cleanupResult is { } result)
            {
                return result;
            }
            if (Services.State.LastWorktreeCleanup is not { Removed.Count: > 0 } last)
            {
                return "Nothing removed yet. Claudette looks a minute after it starts, then every hour.";
            }
            var names = last.Removed.Select(p => Path.GetFileName(Path.TrimEndingDirectorySeparator(p))).ToList();
            var list = names.Count == 1 ? names[0] : $"{string.Join(", ", names[..^1])} and {names[^1]}";
            return $"Last removed {list}, {Core.Formats.Ago(Services.Time.GetUtcNow() - last.At)}.";
        }
    }

    public bool CanCleanUpWorktrees => (RemoveMergedWorktrees || Settings.General.RemoveInactiveWorktreesAfterDays > 0) && !_cleaningUp;

    private string? _cleanupResult;
    private bool _cleaningUp;

    /// <summary><b>Clean up now</b>: a pass straight away, rather than at the next hour.</summary>
    [RelayCommand]
    private async Task CleanUpWorktreesAsync()
    {
        _cleaningUp = true;
        OnPropertyChanged(nameof(CanCleanUpWorktrees));
        try
        {
            var removed = await Services.Worktrees.RunAsync();
            _cleanupResult = removed.Count switch
            {
                0 => "Nothing to remove right now.",
                1 => $"Removed {Path.GetFileName(removed[0])}.",
                _ => $"Removed {removed.Count} worktrees: {string.Join(", ", removed.Select(Path.GetFileName))}.",
            };
        }
        finally
        {
            _cleaningUp = false;
            OnPropertyChanged(nameof(CanCleanUpWorktrees));
            OnPropertyChanged(nameof(WorktreeCleanupText));
        }
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
