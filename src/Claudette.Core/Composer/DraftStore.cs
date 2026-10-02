using System.Text.Json;
using Claudette.Core.Development;
using Claudette.Core.Files;
using Claudette.Core.Settings;

namespace Claudette.Core.Composer;

/// <summary>A message put aside with <b>Stash</b> (DESIGN.md §5, "Drafts and the stash").</summary>
/// <param name="Folder">The folder of the tab it was stashed from, to say where it was written.</param>
public sealed record StashedDraft(string Id, DateTimeOffset StashedAt, string Folder, TabDraft Draft);

/// <summary>
/// Unsent messages kept on disk (DESIGN.md §5, "Drafts and the stash"): each tab's draft, so it outlives quitting, and
/// the stash, which every tab shares. A file each, in <see cref="FolderName"/> in the data folder, written whole or not
/// at all; they belong to this machine and never sync. Like <see cref="Git.GitWorktrees"/>, nothing here throws: a file
/// that can't be read or written reads as none, or is left as it was.
/// </summary>
public sealed class DraftStore(string directory)
{
    public const string FolderName = "drafts";

    private const string TabPrefix = "tab-";
    private const string Extension = ".json";

    private static readonly JsonSerializerOptions Options = JsonFileStore<AppState>.Options;

    public string Directory => directory;

    private string StashDirectory => Path.Combine(directory, "stash");

    /// <summary>The tab's saved draft, or null when it has none.</summary>
    public TabDraft? Load(string tabId) => IsSafeId(tabId) ? Read<TabDraft>(TabFile(tabId)) : null;

    /// <summary>Saves the tab's draft, or deletes it when <paramref name="draft"/> is null or empty. False when it couldn't be written.</summary>
    public bool Save(string tabId, TabDraft? draft)
    {
        if (!IsSafeId(tabId))
        {
            return false;
        }
        return draft is null || draft.IsEmpty ? Delete(TabFile(tabId)) : Write(TabFile(tabId), draft);
    }

    /// <summary>Deletes the drafts of every tab but <paramref name="tabIds"/>: tabs that weren't restored, and won't be.</summary>
    public void KeepOnly(IReadOnlyCollection<string> tabIds)
    {
        foreach (var file in Files(directory, $"{TabPrefix}*{Extension}"))
        {
            var id = Path.GetFileNameWithoutExtension(file)[TabPrefix.Length..];
            if (!tabIds.Contains(id))
            {
                Delete(file);
            }
        }
    }

    /// <summary>The stash, newest first. Entries that can't be read are left out.</summary>
    public IReadOnlyList<StashedDraft> LoadStash() =>
        [.. Files(StashDirectory, $"*{Extension}").Select(Read<StashedDraft>).OfType<StashedDraft>().Where(s => IsSafeId(s.Id)).OrderByDescending(s => s.StashedAt)];

    /// <summary>Adds an entry to the stash. False when it couldn't be written.</summary>
    public bool AddToStash(StashedDraft entry) => IsSafeId(entry.Id) && Write(StashFile(entry.Id), entry);

    /// <summary>Takes an entry out of the stash. False when it couldn't be deleted.</summary>
    public bool RemoveFromStash(string id) => IsSafeId(id) && Delete(StashFile(id));

    private string TabFile(string tabId) => Path.Combine(directory, $"{TabPrefix}{tabId}{Extension}");

    private string StashFile(string id) => Path.Combine(StashDirectory, $"{id}{Extension}");

    /// <summary>Ids name files, so only letters, digits and dashes: a tab's id is a GUID.</summary>
    private static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(AtomicFile.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Write<T>(string path, T value)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(value, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> Files(string folder, string pattern)
    {
        try
        {
            return System.IO.Directory.Exists(folder) ? System.IO.Directory.GetFiles(folder, pattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
