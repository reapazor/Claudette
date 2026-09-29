using System.Text.Json.Nodes;
using Claudette.Core.Diffs;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Diffs;

public sealed class ChangedFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"claudette-changed-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly ChangedFiles _files;

    public ChangedFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        _files = new ChangedFiles(_time);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string PathOf(string name) => Path.Combine(_root, "src", name);

    [Fact]
    public void The_first_change_supplies_the_before_side()
    {
        var path = PathOf("auth.cs");
        Edit("t1", path, "one\n");
        _time.Advance(TimeSpan.FromMinutes(2));
        Edit("t2", path, "one changed\n");

        var file = Assert.Single(_files.Files);
        Assert.Equal(path, file.Path);
        Assert.Equal("one\n", file.Before);
        Assert.True(file.BeforeKnown);
        Assert.False(file.IsNew);
        Assert.Equal(2, file.ChangeCount);
        Assert.Equal(["t1", "t2"], file.ToolUseIds);
        Assert.Equal(_time.GetUtcNow() - TimeSpan.FromMinutes(2), file.FirstChanged);
        Assert.Equal(_time.GetUtcNow(), file.LastChanged);
    }

    [Fact]
    public void Files_are_listed_in_order_of_first_change()
    {
        Edit("t1", PathOf("b.cs"), "b");
        Edit("t2", PathOf("a.cs"), "a");
        Edit("t3", PathOf("b.cs"), "b2");

        Assert.Equal(["b.cs", "a.cs"], _files.Files.Select(f => Path.GetFileName(f.Path)));
    }

    [Fact]
    public void A_write_that_creates_a_file_is_new()
    {
        Use("t1", "Write", PathOf("new.cs"));
        _files.RecordToolResult("t1", false, JsonNode.Parse($$"""{"type":"create","filePath":{{Json(PathOf("new.cs"))}},"content":"x\n","structuredPatch":[],"originalFile":null}"""));

        var file = Assert.Single(_files.Files);
        Assert.True(file.IsNew);
        Assert.True(file.BeforeKnown);
        Assert.Null(file.Before);
    }

    [Fact]
    public void A_write_that_replaces_a_file_keeps_the_original()
    {
        Use("t1", "Write", PathOf("a.cs"));
        _files.RecordToolResult("t1", false, JsonNode.Parse($$"""{"type":"update","filePath":{{Json(PathOf("a.cs"))}},"content":"new","originalFile":"old\n"}"""));

        Assert.Equal("old\n", Assert.Single(_files.Files).Before);
    }

    [Fact]
    public void A_write_over_a_file_too_large_to_diff_has_an_unknown_before()
    {
        Use("t1", "Write", PathOf("big.txt"));
        _files.RecordToolResult("t1", false, JsonNode.Parse("""{"type":"update","originalFile":null,"structuredPatch":[]}"""));

        var file = Assert.Single(_files.Files);
        Assert.False(file.IsNew);
        Assert.False(file.BeforeKnown);
    }

    [Fact]
    public void An_edit_that_creates_a_file_is_new()
    {
        Use("t1", "Edit", PathOf("a.cs"));
        _files.RecordToolResult("t1", false, JsonNode.Parse("""{"oldString":"","newString":"x","originalFile":null}"""));

        Assert.True(Assert.Single(_files.Files).IsNew);
    }

    [Theory]
    [InlineData("""{"stdout":"","stderr":""}""")]
    [InlineData("\"The file was updated.\"")]
    [InlineData(null)]
    public void A_result_without_original_file_still_counts_with_an_unknown_before(string? json)
    {
        Use("t1", "MultiEdit", PathOf("a.cs"));
        _files.RecordToolResult("t1", false, json is null ? null : JsonNode.Parse(json));

        var file = Assert.Single(_files.Files);
        Assert.False(file.BeforeKnown);
        Assert.False(file.IsNew);
        Assert.Null(file.Before);
    }

    [Fact]
    public void Notebook_edits_use_their_own_field_names()
    {
        var path = PathOf("analysis.ipynb");
        _files.RecordToolUse("t1", "NotebookEdit", new JsonObject { ["notebook_path"] = path, ["new_source"] = "print(1)" });
        _files.RecordToolResult("t1", false, new JsonObject { ["notebook_path"] = path, ["original_file"] = "{}", ["updated_file"] = "{\"cells\":[]}" });

        var file = Assert.Single(_files.Files);
        Assert.Equal(path, file.Path);
        Assert.Equal("{}", file.Before);
    }

    [Fact]
    public void Error_results_and_failed_notebook_edits_are_ignored()
    {
        Use("t1", "Edit", PathOf("a.cs"));
        _files.RecordToolResult("t1", true, null);
        _files.RecordToolUse("t2", "NotebookEdit", new JsonObject { ["notebook_path"] = PathOf("n.ipynb") });
        _files.RecordToolResult("t2", false, new JsonObject { ["error"] = "Cell not found", ["original_file"] = "{}" });

        Assert.Empty(_files.Files);
    }

    [Fact]
    public void Other_tools_and_unknown_results_are_ignored()
    {
        _files.RecordToolUse("t1", "Read", new JsonObject { ["file_path"] = PathOf("a.cs") });
        _files.RecordToolResult("t1", false, new JsonObject { ["filePath"] = PathOf("a.cs"), ["originalFile"] = "x" });
        _files.RecordToolResult("never-used", false, new JsonObject { ["filePath"] = PathOf("b.cs"), ["originalFile"] = "x" });

        Assert.Empty(_files.Files);
    }

    [Fact]
    public void Replaying_the_same_events_does_not_count_twice()
    {
        var changed = 0;
        _files.Changed += () => changed++;
        for (var replay = 0; replay < 2; replay++)
        {
            Edit("t1", PathOf("a.cs"), "one\n");
            Edit("t2", PathOf("a.cs"), "two\n");
        }

        var file = Assert.Single(_files.Files);
        Assert.Equal(2, file.ChangeCount);
        Assert.Equal("one\n", file.Before);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Replay_can_supply_the_time_of_each_change()
    {
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Use("t1", "Edit", PathOf("a.cs"));
        _files.RecordToolResult("t1", false, new JsonObject { ["originalFile"] = "x" }, at);

        Assert.Equal(at, Assert.Single(_files.Files).FirstChanged);
    }

    [Fact]
    public void Paths_match_by_the_platform_rule()
    {
        var insensitive = new ChangedFiles(_time, StringComparer.OrdinalIgnoreCase);
        var sensitive = new ChangedFiles(_time, StringComparer.Ordinal);
        foreach (var files in new[] { insensitive, sensitive })
        {
            files.RecordToolUse("t1", "Edit", new JsonObject { ["file_path"] = PathOf("Auth.cs") });
            files.RecordToolResult("t1", false, new JsonObject { ["originalFile"] = "x" });
            files.RecordToolUse("t2", "Edit", new JsonObject { ["file_path"] = PathOf("auth.cs") });
            files.RecordToolResult("t2", false, new JsonObject { ["originalFile"] = "y" });
        }

        Assert.Equal(2, Assert.Single(insensitive.Files).ChangeCount);
        Assert.Equal(2, sensitive.Files.Count);
        Assert.Same(
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase,
            ChangedFiles.PlatformPathComparer);
    }

    [Fact]
    public void Paths_are_matched_after_normalizing()
    {
        Edit("t1", PathOf("a.cs"), "x");
        Edit("t2", Path.Combine(_root, "src", "..", "src", "a.cs"), "y");

        Assert.Equal(2, Assert.Single(_files.Files).ChangeCount);
    }

    [Fact]
    public void Inspect_counts_the_lines_changed_since_before()
    {
        var path = PathOf("a.cs");
        Edit("t1", path, "one\ntwo\nthree\n");
        File.WriteAllText(path, "one\n2\nthree\nfour\n");

        var state = _files.Inspect(Assert.Single(_files.Files));

        Assert.Equal(ChangedFileStatus.Modified, state.Status);
        Assert.Equal(2, state.Added);
        Assert.Equal(1, state.Removed);
        Assert.Equal("one\n2\nthree\nfour\n", state.Current);
    }

    [Fact]
    public void Inspect_sees_a_file_put_back_as_unchanged_even_with_other_line_endings()
    {
        var path = PathOf("a.cs");
        Edit("t1", path, "one\ntwo\n");
        File.WriteAllText(path, "one\r\ntwo\r\n");

        var state = _files.Inspect(Assert.Single(_files.Files));

        Assert.Equal(ChangedFileStatus.Unchanged, state.Status);
        Assert.Equal((0, 0), (state.Added, state.Removed));
    }

    [Fact]
    public void Inspect_counts_a_new_file_as_added()
    {
        var path = PathOf("new.cs");
        Use("t1", "Write", path);
        _files.RecordToolResult("t1", false, new JsonObject { ["type"] = "create", ["originalFile"] = null });
        File.WriteAllText(path, "a\nb\nc");

        var state = _files.Inspect(Assert.Single(_files.Files));

        Assert.Equal(ChangedFileStatus.Added, state.Status);
        Assert.Equal((3, 0), (state.Added, state.Removed));
    }

    [Fact]
    public void Inspect_reports_a_missing_file_as_deleted()
    {
        Edit("t1", PathOf("gone.cs"), "a\nb\n");

        var state = _files.Inspect(Assert.Single(_files.Files));

        Assert.Equal(ChangedFileStatus.Deleted, state.Status);
        Assert.Equal((0, 2), (state.Added, state.Removed));
        Assert.Null(state.Current);
    }

    [Fact]
    public void Inspect_treats_a_new_file_that_was_removed_again_as_unchanged()
    {
        Use("t1", "Write", PathOf("temp.cs"));
        _files.RecordToolResult("t1", false, new JsonObject { ["type"] = "create" });

        Assert.Equal(ChangedFileStatus.Unchanged, _files.Inspect(Assert.Single(_files.Files)).Status);
    }

    [Fact]
    public void Inspect_does_not_count_lines_of_binary_or_very_large_files()
    {
        var binary = PathOf("image.png");
        var large = PathOf("large.txt");
        File.WriteAllBytes(binary, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        File.WriteAllText(large, new string('x', 2_000));
        var files = new ChangedFiles(_time) { MaxInspectBytes = 1_000 };
        foreach (var (id, path) in new[] { ("t1", binary), ("t2", large) })
        {
            files.RecordToolUse(id, "Edit", new JsonObject { ["file_path"] = path });
            files.RecordToolResult(id, false, new JsonObject { ["originalFile"] = "old" });
        }

        var binaryState = files.Inspect(files.Files[0]);
        var largeState = files.Inspect(files.Files[1]);

        Assert.True(binaryState.IsBinary);
        Assert.Equal(ChangedFileStatus.Modified, binaryState.Status);
        Assert.Null(binaryState.Added);
        Assert.Null(binaryState.Current);
        Assert.True(largeState.IsTooLarge);
        Assert.Null(largeState.Removed);
    }

    [Fact]
    public void Inspect_cannot_count_without_a_known_before()
    {
        var path = PathOf("a.cs");
        Use("t1", "Edit", path);
        _files.RecordToolResult("t1", false, new JsonObject { ["filePath"] = path });
        File.WriteAllText(path, "now\n");

        var state = _files.Inspect(Assert.Single(_files.Files));

        Assert.Equal(ChangedFileStatus.Modified, state.Status);
        Assert.Null(state.Added);
        Assert.Equal("now\n", state.Current);
    }

    [Fact]
    public void Display_paths_are_relative_inside_the_folder_and_full_outside_it()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"claudette-outside-{Guid.NewGuid():N}", "x.cs");
        Edit("t1", PathOf("auth.cs"), "a");
        Edit("t2", outside, "b");

        Assert.Equal("src/auth.cs", _files.DisplayPath(_files.Files[0], _root));
        Assert.Equal("src/auth.cs", _files.DisplayPath(_files.Files[0], _root + Path.DirectorySeparatorChar));
        Assert.Equal("auth.cs", _files.DisplayPath(_files.Files[0], Path.Combine(_root, "src")));
        Assert.Equal(outside.Replace('\\', '/'), _files.DisplayPath(_files.Files[1], _root));
        Assert.Equal(PathOf("auth.cs").Replace('\\', '/'), _files.DisplayPath(_files.Files[0], Path.Combine(_root, "sr")));
    }

    [Fact]
    public void Display_paths_follow_the_case_rule()
    {
        var insensitive = new ChangedFiles(_time, StringComparer.OrdinalIgnoreCase);
        insensitive.RecordToolUse("t1", "Edit", new JsonObject { ["file_path"] = PathOf("a.cs") });
        insensitive.RecordToolResult("t1", false, new JsonObject { ["originalFile"] = "x" });

        Assert.Equal("src/a.cs", insensitive.DisplayPath(insensitive.Files[0], _root.ToUpperInvariant()));
    }

    private void Use(string id, string tool, string path) =>
        _files.RecordToolUse(id, tool, new JsonObject { ["file_path"] = path });

    private void Edit(string id, string path, string originalFile)
    {
        Use(id, "Edit", path);
        _files.RecordToolResult(id, false, new JsonObject
        {
            ["filePath"] = path,
            ["oldString"] = "a",
            ["newString"] = "b",
            ["originalFile"] = originalFile,
            ["structuredPatch"] = new JsonArray(),
        });
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
