namespace Claudette.Core.Diffs;

/// <summary>
/// Puts back what Claude changed in a file (DESIGN.md §8, "Reverting"): one hunk of the diff, or the whole file as it
/// was before Claude's first change. The file must still be as the diff showed it: a change made since makes the
/// revert refuse rather than guess.
/// </summary>
public static class Revert
{
    /// <summary>
    /// <paramref name="current"/> with <paramref name="hunk"/> undone: its added lines taken out and its removed lines put
    /// back. Null when the file no longer has the hunk's lines where the hunk says. The file's line endings, and whether
    /// it ends with one, are kept.
    /// </summary>
    public static string? Hunk(string? current, DiffHunk hunk)
    {
        var lines = LineDiff.SplitLines(current).ToList();
        var newLines = hunk.Lines.Where(l => l.Op != DiffOp.Removed).Select(l => l.Text).ToArray();
        var oldLines = hunk.Lines.Where(l => l.Op != DiffOp.Added).Select(l => l.Text).ToArray();
        // As in diff -u, a side with no lines starts at the line before the hunk.
        var start = hunk.NewCount == 0 ? hunk.NewStart : hunk.NewStart - 1;
        if (start < 0 || start + newLines.Length > lines.Count || !lines.Skip(start).Take(newLines.Length).SequenceEqual(newLines, StringComparer.Ordinal))
        {
            return null;
        }
        lines.RemoveRange(start, newLines.Length);
        lines.InsertRange(start, oldLines);
        var newline = current is not null && current.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var endsWithNewline = current is { Length: > 0 } ? current[^1] is '\n' or '\r' : lines.Count > 0;
        return lines.Count == 0 ? "" : string.Join(newline, lines) + (endsWithNewline ? newline : "");
    }

    /// <summary>
    /// <paramref name="text"/> with <paramref name="like"/>'s line endings, when <paramref name="like"/> has Windows ones
    /// and <paramref name="text"/> only Unix ones (as git hands back a file it converts on checkout); otherwise as it is.
    /// </summary>
    public static string? WithLineEndingsOf(string? text, string? like) =>
        text is null || like is null || !like.Contains("\r\n", StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal)
            ? text
            : text.ReplaceLineEndings("\r\n");

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> only if the file still holds
    /// <paramref name="expectedCurrent"/> (null: missing). False when it changed meanwhile. A null
    /// <paramref name="text"/> deletes the file: it didn't exist before Claude made it.
    /// </summary>
    public static bool Write(string path, string? expectedCurrent, string? text)
    {
        string? now;
        try
        {
            now = File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (FileNotFoundException)
        {
            now = null;
        }
        if (!string.Equals(now, expectedCurrent, StringComparison.Ordinal))
        {
            return false;
        }
        if (text is null)
        {
            File.Delete(path);
            return true;
        }
        // Reading drops a UTF-8 byte order mark; put it back if the file had one.
        var hadMark = false;
        if (now is not null)
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[3];
            hadMark = stream.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        Files.AtomicFile.WriteAllText(path, hadMark && !text.StartsWith('\uFEFF') ? '\uFEFF' + text : text);
        return true;
    }
}
