using System.Text.Json;
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

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonFileStore<RestartSnapshot>.Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The snapshot in <paramref name="path"/> if it was written for <paramref name="nonce"/> and isn't stale.</summary>
    public static RestartSnapshot? Load(string path, string nonce, DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(path)
                || JsonSerializer.Deserialize<RestartSnapshot>(File.ReadAllText(path), JsonFileStore<RestartSnapshot>.Options) is not { } snapshot)
            {
                return null;
            }
            return snapshot.Nonce == nonce && now - snapshot.CreatedAt <= MaxAge ? snapshot : null;
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
