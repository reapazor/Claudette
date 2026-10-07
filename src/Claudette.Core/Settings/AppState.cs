using Claudette.Core.Development;
using Claudette.Core.LoginItems;
using Claudette.Core.Sessions;
using Claudette.Core.Status;

namespace Claudette.Core.Settings;

/// <summary>
/// Per-machine state: saved tabs, recent and favorite folders (DESIGN.md §4, §9). Stored as <c>state.json</c> next to
/// the other local data. Unlike settings, none of it syncs between machines.
/// </summary>
public sealed class AppState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<TabState> Tabs { get; set; } = [];

    public string? SelectedTabId { get; set; }

    public List<RecentFolder> RecentFolders { get; set; } = [];

    public List<string> FavoriteFolders { get; set; } = [];

    /// <summary>A folder group's color, as <c>#RRGGBB</c>, by folder path (DESIGN.md §4, "Grouped by folder").</summary>
    public Dictionary<string, string> FolderColors { get; set; } = [];

    /// <summary>
    /// Group colors saved before <see cref="FolderColors"/>, as an index into the group colors. A folder's entry moves to
    /// <see cref="FolderColors"/> when its group next opens.
    /// </summary>
    public Dictionary<string, int> GroupColors { get; set; } = [];

    /// <summary>Folder groups the user collapsed.</summary>
    public List<string> CollapsedGroups { get; set; } = [];

    /// <summary>The user collapsed the sidebar to its rail of status icons (DESIGN.md §4, "Sidebar").</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>The sidebar's width as the user dragged it, or null for the default.</summary>
    public double? SidebarWidth { get; set; }

    /// <summary>The side panel's width as the user dragged it, or null for the default (DESIGN.md §3). One for every tab.</summary>
    public double? SidePanelWidth { get; set; }

    /// <summary>
    /// The usage header is drawn taller, with charts of the session and the week (DESIGN.md §6, "Detailed header").
    /// Its chevron and Settings → Appearance both set it. Kept per machine, like the sidebar's collapsed state.
    /// </summary>
    public bool DetailedUsageHeader { get; set; }

    /// <summary>
    /// This machine's id in the session library's shared usage (DESIGN.md §6, "Sharing across machines"): made up once, so
    /// its file keeps its name when the machine's is changed, and two machines with the same name don't share a file.
    /// </summary>
    public string? MachineId { get; set; }

    /// <summary>
    /// Where a project from another machine lives on this one, keyed by normalized git remote and path in the repo
    /// (DESIGN.md §9, "Restoring on another machine").
    /// </summary>
    public Dictionary<string, string> FolderMappings { get; set; } = [];

    /// <summary>
    /// The marks of sessions on this machine, by session id (DESIGN.md §4, "Marks"), so a session opened from History
    /// after its tab closed has its mark again. Only sessions whose mark was set or cleared here are kept, and a library
    /// record written since goes first (<see cref="TabMarks.ForSession"/>).
    /// </summary>
    public Dictionary<string, SessionMark> SessionMarks { get; set; } = [];

    /// <summary>
    /// Folders the user said to trust, so Claude Code starts in them, and in what's inside them, without asking about
    /// what their own configuration runs (DESIGN.md §7, "Folder trust"). Null until the first launch that knows about
    /// trust, which trusts the folders already used here: their tabs, and the recent and favorite folders.
    /// </summary>
    public List<string>? TrustedFolders { get; set; }

    /// <summary>Sets <see cref="TrustedFolders"/>, the first time, to the folders already used here.</summary>
    public void TrustFoldersAlreadyUsed() =>
        TrustedFolders ??= [.. Tabs.Select(t => t.Folder).Concat(RecentFolders.Select(r => r.Path)).Concat(FavoriteFolders)
            .Where(f => f.Length > 0).Distinct(StringComparer.Ordinal)];

    /// <summary>What settings sync last saw or published, per setting path (DESIGN.md §14).</summary>
    public SettingsSyncState? SettingsSync { get; set; }

    /// <summary>
    /// <b>Update on next launch</b>: run the Claude Code update at the next start, before any tab starts its process,
    /// because WinGet can't replace a running <c>claude</c> (DESIGN.md §12).
    /// </summary>
    public bool UpdateClaudeOnNextLaunch { get; set; }

    /// <summary>
    /// For a source build of Claudette: restart into a new build by itself once no tab is working, instead of asking
    /// (DESIGN.md §9, "Working on Claudette").
    /// </summary>
    public bool RestartOnNewBuild { get; set; }

    /// <summary>
    /// The models the installed Claude Code last offered, with their effort levels, so Settings lists what Claude Code
    /// has rather than a fixed list (DESIGN.md §14). Kept per machine, since Claude Code's version differs between them.
    /// </summary>
    public List<ModelInfo> KnownModels { get; set; } = [];

    /// <summary>The main window's position and size when Claudette last closed on this machine (DESIGN.md §14).</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>The Claude Code version whose update badge the user dismissed: hidden until a newer one (DESIGN.md §12).</summary>
    public string? DismissedClaudeUpdate { get; set; }

    /// <summary>The Claude Code version the last "update ready" notification was for: once per version (DESIGN.md §10).</summary>
    public string? NotifiedClaudeUpdate { get; set; }

    /// <summary>The Claudette release the user chose to skip: not offered again until a newer one (DESIGN.md §2).</summary>
    public string? SkippedAppUpdate { get; set; }

    /// <summary>
    /// The project each folder uses and each project's choices (DESIGN.md §18, "Project tools"). Per machine: paths
    /// differ between machines.
    /// </summary>
    public ProjectToolState ProjectTools { get; set; } = new();

    /// <summary>
    /// The service status banner the user dismissed: hidden until a different incident arrives or things get worse
    /// (DESIGN.md §18, "Service status"). Forgotten once everything is back to operational.
    /// </summary>
    public ServiceStatusDismissal? DismissedServiceStatus { get; set; }

    /// <summary>
    /// Which copy of Claudette the login entry starts, and the installed release a source build prefers to it (DESIGN.md
    /// §9, "Starting at login"). Whether the entry is on is the OS's to say.
    /// </summary>
    public LoginItemRecord LoginItem { get; set; } = new();

    /// <summary>
    /// When a tab last worked in each worktree Claude Code made, by its path, or when Claudette first saw one no tab
    /// had used; for removing inactive ones (DESIGN.md §4, "Cleaning up worktrees").
    /// </summary>
    public Dictionary<string, DateTimeOffset> WorktreesLastUsed { get; set; } = [];

    /// <summary>The worktrees the last cleanup removed, and when, for Settings → General to say.</summary>
    public WorktreeCleanupRecord? LastWorktreeCleanup { get; set; }
}

/// <summary>What a worktree cleanup removed (DESIGN.md §4, "Cleaning up worktrees").</summary>
public sealed class WorktreeCleanupRecord
{
    public DateTimeOffset At { get; set; }

    /// <summary>The removed worktrees' paths.</summary>
    public List<string> Removed { get; set; } = [];
}

/// <summary>What's saved for one tab (DESIGN.md §9, "Restore on launch").</summary>
public sealed class TabState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Folder { get; set; } = "";

    public string? SessionId { get; set; }

    /// <summary>The name Claude Code gave the session.</summary>
    public string? AutoName { get; set; }

    /// <summary>A name the user chose; never overwritten by Claude Code.</summary>
    public string? UserName { get; set; }

    public bool IsPinned { get; set; }

    /// <summary>
    /// The icon the user marked the tab with (DESIGN.md §4, "Marks"), as <see cref="TabMarks.Key"/> writes it; null for
    /// none. The tab's own state, like its name.
    /// </summary>
    public string? Mark { get; set; }

    /// <summary>
    /// Copy this tab's session to the session library after each turn, so another machine can open it from History
    /// (DESIGN.md §9, "Session library"). Off unless the tab opted in: new tabs take Settings → Sessions → Sync new
    /// tabs, and sessions opened from the library keep syncing. Not a per-tab override: it's the tab's own state.
    /// </summary>
    public bool SyncToLibrary { get; set; }

    /// <summary>
    /// Connect this tab to the Claude app with Remote Control whenever its session runs, so it reconnects by itself after
    /// a restart (DESIGN.md §18, "Remote Control"). Off unless the tab opted in: new tabs take Settings → Claude Code →
    /// Connect new tabs to the Claude app. The tab's own state, like <see cref="SyncToLibrary"/>, not an override.
    /// </summary>
    public bool RemoteControl { get; set; }

    public TabOverrides Overrides { get; set; } = new();

    /// <summary>Quick suffixes kept on this tab, by suffix id.</summary>
    public List<string> KeptSuffixes { get; set; } = [];

    /// <summary>
    /// Whether new thinking rows in this tab start expanded, as <b>Collapse all thinking</b> or <b>Expand all thinking</b>
    /// last left it (DESIGN.md §5), or null for Settings → Appearance. The tab's own state, not an override.
    /// </summary>
    public bool? ExpandThinking { get; set; }

    public TokenTotals Tokens { get; set; } = new();

    /// <summary>
    /// The local transcript to resume from, for a session opened from the library (DESIGN.md §9). Null means Claude
    /// Code's own copy, found by <see cref="SessionId"/>.
    /// </summary>
    public string? TranscriptPath { get; set; }

    /// <summary>
    /// When the session began, for the tab info card (DESIGN.md §4): its transcript's first entry for a resumed session,
    /// else when the tab first started it.
    /// </summary>
    public DateTimeOffset? SessionStartedAt { get; set; }

    /// <summary>Resume as a copy with a new session id ("Open a copy", DESIGN.md §9). Cleared once started.</summary>
    public bool ForkOnNextStart { get; set; }

    /// <summary>
    /// Resume only up to this conversation entry, leaving out what came after: a branch from a message, or a rewind
    /// (DESIGN.md §5, "Rewind and branch"). Cleared once started.
    /// </summary>
    public string? ResumeAt { get; set; }

    /// <summary>With <see cref="ResumeAt"/>: the prompt whose turn the resume drops, so Claude Code checks nothing else goes.</summary>
    public string? ResumeDropsTurn { get; set; }

    /// <summary>
    /// The tab starts Claude Code without the folder's own settings, MCP servers and CLAUDE.md (<c>--setting-sources
    /// user</c>), as chosen when asked whether to trust the folder (DESIGN.md §7, "Folder trust").
    /// </summary>
    public bool WithoutProjectSettings { get; set; }

    /// <summary>Ultracode is on in this tab, applied each time its Claude Code starts (DESIGN.md §5, "Model and effort").</summary>
    public bool Ultracode { get; set; }

    /// <summary>
    /// The main checkout whose worktree the tab works in (DESIGN.md §4, "Worktree tabs"): the group it's in. Null for a
    /// tab that isn't in a worktree.
    /// </summary>
    public string? WorktreeOf { get; set; }

    /// <summary>
    /// The worktree Claude Code creates, or opens again, with <c>--worktree</c> when it next starts in the main checkout.
    /// Cleared once <c>system/init</c> says where it is, and <see cref="Folder"/> becomes the worktree.
    /// </summary>
    public string? NewWorktree { get; set; }

    /// <summary>Folders Claude may read and edit besides <see cref="Folder"/> (<c>--add-dir</c>; DESIGN.md §4, "Extra folders").</summary>
    public List<string> ExtraFolders { get; set; } = [];

    /// <summary>The Perforce changelists Claude used in this session (DESIGN.md §18), so a restored tab shows them again.</summary>
    public List<Perforce.TrackedChangelist> Changelists { get; set; } = [];

    /// <summary>The changed files the user marked as reviewed (DESIGN.md §8, "Reviewed"), by full path.</summary>
    public List<Diffs.ReviewedFile> ReviewedFiles { get; set; } = [];

    /// <summary>
    /// A usage limit stopped the tab's last turn, and it waits for the limit to reset (DESIGN.md §6, "Continuing after a
    /// limit resets"), so a restart keeps waiting.
    /// </summary>
    public LimitWait? LimitWait { get; set; }

    /// <summary>
    /// A reply the user chose with <b>Show as the plan</b> (DESIGN.md §5, "Tasks"), so a restored tab shows it on the Tasks
    /// page again. Null when there's none, and after <c>/clear</c>.
    /// </summary>
    public ChosenPlan? Plan { get; set; }
}

/// <summary>A reply the user chose as a tab's plan (DESIGN.md §5, "Tasks").</summary>
public sealed class ChosenPlan
{
    /// <summary>The reply's Markdown, as Claude wrote it.</summary>
    public string Text { get; set; } = "";

    /// <summary>When Claude wrote the reply; null when not known.</summary>
    public DateTimeOffset? WrittenAt { get; set; }

    /// <summary>When the user chose it: a plan approved after this takes its place.</summary>
    public DateTimeOffset ChosenAt { get; set; }
}

/// <summary>Per-tab settings that replace the defaults (DESIGN.md §14, "Per-tab overrides"). Null means "use the default".</summary>
public sealed class TabOverrides
{
    public string? Model { get; set; }

    public string? Effort { get; set; }

    public string? PermissionMode { get; set; }

    public CheckInSettings? CheckIns { get; set; }

    /// <summary>The process monitor for this tab (DESIGN.md §4), or null for Settings → Processes.</summary>
    public bool? ShowProcessMonitor { get; set; }

    /// <summary>
    /// Continue a task a usage limit stopped once the limit resets (DESIGN.md §6, "Continuing after a limit resets"), or
    /// null for Settings → Usage.
    /// </summary>
    public bool? ContinueAfterLimitReset { get; set; }

    public bool HasAny => Model is not null || Effort is not null || PermissionMode is not null || CheckIns is not null || ShowProcessMonitor is not null
        || ContinueAfterLimitReset is not null;
}

public sealed class RecentFolder
{
    public string Path { get; set; } = "";

    public DateTimeOffset LastUsed { get; set; }
}
