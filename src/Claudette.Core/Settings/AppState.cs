using Claudette.Core.Development;
using Claudette.Core.Sessions;

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

    /// <summary>A folder group's color index, by folder path.</summary>
    public Dictionary<string, int> GroupColors { get; set; } = [];

    /// <summary>Folder groups the user collapsed.</summary>
    public List<string> CollapsedGroups { get; set; } = [];

    /// <summary>The user collapsed the sidebar to its rail of status icons (DESIGN.md §4, "Sidebar").</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>The sidebar's width as the user dragged it, or null for the default.</summary>
    public double? SidebarWidth { get; set; }

    /// <summary>
    /// Where a project from another machine lives on this one, keyed by normalized git remote and path in the repo
    /// (DESIGN.md §9, "Restoring on another machine").
    /// </summary>
    public Dictionary<string, string> FolderMappings { get; set; } = [];

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

    public TabOverrides Overrides { get; set; } = new();

    /// <summary>Quick suffixes kept on this tab, by suffix id.</summary>
    public List<string> KeptSuffixes { get; set; } = [];

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

    public bool HasAny => Model is not null || Effort is not null || PermissionMode is not null || CheckIns is not null || ShowProcessMonitor is not null;
}

public sealed class RecentFolder
{
    public string Path { get; set; } = "";

    public DateTimeOffset LastUsed { get; set; }
}
