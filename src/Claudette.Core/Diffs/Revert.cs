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
    /// <paramref name="text"/> deletes the file: it didn't exist before Claude made it. The file keeps its encoding and
    /// byte order mark, its permissions, and a symbolic link stays one (<see cref="Files.AtomicFile.ReplaceContents"/>).
    /// </summary>
    /// <exception cref="IOException">The file isn't text in an encoding that can be written back as it was.</exception>
    public static bool Write(string path, string? expectedCurrent, string? text)
    {
        byte[]? bytes;
        try
        {
            bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (FileNotFoundException)
        {
            bytes = null;
        }
        var encoding = (Files.TextFileEncoding?)null;
        string? now = null;
        if (bytes is not null && !Files.TextFileEncoding.TryRead(bytes, out encoding, out now))
        {
            throw new IOException("It isn't UTF-8 or UTF-16 text, so it can't be written back as it was.");
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
        // A file Claude deleted comes back as UTF-8 without a mark, unless the text had one.
        var bytesToWrite = encoding is not null ? encoding.GetBytes(text.TrimStart('\uFEFF')) : new System.Text.UTF8Encoding(false).GetBytes(text);
        Files.AtomicFile.ReplaceContents(path, bytesToWrite);
        return true;
    }
}
