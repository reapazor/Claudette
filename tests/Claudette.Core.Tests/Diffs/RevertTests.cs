using Claudette.Core.Diffs;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.Diffs;

/// <summary>Putting back what Claude changed (DESIGN.md §8, "Reverting").</summary>
public class RevertTests
{
    private const string Before = "one\ntwo\nthree\nfour\nfive\nsix\nseven\neight\nnine\nten\neleven\ntwelve\n";

    [Fact]
    public void Undoing_one_hunk_leaves_the_others()
    {
        var current = Before.Replace("two\n", "TWO\n", StringComparison.Ordinal).Replace("eleven\n", "eleven\nadded\n", StringComparison.Ordinal);
        var hunks = LineDiff.Hunks(Before, current);
        Assert.Equal(2, hunks.Count);

        var reverted = Revert.Hunk(current, hunks[0]);

        Assert.Equal(Before.Replace("eleven\n", "eleven\nadded\n", StringComparison.Ordinal), reverted);
        Assert.Equal(Before, Revert.Hunk(reverted, LineDiff.Hunks(Before, reverted).Single()));
    }

    [Theory]
    [InlineData("one\ntwo\nthree\n", "one\nthree\n")]
    [InlineData("one\nthree\n", "one\ntwo\nthree\n")]
    [InlineData("", "made\nby Claude\n")]
    [InlineData("a\nb\n", "")]
    public void Additions_and_removals_undo_back_to_before(string before, string current)
    {
        var hunk = LineDiff.Hunks(before, current).Single();

        Assert.Equal(before, Revert.Hunk(current, hunk));
    }

    [Fact]
    public void Windows_line_endings_and_a_missing_last_newline_are_kept()
    {
        var current = "one\r\nTWO\r\nthree";

        var reverted = Revert.Hunk(current, LineDiff.Hunks("one\ntwo\nthree", current).Single());

        Assert.Equal("one\r\ntwo\r\nthree", reverted);
    }

    [Fact]
    public void A_file_changed_since_the_diff_isnt_touched()
    {
        var current = Before.Replace("two\n", "TWO\n", StringComparison.Ordinal);
        var hunk = LineDiff.Hunks(Before, current).Single();

        Assert.Null(Revert.Hunk(current.Replace("TWO", "Two", StringComparison.Ordinal), hunk));
        Assert.Null(Revert.Hunk("", hunk));
    }

    [Fact]
    public void Writing_back_refuses_when_the_file_moved_on_and_keeps_its_byte_order_mark()
    {
        using var temp = new TempFolder();
        var path = temp.Combine("a.txt");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, (byte)'n', (byte)'e', (byte)'w', (byte)'\n']);

        Assert.False(Revert.Write(path, "something else\n", "old\n"));
        Assert.True(Revert.Write(path, "new\n", "old\n"));
        Assert.Equal([0xEF, 0xBB, 0xBF, (byte)'o', (byte)'l', (byte)'d', (byte)'\n'], File.ReadAllBytes(path));

        // A file Claude created goes back to not existing.
        Assert.True(Revert.Write(path, "old\n", null));
        Assert.False(File.Exists(path));
    }
}
