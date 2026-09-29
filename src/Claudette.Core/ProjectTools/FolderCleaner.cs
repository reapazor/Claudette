using System.Globalization;

namespace Claudette.Core.ProjectTools;

/// <summary>
/// Measures and deletes the folders a clean action removes (DESIGN.md §18, "Project tools"). It never follows links
/// or junctions out of a folder, and read-only files are made writable before they're deleted.
/// </summary>
public static class FolderCleaner
{
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>The total size of the files in <paramref name="folders"/>, in bytes. Folders that don't exist count as empty.</summary>
    public static long Measure(IEnumerable<string> folders, CancellationToken cancellationToken = default)
    {
        long total = 0;
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", Walk))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total += file.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Count what could be read.
            }
        }
        return total;
    }

    /// <summary>
    /// Deletes <paramref name="folder"/> and everything in it. A link inside it is removed, not what it points to.
    /// </summary>
    /// <exception cref="IOException">Something in it is in use, for example by a running editor.</exception>
    /// <exception cref="UnauthorizedAccessException">Something in it can't be deleted.</exception>
    public static void Delete(string folder, CancellationToken cancellationToken = default)
    {
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
        {
            return;
        }
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // A link: remove the link itself.
            root.Delete();
            return;
        }
        foreach (var entry in root.EnumerateFileSystemInfos("*", Walk))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        if (root.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            root.Attributes &= ~FileAttributes.ReadOnly;
        }
        root.Delete(recursive: true);
    }

    /// <summary>"3.2 GB", "140 MB", "12 KB".</summary>
    public static string FormatSize(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        return bytes switch
        {
            >= (long)gb => (bytes / gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
            >= (long)mb => Math.Round(bytes / mb).ToString("0", CultureInfo.InvariantCulture) + " MB",
            >= (long)kb => Math.Round(bytes / kb).ToString("0", CultureInfo.InvariantCulture) + " KB",
            _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
        };
    }
}
