using System.Globalization;
using Claudette.Core.Files;
using Claudette.Core.Library;

namespace Claudette.Core.ScratchPads;

/// <summary>
/// Where scratch pads are kept (DESIGN.md §18, "Scratch pad"): this machine's copy of each in <c>scratch</c> in the data
/// folder, which the pad saves to first so nothing typed waits on the library, and the shared copy of a pad with a git
/// remote in <c>scratch</c> in the session library. Files are written whole, through a temporary name. Every method
/// reads and writes files, so callers run them off the UI thread.
/// </summary>
/// <param name="localFolder">This machine's <c>scratch</c> folder.</param>
/// <param name="libraryFolder">The session library's folder now: it can change in Settings.</param>
public sealed class ScratchPadStore(string localFolder, Func<string> libraryFolder)
{
    public const string FolderName = "scratch";

    private const string Extension = ".json";

    public string LocalPath(string id) => Path.Combine(localFolder, CheckId(id) + Extension);

    public string LibraryPath(string id) => Path.Combine(libraryFolder(), FolderName, CheckId(id) + Extension);

    /// <summary>
    /// This machine's copy, or null when it has none. One that isn't a pad's file is kept beside it as
    /// <c>&lt;id&gt;.&lt;time&gt;.bad</c>, as a bad settings file is, and the pad starts empty.
    /// </summary>
    /// <exception cref="IOException">It's there but can't be read just now.</exception>
    public ScratchPadFile? ReadLocal(string id, DateTimeOffset now)
    {
        var path = LocalPath(id);
        if (!File.Exists(path))
        {
            return null;
        }
        if (ScratchPadFile.Parse(AtomicFile.ReadAllText(path)) is { } file)
        {
            return file;
        }
        File.Move(path, Path.ChangeExtension(path, $".{now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.bad"), overwrite: true);
        return null;
    }

    public void WriteLocal(string id, ScratchPadFile file) => AtomicFile.WriteAllText(LocalPath(id), file.ToJson());

    /// <summary>
    /// Brings this machine's copy and the library's together (<see cref="ScratchPadSync.Decide"/>), writing the library's
    /// when this machine's changes go there. Then looks at the copies a sync client made of the library's file when two
    /// machines wrote it at once: one the result already includes, or with the same text, is deleted; the first other
    /// one comes back as <see cref="ScratchPadSyncResult.Conflict"/>. This machine's own file isn't written: the caller
    /// saves <see cref="ScratchPadSyncResult.Local"/> if nothing was typed meanwhile.
    /// </summary>
    public ScratchPadSyncResult Sync(string id, ScratchPadFile local)
    {
        try
        {
            if (!TryReadShared(LibraryPath(id), out var library))
            {
                return Unavailable(local, "its copy of the pad is being written");
            }
            var result = ScratchPadSync.Decide(local, library);
            if (result.Kind == ScratchPadSyncKind.Pushed)
            {
                WriteShared(id, result.Library!);
                library = result.Library;
            }
            else if (result.Kind == ScratchPadSyncKind.Pulled)
            {
                library = result.Local;
            }
            if (result.Kind is ScratchPadSyncKind.Conflict or ScratchPadSyncKind.NewerFormat)
            {
                return result;
            }
            return result with { Conflict = CopyConflict(id, library, result.Local) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable(local, ex.Message);
        }
    }

    /// <summary>
    /// The user's choice about <paramref name="conflict"/> (<see cref="ScratchPadSync.Resolve"/>): writes the pad as
    /// <paramref name="text"/> to the library, and deletes the sync client's copy it came from.
    /// </summary>
    public ScratchPadSyncResult Resolve(string id, ScratchPadFile local, ScratchPadConflict conflict, string text, string machine, DateTimeOffset now)
    {
        try
        {
            if (!TryReadShared(LibraryPath(id), out var library))
            {
                return Unavailable(local, "its copy of the pad is being written");
            }
            if (library is { IsNewerFormat: true })
            {
                return new ScratchPadSyncResult(ScratchPadSyncKind.NewerFormat, local);
            }
            var result = ScratchPadSync.Resolve(local, library, conflict, text, ScratchPadSync.NewRevision(), machine, now);
            if (result.Kind == ScratchPadSyncKind.Pushed)
            {
                WriteShared(id, result.Library!);
            }
            if (result.Kind != ScratchPadSyncKind.Conflict && conflict.CopyPath is { } copy)
            {
                File.Delete(copy);
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unavailable(local, ex.Message);
        }
    }

    private void WriteShared(string id, ScratchPadFile file)
    {
        var path = LibraryPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, file.ToJson());
    }

    /// <summary>
    /// The first sync client's copy of the pad's file that <paramref name="library"/> and <paramref name="local"/> don't
    /// cover. Copies they do cover, or that hold the same text, are deleted on the way.
    /// </summary>
    private ScratchPadConflict? CopyConflict(string id, ScratchPadFile? library, ScratchPadFile local)
    {
        var folder = Path.GetDirectoryName(LibraryPath(id))!;
        if (!Directory.Exists(folder))
        {
            return null;
        }
        foreach (var path in Directory.EnumerateFiles(folder, id + "*" + Extension).Order(StringComparer.Ordinal))
        {
            if (SessionLibrary.ConflictLabel(id, Path.GetFileName(path)) is null || !TryReadShared(path, out var copy) || copy is null || copy.IsNewerFormat)
            {
                continue;
            }
            if (library?.Includes(copy.Revision) == true || copy.Text == local.Text || copy.Text == library?.Text)
            {
                File.Delete(path);
                continue;
            }
            return new ScratchPadConflict(copy, path);
        }
        return null;
    }

    /// <summary>
    /// Reads a shared copy: null when there's no file. False for one that can't be read as a pad's file, which a sync
    /// client may be writing: it's neither used nor written over.
    /// </summary>
    private static bool TryReadShared(string path, out ScratchPadFile? file)
    {
        file = null;
        if (!File.Exists(path))
        {
            return true;
        }
        file = ScratchPadFile.Parse(AtomicFile.ReadAllText(path));
        return file is not null;
    }

    private static ScratchPadSyncResult Unavailable(ScratchPadFile local, string reason) =>
        new(ScratchPadSyncKind.Unavailable, local) { Reason = reason };

    /// <summary>A pad's id is a hash: anything else could name a path outside the folder.</summary>
    private static string CheckId(string id) =>
        id.Length > 0 && id.All(char.IsAsciiHexDigit) ? id : throw new ArgumentException($"Not a scratch pad id: {id}", nameof(id));
}
