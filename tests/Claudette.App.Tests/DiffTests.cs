using System.Text.Json.Nodes;
using Claudette.App.Conversation;

namespace Claudette.App.Tests;

public class DiffTests
{
    [Fact]
    public void Structured_patch_lines_are_numbered_and_counted()
    {
        var patch = JsonNode.Parse("""
            [{"oldStart":10,"oldLines":3,"newStart":10,"newLines":4,"lines":[" keep"," -x","-old","+new","+added","\\ No newline at end of file"]}]
            """);

        var diff = DiffView.FromStructuredPatch(patch)!;

        Assert.Equal("+2 −1", diff.Stats);
        Assert.Equal(DiffLineKind.Hunk, diff.Lines[0].Kind);
        Assert.Equal((DiffLineKind.Context, "keep", 10, 10), Tuple(diff.Lines[1]));
        Assert.Equal((DiffLineKind.Removed, "old", 12, (int?)null), Tuple(diff.Lines[3]));
        Assert.Equal((DiffLineKind.Added, "new", (int?)null, 12), Tuple(diff.Lines[4]));
        Assert.Equal(6, diff.Lines.Count);
    }

    [Fact]
    public void An_empty_patch_is_no_diff()
    {
        Assert.Null(DiffView.FromStructuredPatch(JsonNode.Parse("[]")));
        Assert.Null(DiffView.FromStructuredPatch(null));
    }

    [Fact]
    public void A_replacement_shows_old_as_removed_and_new_as_added()
    {
        var diff = DiffView.FromReplacement("a\nb", "c");

        Assert.Equal("+1 −2", diff.Stats);
        Assert.Equal([DiffLineKind.Removed, DiffLineKind.Removed, DiffLineKind.Added], diff.Lines.Select(l => l.Kind));
    }

    [Fact]
    public void Very_long_diffs_are_cut_with_a_note()
    {
        var diff = DiffView.FromNewFile(string.Join("\n", Enumerable.Range(0, 1000)) + "\n");

        Assert.Equal(1000, diff.Added);
        Assert.Equal(DiffView.MaxLines + 1, diff.Lines.Count);
        Assert.Equal(DiffLineKind.Note, diff.Lines[^1].Kind);
    }

    private static (DiffLineKind, string, int?, int?) Tuple(DiffLine line) => (line.Kind, line.Text, line.OldNumber, line.NewNumber);
}
