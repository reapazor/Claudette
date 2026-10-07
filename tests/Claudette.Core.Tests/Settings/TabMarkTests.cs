using System.Globalization;
using Claudette.Core.Library;
using Claudette.Core.Settings;

namespace Claudette.Core.Tests.Settings;

/// <summary>A tab's mark, as it's stored and found again for a past session (DESIGN.md §4, "Marks").</summary>
public sealed class TabMarkTests : IDisposable
{
    private static readonly DateTimeOffset Noon = DateTimeOffset.Parse("2026-10-07T12:00:00Z", CultureInfo.InvariantCulture);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"claudette-marks-{Guid.NewGuid():N}");

    public TabMarkTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Each_mark_is_stored_as_a_word_that_reads_back_as_the_same_mark()
    {
        Assert.Equal([TabMark.Check, TabMark.Cross, TabMark.Question, TabMark.Star, TabMark.Flag, TabMark.Pause], TabMarks.All);
        Assert.Equal(["check", "cross", "question", "star", "flag", "pause"], TabMarks.All.Select(TabMarks.Key));
        foreach (var mark in TabMarks.All)
        {
            Assert.Equal(mark, TabMarks.Parse(TabMarks.Key(mark)));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("heart")]
    public void No_word_or_one_a_later_Claudette_added_reads_as_no_mark(string? key) => Assert.Null(TabMarks.Parse(key));

    [Fact]
    public void A_past_session_has_whichever_mark_was_written_last_of_its_record_and_this_machines()
    {
        var record = new SessionRecord { SessionId = "s1", Mark = "star", LastUsed = Noon };

        // Only one of them.
        Assert.Equal("star", TabMarks.ForSession(record, local: null));
        Assert.Equal("flag", TabMarks.ForSession(record: null, new SessionMark { Mark = "flag", At = Noon }));
        Assert.Null(TabMarks.ForSession(record: null, local: null));

        // Marked here since the record was written: a tab that stopped syncing never writes it again.
        Assert.Equal("flag", TabMarks.ForSession(record, new SessionMark { Mark = "flag", At = Noon.AddMinutes(5) }));
        // Cleared here since: the record's older mark doesn't come back.
        Assert.Null(TabMarks.ForSession(record, new SessionMark { Mark = null, At = Noon.AddMinutes(5) }));
        // The record was written since, from here or another machine: it has the latest.
        Assert.Equal("star", TabMarks.ForSession(record, new SessionMark { Mark = "flag", At = Noon.AddMinutes(-5) }));
        Assert.Null(TabMarks.ForSession(new SessionRecord { SessionId = "s1", LastUsed = Noon }, new SessionMark { Mark = "flag", At = Noon.AddMinutes(-5) }));
    }

    [Fact]
    public async Task A_tabs_mark_and_this_machines_session_marks_round_trip()
    {
        var store = new JsonFileStore<AppState>(Path.Combine(_root, "state.json"));
        await store.SaveAsync(new AppState
        {
            Tabs = [new TabState { Folder = "/work/api", SessionId = "s1", Mark = "question" }],
            SessionMarks = { ["s1"] = new SessionMark { Mark = "question", At = Noon }, ["s0"] = new SessionMark { Mark = null, At = Noon } },
        }, TestContext.Current.CancellationToken);

        var loaded = store.Load();

        Assert.Equal("question", Assert.Single(loaded.Tabs).Mark);
        Assert.Equal(("question", Noon), (loaded.SessionMarks["s1"].Mark, loaded.SessionMarks["s1"].At));
        Assert.Null(loaded.SessionMarks["s0"].Mark);
    }

    [Fact]
    public void A_state_file_from_before_marks_loads_with_none()
    {
        var path = Path.Combine(_root, "state.json");
        File.WriteAllText(path, """{ "version": 1, "tabs": [ { "folder": "/work/api", "sessionId": "s1" } ] }""");

        var loaded = new JsonFileStore<AppState>(path).Load();

        Assert.Null(Assert.Single(loaded.Tabs).Mark);
        Assert.Empty(loaded.SessionMarks);
    }
}
