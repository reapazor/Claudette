using System.Diagnostics;
using Claudette.Core.Diffs;

namespace Claudette.Core.Tests.Diffs;

public class LineDiffTests
{
    [Fact]
    public void Identical_texts_have_no_hunks()
    {
        Assert.Empty(LineDiff.Hunks("a\nb\n", "a\nb\n"));
        Assert.Equal((0, 0), LineDiff.Count("a\nb\n", "a\nb\n"));
        Assert.All(LineDiff.Full("a\nb\n", "a\nb\n"), l => Assert.Equal(DiffOp.Context, l.Op));
    }

    [Fact]
    public void A_byte_order_mark_isnt_a_change()
    {
        Assert.Empty(LineDiff.Hunks("﻿namespace Demo;\nclass A {}\n", "namespace Demo;\nclass A {}\n"));
        Assert.Equal((1, 1), LineDiff.Count("﻿one\ntwo\n", "one\nTWO\n"));
    }

    [Fact]
    public void An_insertion()
    {
        var hunk = Assert.Single(LineDiff.Hunks("a\nb\nc\n", "a\nb\nX\nc\n"));

        Assert.Equal("@@ -1,3 +1,4 @@", hunk.Header);
        Assert.Equal(
            [
                new DiffLineEntry(DiffOp.Context, "a", 1, 1),
                new DiffLineEntry(DiffOp.Context, "b", 2, 2),
                new DiffLineEntry(DiffOp.Added, "X", null, 3),
                new DiffLineEntry(DiffOp.Context, "c", 3, 4),
            ],
            hunk.Lines);
        Assert.Equal((1, 0), LineDiff.Count("a\nb\nc\n", "a\nb\nX\nc\n"));
    }

    [Fact]
    public void A_deletion()
    {
        var hunk = Assert.Single(LineDiff.Hunks("a\nb\nc\n", "a\nc\n"));

        Assert.Equal("@@ -1,3 +1,2 @@", hunk.Header);
        Assert.Equal(new DiffLineEntry(DiffOp.Removed, "b", 2, null), hunk.Lines[1]);
        Assert.Equal(new DiffLineEntry(DiffOp.Context, "c", 3, 2), hunk.Lines[2]);
    }

    [Fact]
    public void A_change_shows_the_removed_line_before_the_added_one()
    {
        var lines = LineDiff.Full("a\nb\nc\n", "a\nB\nc\n");

        Assert.Equal(
            [
                new DiffLineEntry(DiffOp.Context, "a", 1, 1),
                new DiffLineEntry(DiffOp.Removed, "b", 2, null),
                new DiffLineEntry(DiffOp.Added, "B", null, 2),
                new DiffLineEntry(DiffOp.Context, "c", 3, 3),
            ],
            lines);
        Assert.Equal((1, 1), LineDiff.Count("a\nb\nc\n", "a\nB\nc\n"));
    }

    [Fact]
    public void A_new_file_is_all_added()
    {
        var hunk = Assert.Single(LineDiff.Hunks(null, "x\ny\n"));

        Assert.Equal("@@ -0,0 +1,2 @@", hunk.Header);
        Assert.All(hunk.Lines, l => Assert.Equal(DiffOp.Added, l.Op));
        Assert.Equal((2, 0), LineDiff.Count(null, "x\ny\n"));
        Assert.Equal((2, 0), LineDiff.Count("", "x\ny"));
    }

    [Fact]
    public void An_emptied_file_is_all_removed()
    {
        var hunk = Assert.Single(LineDiff.Hunks("x\ny\n", null));

        Assert.Equal("@@ -1,2 +0,0 @@", hunk.Header);
        Assert.Equal((0, 2), LineDiff.Count("x\ny\n", ""));
    }

    [Fact]
    public void Two_empty_sides_have_no_lines()
    {
        Assert.Empty(LineDiff.Full(null, null));
        Assert.Empty(LineDiff.Hunks("", null));
        Assert.Equal((0, 0), LineDiff.Count(null, ""));
    }

    [Fact]
    public void A_missing_newline_at_the_end_is_not_a_change()
    {
        Assert.Empty(LineDiff.Hunks("a\nb", "a\nb\n"));
        Assert.Equal((0, 0), LineDiff.Count("a\nb\n", "a\nb"));
        Assert.True(LineDiff.NewlineAtEndDiffers("a\nb", "a\nb\n"));
        Assert.False(LineDiff.NewlineAtEndDiffers("a\nb\n", "c\r\n"));
        Assert.False(LineDiff.NewlineAtEndDiffers(null, "c"));
    }

    [Fact]
    public void Line_endings_are_normalized()
    {
        Assert.Empty(LineDiff.Hunks("a\r\nb\r\n", "a\nb\n"));
        Assert.Empty(LineDiff.Hunks("a\rb\r", "a\nb"));
        Assert.Equal(["a", "", "b"], LineDiff.SplitLines("a\r\n\rb\n"));
        Assert.Equal(3, LineDiff.CountLines("a\r\n\rb\n"));
        Assert.Equal(1, LineDiff.CountLines("\n"));
        Assert.Equal(0, LineDiff.CountLines(""));

        var change = Assert.Single(LineDiff.Hunks("a\r\nb\r\n", "a\nc\n"));
        Assert.Equal(["a", "b", "c"], change.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Hunks_have_three_lines_of_context_by_default()
    {
        var before = Lines(20);
        var after = before.Replace("line 10\n", "line ten\n", StringComparison.Ordinal);

        var hunk = Assert.Single(LineDiff.Hunks(before, after));

        Assert.Equal("@@ -7,7 +7,7 @@", hunk.Header);
        Assert.Equal("line 7", hunk.Lines[0].Text);
        Assert.Equal("line 13", hunk.Lines[^1].Text);
    }

    [Fact]
    public void Nearby_changes_share_a_hunk_and_distant_ones_do_not()
    {
        var before = Lines(40);
        var close = before.Replace("line 10\n", "x\n", StringComparison.Ordinal).Replace("line 17\n", "y\n", StringComparison.Ordinal);
        var far = before.Replace("line 10\n", "x\n", StringComparison.Ordinal).Replace("line 18\n", "y\n", StringComparison.Ordinal);

        Assert.Single(LineDiff.Hunks(before, close));
        var hunks = LineDiff.Hunks(before, far);
        Assert.Equal(2, hunks.Count);
        Assert.Equal(["@@ -7,7 +7,7 @@", "@@ -15,7 +15,7 @@"], hunks.Select(h => h.Header));
    }

    [Fact]
    public void Context_can_be_zero()
    {
        var hunk = Assert.Single(LineDiff.Hunks("a\nb\nc\n", "a\nc\n", context: 0));

        Assert.Equal("@@ -2,1 +1,0 @@", hunk.Header);
        Assert.Equal([DiffOp.Removed], hunk.Lines.Select(l => l.Op));
    }

    [Fact]
    public void Side_by_side_pairs_removed_and_added_runs()
    {
        var lines = LineDiff.Full("same\nr1\nr2\nr3\nmid\nend\n", "same\na1\nmid\nend\nn1\nn2\n");

        var rows = LineDiff.SideBySide(lines);

        Assert.Equal(
            [
                ("same", "same"),
                ("r1", "a1"),
                ("r2", null),
                ("r3", null),
                ("mid", "mid"),
                ("end", "end"),
                (null, "n1"),
                (null, "n2"),
            ],
            rows.Select(r => (r.Left?.Text, r.Right?.Text)));
        Assert.Same(rows[0].Left, rows[0].Right);
    }

    [Fact]
    public void Binary_text_contains_a_nul()
    {
        Assert.True(LineDiff.LooksBinary("PNG\0\u0001"));
        Assert.False(LineDiff.LooksBinary("plain text\n"));
    }

    [Fact]
    public void Random_diffs_are_valid_and_shortest()
    {
        var random = new Random(1234);
        for (var run = 0; run < 1500; run++)
        {
            var alphabet = random.Next(1, 5);
            var a = RandomLines(random, random.Next(0, 40), alphabet);
            var b = random.Next(3) == 0 ? Mutate(random, a, alphabet) : RandomLines(random, random.Next(0, 40), alphabet);
            var before = string.Join("\n", a);
            var after = string.Join("\n", b);

            var lines = LineDiff.Full(before, after);

            Assert.Equal(a, lines.Where(l => l.Op != DiffOp.Added).Select(l => l.Text));
            Assert.Equal(b, lines.Where(l => l.Op != DiffOp.Removed).Select(l => l.Text));
            Assert.Equal(Enumerable.Range(1, a.Length).Select(n => (int?)n), lines.Where(l => l.Op != DiffOp.Added).Select(l => l.OldNumber));
            Assert.Equal(Enumerable.Range(1, b.Length).Select(n => (int?)n), lines.Where(l => l.Op != DiffOp.Removed).Select(l => l.NewNumber));
            var changes = lines.Count(l => l.Op != DiffOp.Context);
            Assert.Equal(a.Length + b.Length - 2 * Lcs(a, b), changes);
        }
    }

    [Fact]
    public void Large_files_with_a_few_changes_diff_quickly()
    {
        var before = Lines(20_000);
        var after = before
            .Replace("line 100\n", "changed 100\n", StringComparison.Ordinal)
            .Replace("line 5000\n", "", StringComparison.Ordinal)
            .Replace("line 9000\n", "line 9000\nextra\n", StringComparison.Ordinal)
            .Replace("line 15000\n", "changed 15000\n", StringComparison.Ordinal)
            .Replace("line 19990\n", "changed 19990\n", StringComparison.Ordinal);
        LineDiff.Count("warm\nup\n", "warm\nup\nnow\n");

        var best = TimeSpan.MaxValue;
        IReadOnlyList<DiffHunk> hunks = [];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            hunks = LineDiff.Hunks(before, after);
            stopwatch.Stop();
            best = stopwatch.Elapsed < best ? stopwatch.Elapsed : best;
        }

        Assert.True(best < TimeSpan.FromMilliseconds(100), $"Took {best.TotalMilliseconds:0} ms.");
        Assert.Equal(5, hunks.Count);
        Assert.Equal((4, 4), LineDiff.Count(before, after));
    }

    [Fact]
    public void A_huge_edit_distance_falls_back_to_all_removed_then_all_added()
    {
        // No shared prefix or suffix, and every line occurs on both sides, so only the search can find the 6,000 kept
        // lines, and the shortest edit (12,000) is over the limit.
        var before = string.Concat(Enumerable.Repeat("x\n", 6_000)) + string.Concat(Enumerable.Repeat("y\n", 6_000));
        var after = string.Concat(Enumerable.Repeat("y\n", 6_000)) + string.Concat(Enumerable.Repeat("x\n", 6_000));

        var stopwatch = Stopwatch.StartNew();
        var lines = LineDiff.Full(before, after);
        stopwatch.Stop();

        Assert.Equal(24_000, lines.Count);
        Assert.All(lines.Take(12_000), l => Assert.Equal(DiffOp.Removed, l.Op));
        Assert.All(lines.Skip(12_000), l => Assert.Equal(DiffOp.Added, l.Op));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Took {stopwatch.Elapsed.TotalMilliseconds:0} ms.");
    }

    [Fact]
    public void Lines_found_on_only_one_side_do_not_need_the_search()
    {
        var before = string.Join("\n", Enumerable.Range(0, 30_000).Select(i => $"old {i}"));
        var after = string.Join("\n", Enumerable.Range(0, 30_000).Select(i => $"new {i}"));

        Assert.Equal((30_000, 30_000), LineDiff.Count(before, after));
    }

    private static string Lines(int count) => string.Concat(Enumerable.Range(1, count).Select(i => $"line {i}\n"));

    private static string[] RandomLines(Random random, int count, int alphabet) =>
        Enumerable.Range(0, count).Select(_ => ((char)('a' + random.Next(alphabet))).ToString()).ToArray();

    private static string[] Mutate(Random random, string[] lines, int alphabet)
    {
        var result = lines.ToList();
        for (var edits = random.Next(1, 5); edits > 0; edits--)
        {
            var at = random.Next(result.Count + 1);
            if (random.Next(2) == 0 && at < result.Count)
            {
                result.RemoveAt(at);
            }
            else
            {
                result.Insert(at, ((char)('a' + random.Next(alphabet + 1))).ToString());
            }
        }
        return [.. result];
    }

    private static int Lcs(string[] a, string[] b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                table[i, j] = a[i - 1] == b[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }
        return table[a.Length, b.Length];
    }
}
