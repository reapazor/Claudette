using System.Text.Json;
using Claudette.Core.Files;
using Claudette.Core.Settings;

namespace Claudette.Core.Development;

/// <summary>
/// What one build of Claudette hands the next when it restarts into a new build (DESIGN.md §9, "Working on
/// Claudette"): every open tab, pinned or not, what's typed in each, the selected tab and the window's placement.
/// </summary>
public sealed class RestartSnapshot
{
    /// <summary>A snapshot older than this is ignored: the restart it was for didn't happen.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long the tabs closed for an update wait to be opened again (DESIGN.md §2, "Updating Claudette"). Windows
    /// normally starts the new version itself; if it doesn't, the next launch within this time takes them.
    /// </summary>
    public static readonly TimeSpan UpdateMaxAge = TimeSpan.FromDays(1);

    /// <summary>Ties the snapshot to the launch it was written for.</summary>
    public string Nonce { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The open tabs, in order.</summary>
    public List<TabState> Tabs { get; set; } = [];

    public string? SelectedTabId { get; set; }

    /// <summary>Unsent messages, by tab id.</summary>
    public Dictionary<string, TabDraft> Drafts { get; set; } = [];

    /// <summary>Tabs whose Claude Code was running: they start again straight away instead of when selected.</summary>
    public List<string> RunningTabIds { get; set; } = [];

    public WindowPlacement? Window { get; set; }

    /// <summary>Set when the restart is for installing a release, rather than a new source build.</summary>
    public AppUpdateHandover? Update { get; set; }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonFileStore<RestartSnapshot>.Options));

    /// <summary>
    /// The snapshot in <paramref name="path"/> if it was written for <paramref name="nonce"/> and isn't stale. With no
    /// nonce, as when Claudette is opened by hand after an update, only an update's snapshot is taken.
    /// </summary>
    public static RestartSnapshot? Load(string path, string? nonce, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path)
                || JsonSerializer.Deserialize<RestartSnapshot>(File.ReadAllText(path), JsonFileStore<RestartSnapshot>.Options) is not { } snapshot)
            {
                return null;
            }
            var matches = nonce is null ? snapshot.Update is not null : snapshot.Nonce == nonce;
            var maxAge = snapshot.Update is null ? MaxAge : UpdateMaxAge;
            return matches && now - snapshot.CreatedAt <= maxAge ? snapshot : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stale snapshots are ignored anyway.
        }
    }
}

/// <summary>The versions an update restart went from and to, so the new version can tell whether it installed.</summary>
public sealed record AppUpdateHandover(string From, string To);

/// <summary>A tab's unsent message: the text, the one-off quick suffixes added to it, and its attached images (DESIGN.md §5).</summary>
public sealed record TabDraft(string Text, IReadOnlyList<string> SuffixIds, IReadOnlyList<DraftImage>? Images = null);

/// <summary>An image attached to an unsent message, with the name its thumbnail shows (DESIGN.md §5, "Attachments").</summary>
public sealed record DraftImage(string Name, byte[] Data);

/// <summary>The main window's position and size, in device-independent pixels except the position.</summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool IsMaximized);

/// <summary>
/// The new build tells the old one it's up by writing the restart's nonce to a file; until then the old build waits,
/// ready to take its tabs back if the new one fails (DESIGN.md §9, "Working on Claudette").
/// </summary>
public static class RestartHandshake
{
    public static void SignalReady(string path, string nonce)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, nonce);
    }

    public static bool IsReady(string path, string nonce)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path).Trim() == nonce;
        }
        catch (IOException)
        {
            // Being written.
            return false;
        }
    }
}
