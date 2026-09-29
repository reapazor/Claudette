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
}

/// <summary>Per-tab settings that replace the defaults (DESIGN.md §14, "Per-tab overrides"). Null means "use the default".</summary>
public sealed class TabOverrides
{
    public string? Model { get; set; }

    public string? Effort { get; set; }

    public string? PermissionMode { get; set; }

    public CheckInSettings? CheckIns { get; set; }

    public bool HasAny => Model is not null || Effort is not null || PermissionMode is not null || CheckIns is not null;
}

public sealed class RecentFolder
{
    public string Path { get; set; } = "";

    public DateTimeOffset LastUsed { get; set; }
}
