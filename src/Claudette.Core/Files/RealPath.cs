namespace Claudette.Core.Files;

/// <summary>
/// Paths with the symbolic links on them followed, to tell whether two spellings name the same folder. Git names a
/// worktree by its real path, while a tab names it as it was opened: on macOS <c>/var</c> is a link to
/// <c>/private/var</c>, so a worktree under one is listed under the other. A junction on Windows is followed the same way.
/// </summary>
public static class RealPath
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> name the same folder, through any links on the way.</summary>
    public static bool Same(string a, string b) => PathComparer.Equals(Resolve(a), Resolve(b));

    /// <summary>
    /// <paramref name="path"/> with every link on it followed: each folder from the root down is replaced by what it
    /// links to, so a link anywhere on the way counts, not only at the end. A part that isn't there, or can't be read,
    /// stays as spelled, so a folder that's gone still has one real path.
    /// </summary>
    public static string Resolve(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if (Directory.ResolveLinkTarget(current, returnFinalTarget: true) is { } target)
                {
                    current = Path.TrimEndingDirectorySeparator(target.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not there, or not readable: the rest is matched as spelled.
            }
        }
        return current;
    }
}
