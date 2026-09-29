using System.Text.Json.Nodes;
using Claudette.Core.Diffs;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Diffs;

/// <summary>
/// A large file's <c>originalFile</c>, kept for replays: Claude Code sends it live but writes it to the transcript as
/// null (DESIGN.md §8, "Before content").
/// </summary>
public sealed class BeforeContentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"claudette-before-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    public BeforeContentStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Saved => Path.Combine(_root, "before-content");

    private string File1 => Path.Combine(_root, "big.cs");

    private ChangedFiles Create() => new(_time) { Befores = new BeforeContentStore(Saved, _time) };

    private static readonly string Large = string.Concat(Enumerable.Repeat("a line of a large file\n", 500));

    [Fact]
    public void A_large_original_file_seen_live_is_found_again_when_the_transcript_is_replayed()
    {
        var live = Create();
        Edit(live, "toolu_01", Large);
        Edit(live, "toolu_02", Large + "later\n");

        // A restored tab: a new session's objects, and the transcript's null.
        var replayed = Create();
        Edit(replayed, "toolu_01", null);
        Edit(replayed, "toolu_02", null);

        var file = Assert.Single(replayed.Files);
        Assert.True(file.BeforeKnown);
        Assert.Equal(Large, file.Before);
        Assert.Equal(2, file.ChangeCount);
        // Only the first change's is needed.
        Assert.Single(Directory.GetFiles(Saved));
    }

    [Fact]
    public void Original_files_a_transcript_keeps_are_not_saved()
    {
        var files = Create();

        Edit(files, "t1", new string('x', BeforeContentStore.TranscriptLimit));

        Assert.Equal(BeforeContentStore.TranscriptLimit, Assert.Single(files.Files).Before!.Length);
        Assert.False(Directory.Exists(Saved));
    }

    [Fact]
    public void A_replayed_large_file_that_was_never_seen_live_has_an_unknown_before()
    {
        var files = Create();

        Edit(files, "t1", null);

        var file = Assert.Single(files.Files);
        Assert.False(file.BeforeKnown);
        Assert.False(file.IsNew);
    }

    [Fact]
    public void Tool_use_ids_that_are_not_plain_never_name_a_file()
    {
        var store = new BeforeContentStore(Saved, _time);

        store.Save("../escape", "x");

        Assert.False(Directory.Exists(Saved));
        Assert.Null(store.Load("../escape"));
    }

    [Fact]
    public void Content_unused_for_30_days_is_deleted_and_using_it_keeps_it()
    {
        var store = new BeforeContentStore(Saved, _time);
        store.Save("old", "x");
        store.Save("used", "y");
        var old = Path.Combine(Saved, "old.txt.gz");
        var used = Path.Combine(Saved, "used.txt.gz");
        foreach (var path in new[] { old, used })
        {
            File.SetLastWriteTimeUtc(path, (_time.GetUtcNow() - TimeSpan.FromDays(31)).UtcDateTime);
        }

        Assert.Equal("y", store.Load("used"));
        BeforeContentStore.DeleteOld(Saved, _time.GetUtcNow());

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(used));
    }

    /// <summary>An Edit's result, with <c>originalFile</c> as live (the content) or as a transcript has it over the limit (null).</summary>
    private void Edit(ChangedFiles files, string id, string? originalFile)
    {
        files.RecordToolUse(id, "Edit", new JsonObject { ["file_path"] = File1 });
        files.RecordToolResult(id, false, new JsonObject
        {
            ["filePath"] = File1,
            ["oldString"] = "a line",
            ["newString"] = "a changed line",
            ["originalFile"] = originalFile,
            ["structuredPatch"] = new JsonArray(),
        });
    }
}
