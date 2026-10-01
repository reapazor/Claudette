using Claudette.Core.Files;

namespace Claudette.Core.Library;

/// <summary>
/// File writes for the session library (DESIGN.md §9, "Writing"). Each file is written under a temporary name in the
/// same folder and then renamed over the target, so a sync client never uploads a half-written file.
/// </summary>
internal static class LibraryFiles
{
    /// <summary>The extension of in-progress files; anything ending in it is ignored when reading the library.</summary>
    public const string TempExtension = AtomicFile.TempExtension;

    /// <summary>
    /// Last-write times this close count as the same. Some file systems keep coarse times (FAT, and the virtual drive of
    /// Google Drive for desktop, keep 2 seconds), so an exact comparison would copy an unchanged file every time.
    /// </summary>
    public static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/> unless the target already has the same length and
    /// last-write time. The copy gets the source's last-write time. The source is only read, and is shared so that
    /// another process can keep writing it.
    /// </summary>
    /// <returns>True when the file was copied.</returns>
    public static Task<bool> CopyIfChangedAsync(string source, string target, CancellationToken cancellationToken) =>
        CopyAsync(source, target, force: false, cancellationToken);

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/> unless the target is the same or newer: written
    /// later, or as late and at least as long (transcripts only grow). A working copy that has turns the library
    /// doesn't, because copying it there after a turn failed, isn't overwritten with the library's older one.
    /// </summary>
    /// <returns>True when the file was copied.</returns>
    public static Task<bool> CopyIfNewerAsync(string source, string target, CancellationToken cancellationToken) =>
        CopyAsync(source, target, force: false, cancellationToken, keepNewerTarget: true);

    /// <inheritdoc cref="CopyIfChangedAsync"/>
    /// <param name="force">Copies the file even when the target looks unchanged (<b>Sync now</b>, DESIGN.md §9).</param>
    public static Task<bool> CopyAsync(string source, string target, bool force, CancellationToken cancellationToken) =>
        CopyAsync(source, target, force, cancellationToken, keepNewerTarget: false);

    private static async Task<bool> CopyAsync(string source, string target, bool force, CancellationToken cancellationToken, bool keepNewerTarget)
    {
        var from = new FileInfo(source);
        if (!from.Exists)
        {
            throw new FileNotFoundException($"Can't copy {source}: it doesn't exist.", source);
        }
        // Taken before copying: if the source changes meanwhile, the next copy sees a different time and copies again.
        var lastWrite = from.LastWriteTimeUtc;
        var to = new FileInfo(target);
        if (!force && (IsSame(from.Length, lastWrite, to) || keepNewerTarget && IsNewer(to, from.Length, lastWrite)))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = TempPath(target);
        try
        {
            var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
            }
            File.SetLastWriteTimeUtc(temp, lastWrite);
            Replace(temp, target);
            return true;
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>Writes <paramref name="text"/> to a temporary file, then renames it over <paramref name="target"/>.</summary>
    public static Task WriteTextAsync(string target, string text, CancellationToken cancellationToken) =>
        AtomicFile.WriteAllTextAsync(target, text, cancellationToken);

    /// <inheritdoc cref="WriteTextAsync"/>
    public static void WriteText(string target, string text) => AtomicFile.WriteAllText(target, text);

    /// <summary>
    /// Reads a library file written by <see cref="WriteText"/> or <see cref="WriteTextAsync"/>. It's opened without
    /// sharing deletion: a rename that fails over a file open that way can leave the file deleted once it's closed.
    /// </summary>
    public static string ReadText(string path) => AtomicFile.ReadAllText(path);

    public static bool IsTemp(string path) => AtomicFile.IsTemp(path);

    private static bool IsSame(long length, DateTime lastWriteUtc, FileInfo target) =>
        target.Exists && target.Length == length && (target.LastWriteTimeUtc - lastWriteUtc).Duration() < TimestampTolerance;

    private static bool IsNewer(FileInfo target, long length, DateTime lastWriteUtc) =>
        target.Exists && (target.LastWriteTimeUtc - lastWriteUtc >= TimestampTolerance
            || (target.LastWriteTimeUtc - lastWriteUtc).Duration() < TimestampTolerance && target.Length >= length);

    private static string TempPath(string target) => AtomicFile.TempPath(target);

    private static void Replace(string temp, string target) => AtomicFile.Replace(temp, target);

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp file is ignored when the library is read.
        }
    }
}
