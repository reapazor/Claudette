namespace Claudette.Core.Diffs;

/// <summary>
/// A file the user marked as reviewed in the Changed files panel (DESIGN.md §8, "Reviewed"). Saved with the tab, and in
/// the session record of a tab that syncs.
/// </summary>
public sealed class ReviewedFile
{
    /// <summary>The file's full path. In a session record, relative to the record's folder, with forward slashes.</summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The id of Claude's latest Edit or Write call to the file when it was marked, or null when Claude hadn't changed it
    /// in the session. The file stays reviewed until Claude changes it again.
    /// </summary>
    public string? Change { get; set; }
}

/// <summary>
/// The files a tab's user marked as reviewed (DESIGN.md §8, "Reviewed"). A file stays reviewed until Claude changes it
/// again: its mark remembers Claude's latest change to it by tool call id, which is the same live and in a replayed
/// transcript, on this machine or another. Changes made by commands or by the user don't count.
/// </summary>
/// <remarks>Used from the UI thread only. The marks live in the list it's given: the tab's saved state.</remarks>
public sealed class ReviewedFiles
{
    private readonly List<ReviewedFile> _marks;
    private readonly StringComparer _pathComparer;

    /// <param name="marks">The tab's saved marks, kept up to date in place.</param>
    /// <param name="pathComparer">
    /// How paths are matched. Defaults to <see cref="ChangedFiles.PlatformPathComparer"/>; tests pass one to check another
    /// OS's rule.
    /// </param>
    public ReviewedFiles(List<ReviewedFile> marks, StringComparer? pathComparer = null)
    {
        _marks = marks;
        _pathComparer = pathComparer ?? ChangedFiles.PlatformPathComparer;
    }

    /// <summary>The id of Claude's latest change to a file, or null for a file Claude hasn't changed in the session.</summary>
    public static string? LatestChange(ChangedFile? file) => file?.ToolUseIds[^1];

    /// <summary>The file was marked, and Claude hasn't changed it since.</summary>
    /// <param name="claudeChanges">Claude's changes to the file in the session, or null when it hasn't changed it.</param>
    public bool IsReviewed(string path, ChangedFile? claudeChanges) =>
        Find(path) is { } mark && mark.Change == LatestChange(claudeChanges);

    /// <summary>Marks a file as reviewed as of Claude's change <paramref name="change"/> to it (null: none yet).</summary>
    public void Mark(string path, string? change)
    {
        if (Find(path) is { } mark)
        {
            mark.Change = change;
            return;
        }
        _marks.Add(new ReviewedFile { Path = ChangedFiles.Normalize(path), Change = change });
    }

    /// <returns>Whether the file had been marked.</returns>
    public bool Unmark(string path)
    {
        var key = ChangedFiles.Normalize(path);
        return _marks.RemoveAll(m => _pathComparer.Equals(m.Path, key)) > 0;
    }

    /// <summary>
    /// Forgets the marks on files Claude has changed since. A mark made before Claude changed the file at all is one of
    /// them. A mark whose change isn't in the session yet is kept, since its transcript may still be replaying; tool call
    /// ids never repeat, so it can't count as reviewed again by mistake.
    /// </summary>
    /// <returns>Whether any mark was forgotten.</returns>
    public bool Prune(ChangedFiles changes) => _marks.RemoveAll(mark =>
        changes.Find(mark.Path) is { } file
        && mark.Change != LatestChange(file)
        && (mark.Change is null || file.ToolUseIds.Contains(mark.Change))) > 0;

    /// <summary>
    /// The marks as a session record keeps them: relative to <paramref name="folder"/> with forward slashes, since the
    /// folder's path differs between machines (DESIGN.md §9). A file with no relative path from there, such as one on
    /// another drive, is left out.
    /// </summary>
    public static List<ReviewedFile> ToRecord(IEnumerable<ReviewedFile> marks, string folder)
    {
        var result = new List<ReviewedFile>();
        foreach (var mark in marks)
        {
            string relative;
            try
            {
                relative = Path.GetRelativePath(folder, mark.Path);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (!Path.IsPathRooted(relative))
            {
                result.Add(new ReviewedFile { Path = relative.Replace('\\', '/'), Change = mark.Change });
            }
        }
        return result;
    }

    /// <summary>A session record's marks on this machine, where the session's folder is <paramref name="folder"/>.</summary>
    public static List<ReviewedFile> FromRecord(IEnumerable<ReviewedFile>? marks, string folder)
    {
        var result = new List<ReviewedFile>();
        foreach (var mark in marks ?? [])
        {
            if (string.IsNullOrEmpty(mark.Path) || Path.IsPathRooted(mark.Path))
            {
                continue;
            }
            try
            {
                var path = Path.GetFullPath(Path.Combine(folder, mark.Path.Replace('/', Path.DirectorySeparatorChar)));
                result.Add(new ReviewedFile { Path = path, Change = mark.Change });
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a path on this machine.
            }
        }
        return result;
    }

    private ReviewedFile? Find(string path)
    {
        var key = ChangedFiles.Normalize(path);
        return _marks.Find(m => _pathComparer.Equals(m.Path, key));
    }
}
