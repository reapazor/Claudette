namespace Claudette.Core.Library;

/// <summary>
/// File writes for the session library (DESIGN.md §9, "Writing"). Each file is written under a temporary name in the
/// same folder and then renamed over the target, so a sync client never uploads a half-written file.
/// </summary>
internal static class LibraryFiles
{
    /// <summary>The extension of in-progress files; anything ending in it is ignored when reading the library.</summary>
    public const string TempExtension = ".tmp";

    /// <summary>
    /// Last-write times this close count as the same. Some file systems keep coarse times (FAT, and the virtual drive of
    /// Google Drive for desktop, keep 2 seconds), so an exact comparison would copy an unchanged file every time.
    /// </summary>
    public static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How many times a rename over a library file, or a read of one, is tried. On Windows a rename fails while anything
    /// has the file open (History reading a record, say), and a read fails while a rename is under way; either only
    /// lasts a moment.
    /// </summary>
    private const int Attempts = 20;

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="target"/> unless the target already has the same length and
    /// last-write time. The copy gets the source's last-write time. The source is only read, and is shared so that
    /// another process can keep writing it.
    /// </summary>
    /// <returns>True when the file was copied.</returns>
    public static Task<bool> CopyIfChangedAsync(string source, string target, CancellationToken cancellationToken) =>
        CopyAsync(source, target, force: false, cancellationToken);

    /// <inheritdoc cref="CopyIfChangedAsync"/>
    /// <param name="force">Copies the file even when the target looks unchanged (<b>Sync now</b>, DESIGN.md §9).</param>
    public static async Task<bool> CopyAsync(string source, string target, bool force, CancellationToken cancellationToken)
    {
        var from = new FileInfo(source);
        if (!from.Exists)
        {
            throw new FileNotFoundException($"Can't copy {source}: it doesn't exist.", source);
        }
        // Taken before copying: if the source changes meanwhile, the next copy sees a different time and copies again.
        var lastWrite = from.LastWriteTimeUtc;
        if (!force && IsSame(from.Length, lastWrite, new FileInfo(target)))
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
    public static async Task WriteTextAsync(string target, string text, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = TempPath(target);
        try
        {
            await File.WriteAllTextAsync(temp, text, cancellationToken).ConfigureAwait(false);
            Replace(temp, target);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <inheritdoc cref="WriteTextAsync"/>
    public static void WriteText(string target, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = TempPath(target);
        try
        {
            File.WriteAllText(temp, text);
            Replace(temp, target);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>
    /// Reads a library file written by <see cref="WriteText"/> or <see cref="WriteTextAsync"/>. It's opened without
    /// sharing deletion: a rename that fails over a file open that way can leave the file deleted once it's closed.
    /// </summary>
    public static string ReadText(string path)
    {
        var spin = new SpinWait();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException) && attempt < Attempts)
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }
    }

    public static bool IsTemp(string path) => path.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase);

    private static bool IsSame(long length, DateTime lastWriteUtc, FileInfo target) =>
        target.Exists && target.Length == length && (target.LastWriteTimeUtc - lastWriteUtc).Duration() < TimestampTolerance;

    /// <summary>A unique name next to <paramref name="target"/>, so the rename stays on one volume.</summary>
    private static string TempPath(string target) => $"{target}.{Guid.NewGuid():N}{TempExtension}";

    /// <summary>Renames <paramref name="temp"/> over <paramref name="target"/>, trying again while something has the target open.</summary>
    private static void Replace(string temp, string target)
    {
        var spin = new SpinWait();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < Attempts)
            {
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }
    }

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
