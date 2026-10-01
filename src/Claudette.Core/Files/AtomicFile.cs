using System.Text;

namespace Claudette.Core.Files;

/// <summary>
/// Writes a file so that it's never seen half-written, even after a crash or a power cut: the text goes to a temporary
/// file next to it under a unique name, is flushed to the disk, and is then renamed over the target, trying again while
/// something has the target open (<see cref="FileRetry"/>).
/// </summary>
public static class AtomicFile
{
    /// <summary>The extension of in-progress files.</summary>
    public const string TempExtension = ".tmp";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A unique temporary name next to <paramref name="target"/>, so the rename stays on one volume.</summary>
    public static string TempPath(string target) => $"{target}.{Guid.NewGuid():N}{TempExtension}";

    public static bool IsTemp(string path) => path.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase);

    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = TempPath(path);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8.GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            Replace(temp, path);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    public static async Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = TempPath(path);
        try
        {
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(Utf8.GetBytes(text), cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            Replace(temp, path);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>
    /// Replaces the contents of a file of the user's, as <see cref="WriteAllText"/> does, without changing what it is: a
    /// symbolic link is written through, to the file it points to, rather than replaced by a file, and on macOS and
    /// Linux the file keeps its permissions, such as being executable.
    /// </summary>
    public static void ReplaceContents(string path, ReadOnlySpan<byte> bytes)
    {
        var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        var temp = TempPath(target);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && File.Exists(target))
            {
                File.SetUnixFileMode(temp, File.GetUnixFileMode(target));
            }
            Replace(temp, target);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>Reads a file, trying again while something has it open. A missing file throws as usual.</summary>
    public static string ReadAllText(string path) => FileRetry.Run(() => File.ReadAllText(path));

    /// <summary>Renames <paramref name="temp"/> over <paramref name="target"/>, trying again while something has the target open.</summary>
    public static void Replace(string temp, string target) => FileRetry.Run(() => File.Move(temp, target, overwrite: true));

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temporary file is ignored, and readers skip the extension.
        }
    }
}
