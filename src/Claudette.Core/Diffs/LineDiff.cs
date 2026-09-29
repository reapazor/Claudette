namespace Claudette.Core.Diffs;

/// <summary>What happened to one line in a diff.</summary>
public enum DiffOp
{
    Context,
    Added,
    Removed,
}

/// <summary>
/// One line of a diff. <paramref name="OldNumber"/> and <paramref name="NewNumber"/> are 1-based, and null on the side
/// the line isn't on.
/// </summary>
public sealed record DiffLineEntry(DiffOp Op, string Text, int? OldNumber, int? NewNumber);

/// <summary>
/// A unified-diff hunk. As in <c>diff -u</c>, a side with no lines has its start set to the line before the hunk, so a
/// new file's only hunk is <c>@@ -0,0 +1,3 @@</c>.
/// </summary>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLineEntry> Lines)
{
    public string Header => $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@";
}

/// <summary>One row of a side-by-side diff. A missing side is an empty cell.</summary>
public sealed record SideBySideRow(DiffLineEntry? Left, DiffLineEntry? Right);

/// <summary>
/// A line diff for the diff view (DESIGN.md §8): Myers' O(ND) algorithm in linear space.
/// </summary>
/// <remarks>
/// <para>
/// <c>\r\n</c> and <c>\r</c> count as <c>\n</c>, so line-ending differences don't show as changes. A missing newline at
/// the end is ignored too: <c>"a\nb"</c> and <c>"a\nb\n"</c> have the same lines. <see cref="NewlineAtEndDiffers"/>
/// tells the view when to note it. A null text is an empty (or missing) file.
/// </para>
/// <para>
/// When the two texts differ by more than <see cref="MaxEditDistance"/> lines, the differing middle is shown as
/// removed then added instead of searching for the shortest edit, so a huge rewrite can't hang the view.
/// </para>
/// </remarks>
public static class LineDiff
{
    /// <summary>The largest edit distance (lines removed plus lines added) the diff searches for exactly.</summary>
    public const int MaxEditDistance = 10_000;

    /// <summary>The changed lines with <paramref name="context"/> unchanged lines around each change.</summary>
    public static IReadOnlyList<DiffHunk> Hunks(string? before, string? after, int context = 3)
    {
        context = Math.Max(0, context);
        var entries = BuildEntries(Compute(before, after));

        var hunks = new List<DiffHunk>();
        int oldSeen = 0, newSeen = 0, scanned = 0;
        var index = 0;
        while (index < entries.Count)
        {
            var firstChange = NextChange(entries, index);
            if (firstChange < 0)
            {
                break;
            }
            var lastChange = firstChange;
            for (var next = NextChange(entries, lastChange + 1); next >= 0 && next - lastChange - 1 <= 2 * context; next = NextChange(entries, lastChange + 1))
            {
                lastChange = next;
            }

            var start = Math.Max(index, firstChange - context);
            var end = Math.Min(entries.Count, lastChange + 1 + context);
            for (; scanned < start; scanned++)
            {
                if (entries[scanned].OldNumber is not null)
                {
                    oldSeen++;
                }
                if (entries[scanned].NewNumber is not null)
                {
                    newSeen++;
                }
            }

            var lines = entries.GetRange(start, end - start);
            var oldCount = lines.Count(l => l.OldNumber is not null);
            var newCount = lines.Count(l => l.NewNumber is not null);
            hunks.Add(new DiffHunk(
                oldCount > 0 ? oldSeen + 1 : oldSeen, oldCount,
                newCount > 0 ? newSeen + 1 : newSeen, newCount,
                lines));
            index = end;
        }
        return hunks;
    }

    /// <summary>Every line of both texts, for a whole-file view.</summary>
    public static IReadOnlyList<DiffLineEntry> Full(string? before, string? after) => BuildEntries(Compute(before, after));

    /// <summary>The number of lines added and removed.</summary>
    public static (int Added, int Removed) Count(string? before, string? after)
    {
        if (string.IsNullOrEmpty(before))
        {
            return (CountLines(after), 0);
        }
        if (string.IsNullOrEmpty(after))
        {
            return (0, CountLines(before));
        }
        var script = Compute(before, after);
        return (script.Added.Count(a => a), script.Removed.Count(r => r));
    }

    /// <summary>
    /// Lays diff lines out in two columns. Context lines appear on both sides. A run of removed lines followed by added
    /// lines is paired row by row, and the leftovers get an empty cell on the other side.
    /// </summary>
    public static IReadOnlyList<SideBySideRow> SideBySide(IReadOnlyList<DiffLineEntry> lines)
    {
        var rows = new List<SideBySideRow>(lines.Count);
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Op == DiffOp.Context)
            {
                rows.Add(new SideBySideRow(lines[i], lines[i]));
                i++;
                continue;
            }
            var removedStart = i;
            while (i < lines.Count && lines[i].Op == DiffOp.Removed)
            {
                i++;
            }
            var addedStart = i;
            while (i < lines.Count && lines[i].Op == DiffOp.Added)
            {
                i++;
            }
            int removed = addedStart - removedStart, added = i - addedStart;
            for (var row = 0; row < Math.Max(removed, added); row++)
            {
                rows.Add(new SideBySideRow(
                    row < removed ? lines[removedStart + row] : null,
                    row < added ? lines[addedStart + row] : null));
            }
        }
        return rows;
    }

    /// <summary>Whether the text looks binary: it contains a NUL character.</summary>
    public static bool LooksBinary(string text) => text.Contains('\0');

    /// <summary>
    /// Whether exactly one of two non-empty texts ends with a newline. The diff ignores this, so the view can show a
    /// "No newline at end of file" note instead.
    /// </summary>
    public static bool NewlineAtEndDiffers(string? before, string? after) =>
        !string.IsNullOrEmpty(before) && !string.IsNullOrEmpty(after) && EndsWithNewline(before) != EndsWithNewline(after);

    /// <summary>Splits text into lines, treating <c>\r\n</c>, <c>\r</c> and <c>\n</c> alike. A final newline doesn't start another line.</summary>
    public static IReadOnlyList<string> SplitLines(string? text) => Split(text);

    /// <summary>The number of lines <see cref="SplitLines"/> would return, without allocating them.</summary>
    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        var span = text.AsSpan();
        var count = 0;
        while (true)
        {
            var at = span.IndexOfAny('\r', '\n');
            if (at < 0)
            {
                return span.IsEmpty ? count : count + 1;
            }
            count++;
            var length = span[at] == '\r' && at + 1 < span.Length && span[at + 1] == '\n' ? 2 : 1;
            span = span[(at + length)..];
        }
    }

    private static bool EndsWithNewline(string text) => text[^1] is '\n' or '\r';

    private static int NextChange(List<DiffLineEntry> entries, int from)
    {
        for (var i = from; i < entries.Count; i++)
        {
            if (entries[i].Op != DiffOp.Context)
            {
                return i;
            }
        }
        return -1;
    }

    private static string[] Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        var lines = new List<string>();
        // A byte order mark isn't content: Claude Code's originalFile can keep one that a plain read drops.
        var position = text[0] == '﻿' ? 1 : 0;
        while (position < text.Length)
        {
            var at = text.AsSpan(position).IndexOfAny('\r', '\n');
            if (at < 0)
            {
                lines.Add(text[position..]);
                break;
            }
            lines.Add(text.Substring(position, at));
            position += at;
            position += text[position] == '\r' && position + 1 < text.Length && text[position + 1] == '\n' ? 2 : 1;
        }
        return [.. lines];
    }

    private static List<DiffLineEntry> BuildEntries(EditScript script)
    {
        var (oldLines, newLines, removed, added) = script;
        var entries = new List<DiffLineEntry>(Math.Max(oldLines.Length, newLines.Length));
        int i = 0, j = 0;
        while (i < oldLines.Length || j < newLines.Length)
        {
            if (i < oldLines.Length && removed[i])
            {
                entries.Add(new DiffLineEntry(DiffOp.Removed, oldLines[i], i + 1, null));
                i++;
            }
            else if (j < newLines.Length && added[j])
            {
                entries.Add(new DiffLineEntry(DiffOp.Added, newLines[j], null, j + 1));
                j++;
            }
            else if (i < oldLines.Length && j < newLines.Length)
            {
                entries.Add(new DiffLineEntry(DiffOp.Context, newLines[j], i + 1, j + 1));
                i++;
                j++;
            }
            else
            {
                break; // Unreachable for a valid script.
            }
        }
        return entries;
    }

    private readonly record struct EditScript(string[] Old, string[] New, bool[] Removed, bool[] Added);

    private static EditScript Compute(string? before, string? after)
    {
        var oldLines = Split(before);
        var newLines = Split(after);
        var removed = new bool[oldLines.Length];
        var added = new bool[newLines.Length];

        // Common prefix and suffix are compared as strings, so the (usually small) middle is all that gets interned.
        var start = 0;
        while (start < oldLines.Length && start < newLines.Length && string.Equals(oldLines[start], newLines[start], StringComparison.Ordinal))
        {
            start++;
        }
        int oldEnd = oldLines.Length, newEnd = newLines.Length;
        while (oldEnd > start && newEnd > start && string.Equals(oldLines[oldEnd - 1], newLines[newEnd - 1], StringComparison.Ordinal))
        {
            oldEnd--;
            newEnd--;
        }
        if (start < oldEnd || start < newEnd)
        {
            DiffMiddle(oldLines, newLines, start, oldEnd, newEnd, removed, added);
        }
        return new EditScript(oldLines, newLines, removed, added);
    }

    /// <summary>
    /// Diffs <c>oldLines[start..oldEnd)</c> against <c>newLines[start..newEnd)</c>. Lines that don't occur on the other
    /// side at all can't be part of any common subsequence, so they're marked straight away and left out of the search:
    /// the result is still a shortest edit, and a wholesale rewrite costs nothing to diff.
    /// </summary>
    private static void DiffMiddle(string[] oldLines, string[] newLines, int start, int oldEnd, int newEnd, bool[] removed, bool[] added)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var oldIds = Intern(oldLines, start, oldEnd, ids);
        var newIds = Intern(newLines, start, newEnd, ids);

        var inOld = new bool[ids.Count];
        var inNew = new bool[ids.Count];
        foreach (var id in oldIds)
        {
            inOld[id] = true;
        }
        foreach (var id in newIds)
        {
            inNew[id] = true;
        }

        var keptOld = Keep(oldIds, inNew, start, removed);
        var keptNew = Keep(newIds, inOld, start, added);
        if (keptOld.Count == 0 || keptNew.Count == 0)
        {
            MarkAll(keptOld, removed);
            MarkAll(keptNew, added);
            return;
        }

        var a = keptOld.Select(k => k.Id).ToArray();
        var b = keptNew.Select(k => k.Id).ToArray();
        var aRemoved = new bool[a.Length];
        var bAdded = new bool[b.Length];
        if (Math.Abs(a.Length - b.Length) > MaxEditDistance || !new Myers(a, b, aRemoved, bAdded, MaxEditDistance).Run())
        {
            MarkAll(keptOld, removed);
            MarkAll(keptNew, added);
            return;
        }
        for (var k = 0; k < a.Length; k++)
        {
            removed[keptOld[k].Index] = aRemoved[k];
        }
        for (var k = 0; k < b.Length; k++)
        {
            added[keptNew[k].Index] = bAdded[k];
        }
    }

    private static int[] Intern(string[] lines, int start, int end, Dictionary<string, int> ids)
    {
        var result = new int[end - start];
        for (var i = start; i < end; i++)
        {
            if (!ids.TryGetValue(lines[i], out var id))
            {
                id = ids.Count;
                ids.Add(lines[i], id);
            }
            result[i - start] = id;
        }
        return result;
    }

    /// <summary>The lines whose text also occurs on the other side; the rest are marked as changed.</summary>
    private static List<(int Id, int Index)> Keep(int[] ids, bool[] onOtherSide, int start, bool[] changed)
    {
        var kept = new List<(int Id, int Index)>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            if (onOtherSide[ids[i]])
            {
                kept.Add((ids[i], start + i));
            }
            else
            {
                changed[start + i] = true;
            }
        }
        return kept;
    }

    private static void MarkAll(List<(int Id, int Index)> lines, bool[] changed)
    {
        foreach (var (_, index) in lines)
        {
            changed[index] = true;
        }
    }

    /// <summary>
    /// Myers' linear-space refinement: find the middle snake of an optimal path, then recurse on each side of it
    /// ("An O(ND) Difference Algorithm and Its Variations", 1986, section 4b; the same shape as GNU diff's
    /// <c>compareseq</c>, without its heuristics). Diagonals are numbered <c>k = x - y</c> in whole-sequence coordinates,
    /// so one pair of arrays serves every sub-problem.
    /// </summary>
    private sealed class Myers
    {
        private readonly int[] _a;
        private readonly int[] _b;
        private readonly bool[] _removed;
        private readonly bool[] _added;
        private readonly int[] _forward;
        private readonly int[] _backward;
        private readonly int _offset;
        private readonly int _maxSteps;

        public Myers(int[] a, int[] b, bool[] removed, bool[] added, int maxEditDistance)
        {
            _a = a;
            _b = b;
            _removed = removed;
            _added = added;
            _forward = new int[a.Length + b.Length + 3];
            _backward = new int[a.Length + b.Length + 3];
            _offset = b.Length + 1;
            // Both searches advance one edit per step and meet after ceil(D / 2) steps.
            _maxSteps = (maxEditDistance + 1) / 2;
        }

        /// <summary>Marks the shortest edit; false when it's longer than the limit.</summary>
        public bool Run() => Compare(0, _a.Length, 0, _b.Length);

        private bool Compare(int xOff, int xLim, int yOff, int yLim)
        {
            while (xOff < xLim && yOff < yLim && _a[xOff] == _b[yOff])
            {
                xOff++;
                yOff++;
            }
            while (xLim > xOff && yLim > yOff && _a[xLim - 1] == _b[yLim - 1])
            {
                xLim--;
                yLim--;
            }

            if (xOff == xLim)
            {
                Array.Fill(_added, true, yOff, yLim - yOff);
                return true;
            }
            if (yOff == yLim)
            {
                Array.Fill(_removed, true, xOff, xLim - xOff);
                return true;
            }
            return MiddleSnake(xOff, xLim, yOff, yLim, out var xMid, out var yMid)
                && Compare(xOff, xMid, yOff, yMid)
                && Compare(xMid, xLim, yMid, yLim);
        }

        private bool MiddleSnake(int xOff, int xLim, int yOff, int yLim, out int xMid, out int yMid)
        {
            var fd = _forward;
            var bd = _backward;
            var o = _offset;
            int dMin = xOff - yLim, dMax = xLim - yOff;
            int fMid = xOff - yOff, bMid = xLim - yLim;
            int fMin = fMid, fMax = fMid, bMin = bMid, bMax = bMid;
            var odd = ((fMid - bMid) & 1) != 0;
            fd[o + fMid] = xOff;
            bd[o + bMid] = xLim;

            for (var step = 1; step <= _maxSteps; step++)
            {
                // Forward: extend each diagonal by one edit, then follow the snake.
                if (fMin > dMin)
                {
                    fd[o + --fMin - 1] = -1;
                }
                else
                {
                    fMin++;
                }
                if (fMax < dMax)
                {
                    fd[o + ++fMax + 1] = -1;
                }
                else
                {
                    fMax--;
                }
                for (var d = fMax; d >= fMin; d -= 2)
                {
                    int low = fd[o + d - 1], high = fd[o + d + 1];
                    var x = low >= high ? low + 1 : high;
                    var y = x - d;
                    while (x < xLim && y < yLim && _a[x] == _b[y])
                    {
                        x++;
                        y++;
                    }
                    fd[o + d] = x;
                    // The bounds check is a guard: the paths meet inside the box before an edge value could matter.
                    if (odd && bMin <= d && d <= bMax && bd[o + d] <= x && x <= xLim && y <= yLim)
                    {
                        xMid = x;
                        yMid = y;
                        return true;
                    }
                }

                // Backward, from the far corner.
                if (bMin > dMin)
                {
                    bd[o + --bMin - 1] = int.MaxValue;
                }
                else
                {
                    bMin++;
                }
                if (bMax < dMax)
                {
                    bd[o + ++bMax + 1] = int.MaxValue;
                }
                else
                {
                    bMax--;
                }
                for (var d = bMax; d >= bMin; d -= 2)
                {
                    int low = bd[o + d - 1], high = bd[o + d + 1];
                    var x = low < high ? low : high - 1;
                    var y = x - d;
                    while (x > xOff && y > yOff && _a[x - 1] == _b[y - 1])
                    {
                        x--;
                        y--;
                    }
                    bd[o + d] = x;
                    if (!odd && fMin <= d && d <= fMax && x <= fd[o + d] && x >= xOff && y >= yOff)
                    {
                        xMid = x;
                        yMid = y;
                        return true;
                    }
                }
            }
            xMid = yMid = 0;
            return false;
        }
    }
}
