using System.Text.Json.Nodes;
using Claudette.Core.Diffs;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Diffs;

/// <summary>Marking changed files as reviewed (DESIGN.md §8, "Reviewed").</summary>
public sealed class ReviewedFilesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "claudette-reviewed");
    private readonly ChangedFiles _changes = new(new FakeTimeProvider());
    private readonly List<ReviewedFile> _marks = [];
    private readonly ReviewedFiles _reviewed;

    public ReviewedFilesTests() => _reviewed = new ReviewedFiles(_marks);

    private static string PathOf(string name) => Path.Combine(Root, "src", name);

    [Fact]
    public void A_file_stays_reviewed_until_Claude_changes_it_again()
    {
        var path = PathOf("auth.cs");
        Edit("t1", path);
        _reviewed.Mark(path, ReviewedFiles.LatestChange(_changes.Find(path)));

        Assert.True(_reviewed.IsReviewed(path, _changes.Find(path)));
        Assert.Equal([(path, "t1")], _marks.Select(m => (m.Path, m.Change)));

        Edit("t2", path);
        Assert.False(_reviewed.IsReviewed(path, _changes.Find(path)));
    }

    [Fact]
    public void Marking_an_older_change_than_Claudes_latest_leaves_the_file_unreviewed()
    {
        var path = PathOf("auth.cs");
        Edit("t1", path);
        Edit("t2", path);

        _reviewed.Mark(path, "t1");

        Assert.False(_reviewed.IsReviewed(path, _changes.Find(path)));
    }

    [Fact]
    public void A_file_Claude_never_changed_is_reviewed_until_it_does()
    {
        var path = PathOf("readme.md");
        _reviewed.Mark(path, change: null);
        Assert.True(_reviewed.IsReviewed(path, _changes.Find(path)));

        Edit("t1", path);

        Assert.False(_reviewed.IsReviewed(path, _changes.Find(path)));
    }

    [Fact]
    public void Unmarking_clears_it_and_marking_again_updates_the_change()
    {
        var path = PathOf("auth.cs");
        Edit("t1", path);
        _reviewed.Mark(path, "t1");

        Assert.True(_reviewed.Unmark(path));
        Assert.False(_reviewed.IsReviewed(path, _changes.Find(path)));
        Assert.False(_reviewed.Unmark(path));

        Edit("t2", path);
        _reviewed.Mark(path, "t1");
        _reviewed.Mark(path, "t2");
        Assert.True(_reviewed.IsReviewed(path, _changes.Find(path)));
        Assert.Single(_marks);
    }

    [Fact]
    public void Paths_match_by_the_platforms_rule()
    {
        var path = PathOf("Auth.cs");
        var insensitive = new ReviewedFiles([], StringComparer.OrdinalIgnoreCase);
        var sensitive = new ReviewedFiles([], StringComparer.Ordinal);

        insensitive.Mark(path, null);
        sensitive.Mark(path, null);

        Assert.True(insensitive.IsReviewed(path.ToLowerInvariant(), null));
        Assert.False(sensitive.IsReviewed(path.ToLowerInvariant(), null));
    }

    [Fact]
    public void Pruning_forgets_marks_Claude_has_changed_the_file_since()
    {
        var (current, superseded, beforeClaude, unknown, untouched) = (PathOf("a.cs"), PathOf("b.cs"), PathOf("c.cs"), PathOf("d.cs"), PathOf("e.cs"));
        Edit("a1", current);
        Edit("b1", superseded);
        Edit("b2", superseded);
        Edit("c1", beforeClaude);
        Edit("d1", unknown);
        _reviewed.Mark(current, "a1");
        _reviewed.Mark(superseded, "b1");
        _reviewed.Mark(beforeClaude, null);
        // From a transcript that hasn't finished replaying: the change isn't in the session yet.
        _reviewed.Mark(unknown, "d9");
        _reviewed.Mark(untouched, null);

        Assert.True(_reviewed.Prune(_changes));

        Assert.Equal([current, unknown, untouched], _marks.Select(m => m.Path));
        Assert.False(_reviewed.Prune(_changes));
    }

    [Fact]
    public void A_record_keeps_paths_relative_to_the_folder_and_another_machine_finds_them_in_its_own()
    {
        var folder = Path.Combine(Root, "api");
        var inside = Path.Combine(folder, "src", "auth.cs");
        var beside = Path.Combine(Root, "shared", "util.cs");
        var marks = new List<ReviewedFile> { new() { Path = inside, Change = "t1" }, new() { Path = beside } };

        var record = ReviewedFiles.ToRecord(marks, folder);

        Assert.Equal([("src/auth.cs", "t1"), ("../shared/util.cs", null)], record.Select(m => (m.Path, m.Change)));

        var elsewhere = Path.Combine(Path.GetTempPath(), "elsewhere", "api");
        var restored = ReviewedFiles.FromRecord(record, elsewhere);

        Assert.Equal(
            [(Path.Combine(elsewhere, "src", "auth.cs"), "t1"), (Path.GetFullPath(Path.Combine(elsewhere, "..", "shared", "util.cs")), null)],
            restored.Select(m => (m.Path, m.Change)));
    }

    [Fact]
    public void A_record_path_that_isnt_relative_is_ignored()
    {
        var folder = Path.Combine(Root, "api");
        var rooted = Path.Combine(Root, "api", "src", "auth.cs");

        var restored = ReviewedFiles.FromRecord([new ReviewedFile { Path = rooted }, new ReviewedFile { Path = "" }], folder);

        Assert.Empty(restored);
        Assert.Empty(ReviewedFiles.FromRecord(null, folder));
    }

    [Fact]
    public void A_file_on_another_drive_is_left_out_of_the_record()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows has drives.");
        var marks = new List<ReviewedFile> { new() { Path = @"E:\other\a.cs" } };

        Assert.Empty(ReviewedFiles.ToRecord(marks, @"D:\Repos\api"));
    }

    private void Edit(string id, string path)
    {
        _changes.RecordToolUse(id, "Edit", new JsonObject { ["file_path"] = path });
        _changes.RecordToolResult(id, false, new JsonObject
        {
            ["filePath"] = path,
            ["oldString"] = "a",
            ["newString"] = "b",
            ["originalFile"] = "a\n",
        });
    }
}
