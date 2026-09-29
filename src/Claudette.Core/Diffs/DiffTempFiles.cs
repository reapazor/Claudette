using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Claudette.Core.Diffs;

/// <summary>
/// The temporary "before" files handed to external diff tools (DESIGN.md §8). Each launch gets its own folder inside
/// the tab's temp directory, named from the time, so the file can keep the original name (<c>auth (before).cs</c>) and
/// the tool can still tell the language.
/// </summary>
public static partial class DiffTempFiles
{
    /// <summary>
    /// Writes <paramref name="content"/> (empty when null) to a new read-only file named like <c>auth (before).cs</c>,
    /// and returns its path.
    /// </summary>
    public static async Task<string> WriteBeforeAsync(string tempDirectory, string fileName, string? content, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var folder = CreateLaunchFolder(tempDirectory, now);
        return await WriteAsync(folder, BeforeFileName(fileName), content, readOnly: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>auth.cs</c> becomes <c>auth (before).cs</c>, keeping the extension last.</summary>
    public static string BeforeFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Length == 0 ? $"{fileName} (before)" : $"{stem} (before){Path.GetExtension(fileName)}";
    }

    /// <summary>
    /// Deletes the files written into <paramref name="tempDirectory"/>, clearing their read-only attribute first, and
    /// then the directory if nothing else is left in it. Called when a tab closes. Only this class's launch folders are
    /// touched. Returns false when some couldn't be deleted, for example because a tool still has one open.
    /// </summary>
    public static bool Cleanup(string tempDirectory)
    {
        var clean = true;
        try
        {
            if (!Directory.Exists(tempDirectory))
            {
                return true;
            }
            foreach (var folder in Directory.EnumerateDirectories(tempDirectory))
            {
                if (!LaunchFolderName().IsMatch(Path.GetFileName(folder)))
                {
                    continue;
                }
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    clean = false;
                }
            }
            if (!Directory.EnumerateFileSystemEntries(tempDirectory).Any())
            {
                Directory.Delete(tempDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            clean = false;
        }
        return clean;
    }

    internal static string CreateLaunchFolder(string tempDirectory, DateTimeOffset now)
    {
        Directory.CreateDirectory(tempDirectory);
        var stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        for (var attempt = 1; ; attempt++)
        {
            var folder = Path.Combine(tempDirectory, attempt == 1 ? stamp : $"{stamp}-{attempt}");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                return folder;
            }
        }
    }

    internal static async Task<string> WriteAsync(string folder, string fileName, string? content, bool readOnly, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, fileName);
        await File.WriteAllTextAsync(path, content ?? "", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        if (readOnly)
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
        return path;
    }

    [GeneratedRegex(@"^\d{8}-\d{6}-\d{3}(-\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LaunchFolderName();
}
