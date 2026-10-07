using Claudette.Core.Git;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;

namespace Claudette.Core.Library;

/// <summary>
/// The session record in the library, <c>sessions/&lt;session-id&gt;/record.json</c> (DESIGN.md §9, "What's in the
/// library"): the tab's name and settings, token stats, which machine last used it and when, and the project identity.
/// Serialized with <see cref="Settings.JsonFileStore{T}.Options"/>.
/// </summary>
public sealed class SessionRecord
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string SessionId { get; set; } = "";

    /// <summary>The name shown for the session: the user's name if they gave one, else Claude Code's.</summary>
    public string? Name { get; set; }

    /// <summary>The name Claude Code gave the session.</summary>
    public string? AutoName { get; set; }

    /// <summary>A name the user chose.</summary>
    public string? UserName { get; set; }

    /// <summary>The icon the user marked the tab with (DESIGN.md §4, "Marks"), as <see cref="TabMarks.Key"/> writes it; null for none.</summary>
    public string? Mark { get; set; }

    public string? Model { get; set; }

    public string? Effort { get; set; }

    public string? PermissionMode { get; set; }

    /// <summary>
    /// The tab's per-tab overrides (DESIGN.md §14), applied when the session is opened again. Null in records written
    /// before they were kept; the model and effort above are used then.
    /// </summary>
    public TabOverrides? Overrides { get; set; }

    public TokenTotals Tokens { get; set; } = new();

    /// <summary>The name of the machine that last used the session, as set in Settings → Sessions.</summary>
    public string Machine { get; set; } = "";

    public DateTimeOffset LastUsed { get; set; }

    /// <summary>The session's folder on <see cref="Machine"/>. Paths differ between machines; see <see cref="Project"/>.</summary>
    public string? Folder { get; set; }

    /// <summary>Null when the folder wasn't in a git repository.</summary>
    public ProjectIdentity? Project { get; set; }

    /// <summary><see cref="Machine"/> had uncommitted changes when it last used the session.</summary>
    public bool HadUncommittedChanges { get; set; }

    public string? FirstPrompt { get; set; }

    /// <summary>
    /// The changed files marked as reviewed (DESIGN.md §8, "Reviewed"), relative to <see cref="Folder"/> with forward
    /// slashes, so another machine finds them in its own copy of the folder. Null in records written before they were kept.
    /// </summary>
    public List<Diffs.ReviewedFile>? ReviewedFiles { get; set; }

    public string? ClaudeCodeVersion { get; set; }
}
