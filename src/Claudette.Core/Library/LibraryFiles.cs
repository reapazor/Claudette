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
    /// Copies a transcript from the session library to <paramref name="target"/>, Claude Code's working copy, unless the
    /// target is the same or newer: written later, or as late and at least as long (transcripts only grow). A working
    /// copy that has turns the library doesn't, because copying it there after a turn failed, isn't overwritten with the
    /// library's older one. Only whole lines are copied: a sync client may have brought the library's copy over while
    /// a line was being added to it (<see cref="CopyTranscriptAsync"/>), and Claude Code would add its next line to
    /// the half one.
    /// </summary>
    /// <returns>True when the file was copied.</returns>
    public static Task<bool> CopyIfNewerAsync(string source, string target, CancellationToken cancellationToken) =>
        CopyAsync(source, target, force: false, cancellationToken, keepNewerTarget: true, append: false, wholeLines: true);

    /// <inheritdoc cref="CopyIfChangedAsync"/>
    /// <param name="force">Copies the file even when the target looks unchanged (<b>Sync now</b>, DESIGN.md §9).</param>
    public static Task<bool> CopyAsync(string source, string target, bool force, CancellationToken cancellationToken) =>
        CopyAsync(source, target, force, cancellationToken, keepNewerTarget: false, append: false, wholeLines: false);

    /// <summary>
    /// Copies a transcript, which Claude Code only ever adds to, as <see cref="CopyAsync(string, string, bool, CancellationToken)"/>
    /// does, except that a target holding the start of the source, unchanged, has just the rest appended to it rather
    /// than the whole file written again (DESIGN.md §9, "Writing"). Anything else, and <paramref name="force"/>, writes
    /// the whole file.
    /// </summary>
    public static Task<bool> CopyTranscriptAsync(string source, string target, bool force, CancellationToken cancellationToken) =>
        CopyAsync(source, target, force, cancellationToken, keepNewerTarget: false, append: !force, wholeLines: false);

    /// <summary>How much of a target's start, and of its end, has to match the source for the rest to be appended.</summary>
    internal const int AppendCheckLength = 64 * 1024;

    private static async Task<bool> CopyAsync(string source, string target, bool force, CancellationToken cancellationToken, bool keepNewerTarget, bool append, bool wholeLines)
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
        if (append && to.Exists && to.Length > 0 && to.Length < from.Length
            && await TryAppendAsync(source, target, cancellationToken).ConfigureAwait(false))
        {
            File.SetLastWriteTimeUtc(target, lastWrite);
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = TempPath(target);
        try
        {
            var input = OpenSource(source);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    if (wholeLines)
                    {
                        await CopyBytesAsync(input, output, await WholeLinesLengthAsync(input, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }
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

    private static FileStream OpenSource(string source) =>
        new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// Appends what <paramref name="source"/> has past the end of <paramref name="target"/>, when the target's start and
    /// end are the source's bytes at the same places. False, having written nothing, when they aren't, or the target
    /// can't be opened (on Windows, while something reads it without sharing writes): the whole file is copied then.
    /// </summary>
    private static async Task<bool> TryAppendAsync(string source, string target, CancellationToken cancellationToken)
    {
        try
        {
            var input = OpenSource(source);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 81920, FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    var length = output.Length;
                    if (length == 0 || length >= input.Length
                        || !await SameBytesAsync(input, output, 0, (int)Math.Min(length, AppendCheckLength), cancellationToken).ConfigureAwait(false)
                        || !await SameBytesAsync(input, output, Math.Max(0, length - AppendCheckLength), (int)Math.Min(length, AppendCheckLength), cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                    input.Position = length;
                    output.Position = length;
                    // Stopped part way (cancelled, or the disk full), the target is still the source's start, so the
                    // next copy appends the rest.
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> SameBytesAsync(FileStream a, FileStream b, long offset, int count, CancellationToken cancellationToken)
    {
        var left = new byte[count];
        var right = new byte[count];
        a.Position = offset;
        b.Position = offset;
        await a.ReadExactlyAsync(left, cancellationToken).ConfigureAwait(false);
        await b.ReadExactlyAsync(right, cancellationToken).ConfigureAwait(false);
        return left.AsSpan().SequenceEqual(right);
    }

    /// <summary>
    /// How much of <paramref name="stream"/> is whole lines: up to and including its last <c>\n</c>. All of it when it
    /// has none, as a file of one line, still being written, is better than an empty one.
    /// </summary>
    private static async Task<long> WholeLinesLengthAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var length = stream.Length;
        var buffer = new byte[(int)Math.Min(length, AppendCheckLength)];
        for (var end = length; end > 0;)
        {
            var count = (int)Math.Min(end, buffer.Length);
            stream.Position = end - count;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            var newline = buffer.AsSpan(0, count).LastIndexOf((byte)'\n');
            if (newline >= 0)
            {
                stream.Position = 0;
                return end - count + newline + 1;
            }
            end -= count;
        }
        stream.Position = 0;
        return length;
    }

    private static async Task CopyBytesAsync(FileStream input, FileStream output, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (count > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(count, buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            count -= read;
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
