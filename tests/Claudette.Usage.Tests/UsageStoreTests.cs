using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Usage.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Usage.Tests;

public sealed class UsageStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"claudette-usage-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(Start);
    private readonly List<UsageStore> _stores = [];

    public void Dispose()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DatabasePath => Path.Combine(_root, "nested", "usage.db");

    [Fact]
    public void A_new_store_creates_its_folder_and_is_empty()
    {
        var store = Open();

        Assert.True(File.Exists(DatabasePath));
        Assert.Null(store.GetLatestSample());
        Assert.Empty(store.GetSamples(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.Empty(store.GetTurns(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void A_sample_holds_the_session_weekly_and_model_readings()
    {
        var store = Open();
        var snapshot = UsageParser.FromGetUsage(UsageFixtures.GetUsage(), Start)!;

        Assert.True(store.AddSample(snapshot));

        var sample = store.GetLatestSample();
        Assert.NotNull(sample);
        Assert.Equal(Start, sample.Timestamp);
        Assert.Equal((13.0, UsageFixtures.SessionResetsAt), (sample.SessionPercent!.Value, sample.SessionResetsAt!.Value));
        Assert.Equal((57.0, UsageFixtures.WeekResetsAt), (sample.WeeklyPercent!.Value, sample.WeeklyResetsAt!.Value));
        Assert.Equal([new ModelSample("Fable", 100, UsageFixtures.WeekResetsAt)], sample.Models);

        var restored = sample.ToSnapshot();
        Assert.Equal(UsageSource.Stored, restored.Source);
        Assert.Equal(Start, restored.AsOf);
        Assert.Equal(
            snapshot.Limits.Select(l => (l.Kind, l.Label, l.Percent, l.ResetsAt)),
            restored.Limits.Select(l => (l.Kind, l.Label, l.Percent, l.ResetsAt)));
    }

    [Fact]
    public void A_sample_is_saved_only_when_a_value_changes()
    {
        var store = Open();

        Assert.True(store.AddSample(Snapshot(10)));
        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.False(store.AddSample(Snapshot(10)));
        Assert.True(store.AddSample(Snapshot(11)));
        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.True(store.AddSample(Snapshot(11, weekly: 58)));
        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.True(store.AddSample(Snapshot(11, weekly: 58, fable: 99)));
        _time.Advance(TimeSpan.FromMinutes(3));
        Assert.True(store.AddSample(Snapshot(11, resetsAt: Start.AddHours(9), weekly: 58, fable: 99)));

        Assert.Equal(5, store.GetSamples(Start, _time.GetUtcNow()).Count);
    }

    [Fact]
    public void A_change_inside_the_minute_is_held_until_the_minute_has_passed()
    {
        var store = Open();
        store.AddSample(Snapshot(10));

        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.False(store.AddSample(Snapshot(12)));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(store.AddSample(Snapshot(12)));
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(store.AddSample(Snapshot(12)));

        var samples = store.GetSamples(Start, _time.GetUtcNow());
        Assert.Equal([10.0, 12.0], samples.Select(s => s.SessionPercent!.Value));
        Assert.Equal(Start.AddMinutes(1), samples[1].Timestamp);
    }

    [Fact]
    public void A_held_change_that_reverts_is_not_saved()
    {
        var store = Open();
        store.AddSample(Snapshot(10));

        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.False(store.AddSample(Snapshot(12)));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(store.AddSample(Snapshot(10)));

        Assert.Single(store.GetSamples(Start, _time.GetUtcNow()));
    }

    [Fact]
    public void An_empty_snapshot_is_not_saved()
    {
        var store = Open();

        Assert.False(store.AddSample(new UsageSnapshot([], Start, UsageSource.GetUsage)));
        Assert.Null(store.GetLatestSample());
    }

    [Fact]
    public void Samples_are_returned_by_range_oldest_first()
    {
        var store = Open();
        for (var i = 0; i < 5; i++)
        {
            store.AddSample(Snapshot(10 + i));
            _time.Advance(TimeSpan.FromMinutes(10));
        }

        var middle = store.GetSamples(Start.AddMinutes(10), Start.AddMinutes(30));

        Assert.Equal([11.0, 12.0, 13.0], middle.Select(s => s.SessionPercent!.Value));
        Assert.Empty(store.GetSamples(Start.AddMinutes(41), Start.AddHours(1)));
        Assert.Equal(14, store.GetLatestSample()!.SessionPercent);
    }

    [Fact]
    public void Past_windows_give_each_window_its_peak_newest_first_and_leave_out_the_current_one()
    {
        var store = Open();
        var first = Start.AddHours(1);
        var second = Start.AddHours(6);
        store.AddSample(Snapshot(20, first));
        _time.Advance(TimeSpan.FromMinutes(30));
        store.AddSample(Snapshot(64, first));
        _time.Advance(TimeSpan.FromMinutes(40));
        // The same window from the other source, a fraction of a second off.
        store.AddSample(Snapshot(71, first.AddMilliseconds(-50)));
        _time.Advance(TimeSpan.FromHours(1));
        store.AddSample(Snapshot(5, second));
        _time.Advance(TimeSpan.FromHours(1));
        store.AddSample(Snapshot(9, Start.AddHours(11)));

        var past = store.GetPastWindows(UsageWindow.Session, second);

        Assert.Equal([(second, 5.0), (first, 71.0)], past.Select(p => (p.ResetsAt, p.PeakPercent)));
        Assert.Empty(store.GetPastWindows(UsageWindow.Weekly, second));
    }

    [Fact]
    public void Reopening_the_file_keeps_the_history_and_the_sampling_rule()
    {
        var store = Open();
        store.AddSample(UsageParser.FromGetUsage(UsageFixtures.GetUsage(), Start)!);
        store.AddTurns([new TurnRecord(Start, "tab-1", "s1", "claude-fable", 1, 2, 3, 4, 0.5)]);
        store.Dispose();

        var reopened = Open();

        Assert.Equal(13, reopened.GetLatestSample()!.SessionPercent);
        Assert.Single(reopened.GetTurns(Start, Start));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(reopened.AddSample(Snapshot(20)));
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(reopened.AddSample(UsageParser.FromGetUsage(UsageFixtures.GetUsage(), _time.GetUtcNow())!));
        Assert.True(reopened.AddSample(Snapshot(20)));
    }

    [Fact]
    public void Turn_records_come_one_per_model_from_a_result()
    {
        var modelUsage = JsonNode.Parse("""
            {
              "claude-fable-5": {"inputTokens": 10, "outputTokens": 200, "cacheCreationInputTokens": 3000, "cacheReadInputTokens": 40000, "costUSD": 0.25, "contextWindow": 200000},
              "claude-haiku-4-5": {"inputTokens": 5, "outputTokens": 6, "costUSD": 0.001},
              "broken": 12
            }
            """)!.AsObject();
        var result = new ResultMessage("success", false, "ok", null, "session-1", 0.251, null, modelUsage, []);

        var turns = TurnRecord.FromResult(result, "tab-1", Start);

        Assert.Equal(
            [
                new TurnRecord(Start, "tab-1", "session-1", "claude-fable-5", 10, 200, 3000, 40000, 0.25),
                new TurnRecord(Start, "tab-1", "session-1", "claude-haiku-4-5", 5, 6, 0, 0, 0.001),
            ],
            turns);
        Assert.Empty(TurnRecord.FromResult(result with { ModelUsage = null }, "tab-1", Start));
    }

    [Fact]
    public void Turns_are_returned_by_range_and_tab()
    {
        var store = Open();
        store.AddTurns(
        [
            new TurnRecord(Start, "tab-1", "s1", "fable", 1, 1, 1, 1, 0.1),
            new TurnRecord(Start.AddMinutes(5), "tab-2", null, "fable", 2, 2, 2, 2, 0.2),
            new TurnRecord(Start.AddMinutes(10), "tab-1", "s1", "fable", 3, 3, 3, 3, 0.3),
        ]);

        Assert.Equal(3, store.GetTurns(Start, Start.AddMinutes(10)).Count);
        Assert.Equal([1L, 3L], store.GetTurns(Start, Start.AddHours(1), "tab-1").Select(t => t.Input));
        var second = Assert.Single(store.GetTurns(Start.AddMinutes(1), Start.AddMinutes(9)));
        Assert.Equal(new TurnRecord(Start.AddMinutes(5), "tab-2", null, "fable", 2, 2, 2, 2, 0.2), second);
    }

    [Fact]
    public void Tokens_by_tab_puts_the_heaviest_tab_first_and_counts_turns()
    {
        var store = Open();
        store.AddTurns(
        [
            new TurnRecord(Start.AddHours(-6), "tab-old", null, "fable", 1_000_000, 0, 0, 0, 9),
            new TurnRecord(Start, "tab-1", "s1", "fable", 100, 10, 0, 1000, 0.1),
            new TurnRecord(Start, "tab-1", "s1", "haiku", 50, 5, 0, 0, 0.01),
            new TurnRecord(Start.AddMinutes(1), "tab-1", "s1", "fable", 100, 10, 0, 1000, 0.1),
            new TurnRecord(Start.AddMinutes(2), "tab-2", "s2", "fable", 10_000, 500, 200, 90_000, 1.5),
        ]);

        var sums = store.GetTokensByTab(Start.AddHours(-5));

        Assert.Equal(["tab-2", "tab-1"], sums.Select(s => s.TabId));
        var tab1 = sums[1];
        Assert.Equal(new TabTokenSum("tab-1", 250, 25, 0, 2000, 0.21, 2), tab1 with { CostUsd = Math.Round(tab1.CostUsd, 6) });
        Assert.Equal(2275, tab1.Total);
        Assert.Equal(1, sums[0].Turns);
    }

    [Fact]
    public void Tokens_by_tab_carry_each_tabs_last_known_name()
    {
        var store = Open();
        // A tab without turns gets no name stored.
        store.SetTabName("tab-2", "not used yet");
        store.AddTurns(
        [
            new TurnRecord(Start, "tab-1", "s1", "fable", 100, 0, 0, 0, 0.1),
            new TurnRecord(Start, "tab-2", "s2", "fable", 50, 0, 0, 0, 0.1),
        ]);

        store.SetTabName("tab-1", "api");
        store.SetTabName("tab-1", "refactor auth");

        Assert.Equal(
            [("tab-1", "refactor auth"), ("tab-2", null)],
            store.GetTokensByTab(Start.AddHours(-5)).Select(s => (s.TabId, s.Name)));
    }

    [Fact]
    public void Prune_and_clear_drop_the_names_of_tabs_with_no_turns_left()
    {
        var store = Open();
        store.AddTurns([new TurnRecord(Start, "old", null, "fable", 1, 1, 1, 1, 0)]);
        store.SetTabName("old", "an old tab");
        _time.Advance(TimeSpan.FromDays(2));
        store.AddTurns([new TurnRecord(_time.GetUtcNow(), "new", null, "fable", 1, 1, 1, 1, 0)]);
        store.SetTabName("new", "a new tab");

        Assert.Equal(1, store.Prune(TimeSpan.FromDays(1)));

        // The old tab's name went with its turns, so a turn under its id again starts unnamed.
        store.AddTurns([new TurnRecord(_time.GetUtcNow(), "old", null, "fable", 1, 1, 1, 1, 0)]);
        Assert.Equal(
            [("new", "a new tab"), ("old", null)],
            store.GetTokensByTab(DateTimeOffset.MinValue).Select(s => (s.TabId, s.Name)));

        store.Clear();
        store.AddTurns([new TurnRecord(_time.GetUtcNow(), "new", null, "fable", 1, 1, 1, 1, 0)]);
        Assert.Null(Assert.Single(store.GetTokensByTab(DateTimeOffset.MinValue)).Name);
    }

    [Fact]
    public void A_history_from_before_tab_names_is_upgraded_and_keeps_its_turns()
    {
        var store = Open();
        store.AddTurns([new TurnRecord(Start, "tab-1", "s1", "fable", 1, 2, 3, 4, 0.5)]);
        store.Dispose();
        // The first version of the file had no names.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE tabs; PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        var upgraded = Open();

        var sum = Assert.Single(upgraded.GetTokensByTab(DateTimeOffset.MinValue));
        Assert.Equal(("tab-1", 10L, (string?)null), (sum.TabId, sum.Total, sum.Name));
        upgraded.SetTabName("tab-1", "refactor auth");
        Assert.Equal("refactor auth", Assert.Single(upgraded.GetTokensByTab(DateTimeOffset.MinValue)).Name);
    }

    // ---- Sharing across machines (DESIGN.md §6) --------------------------------------------------------------------

    [Fact]
    public void Samples_another_machine_shared_are_imported_once_and_never_shared_again()
    {
        var store = Open();
        store.AddSample(Snapshot(10));
        var theirs = new[] { Shared(Start.AddMinutes(-2), 8), Shared(Start.AddMinutes(1), 12) };

        Assert.Equal(2, store.ImportSamples("machine-2", theirs));
        Assert.Equal(0, store.ImportSamples("machine-2", theirs));

        // The charts see every machine's readings, in time order; only this machine's own are shared.
        Assert.Equal([8, 10, 12], store.GetSamples(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Select(s => s.SessionPercent));
        Assert.Equal([10], store.GetOwnSamples(DateTimeOffset.MinValue).Select(s => s.SessionPercent));
        Assert.Equal(12, store.GetLatestSample()?.SessionPercent);

        // Newer readings come in; older ones it already had don't.
        Assert.Equal(1, store.ImportSamples("machine-2", [.. theirs, Shared(Start.AddMinutes(5), 15)]));
    }

    [Fact]
    public void Samples_from_the_future_wait_until_their_time_has_come()
    {
        // A machine whose clock runs ahead: imported now, its sample would move the mark past everything it sends once
        // its clock is put right, and stand as the newest reading.
        var store = Open();
        var ahead = Shared(Start.AddHours(1), 50);

        Assert.Equal(0, store.ImportSamples("machine-2", [ahead]));
        Assert.Equal(1, store.ImportSamples("machine-2", [Shared(Start.AddMinutes(-1), 20), ahead]));
        Assert.Equal(20, store.GetLatestSample()?.SessionPercent);

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, store.ImportSamples("machine-2", [ahead]));
    }

    [Fact]
    public void A_clock_set_back_doesnt_stop_samples_being_written()
    {
        var store = Open();
        Assert.True(store.AddSample(Snapshot(10)));

        _time.AdjustTime(Start.AddHours(-3));
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(store.AddSample(Snapshot(12)));
    }

    [Fact]
    public void Another_machines_readings_dont_count_as_this_machines_last_one()
    {
        var store = Open();
        store.AddSample(Snapshot(10));
        store.ImportSamples("machine-2", [Shared(Start.AddMinutes(1), 12)]);
        store.Dispose();
        _time.Advance(TimeSpan.FromMinutes(2));

        // Reopened, a reading of 12 here is still a change from this machine's 10.
        Assert.True(Open().AddSample(Snapshot(12)));
    }

    [Fact]
    public void Cleared_history_isnt_imported_again()
    {
        var store = Open();
        var theirs = new[] { Shared(Start.AddMinutes(-2), 8) };
        store.ImportSamples("machine-2", theirs);

        store.Clear();

        Assert.Equal(0, store.ImportSamples("machine-2", theirs));
        Assert.Equal(0, store.ImportSamples("machine-3", [Shared(Start.AddMinutes(-1), 9)]));
        Assert.Equal(1, store.ImportSamples("machine-3", [Shared(Start.AddMinutes(1), 9)]));
    }

    [Fact]
    public void A_history_from_before_sharing_is_upgraded_and_its_samples_are_this_machines()
    {
        var store = Open();
        store.AddSample(Snapshot(10));
        store.Dispose();
        // Version 2 had no machine column and no imports.
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE old_samples AS SELECT id, timestamp, session_percent, session_resets_at, weekly_percent, weekly_resets_at, models FROM samples;
                DROP TABLE samples; ALTER TABLE old_samples RENAME TO samples; DROP TABLE imports; PRAGMA user_version = 2;
                """;
            command.ExecuteNonQuery();
        }

        var upgraded = Open();

        Assert.Equal([10], upgraded.GetOwnSamples(DateTimeOffset.MinValue).Select(s => s.SessionPercent));
        Assert.Equal(1, upgraded.ImportSamples("machine-2", [Shared(Start.AddMinutes(1), 12)]));
    }

    [Fact]
    public void Prune_deletes_records_older_than_the_retention()
    {
        var store = Open();
        store.AddSample(Snapshot(10));
        store.AddTurns([new TurnRecord(Start, "tab-1", null, "fable", 1, 1, 1, 1, 0)]);
        _time.Advance(TimeSpan.FromDays(2));
        store.AddSample(Snapshot(11));
        store.AddTurns([new TurnRecord(_time.GetUtcNow(), "tab-1", null, "fable", 1, 1, 1, 1, 0)]);

        Assert.Equal(0, store.Prune(null));
        Assert.Equal(0, store.Prune(TimeSpan.FromDays(30)));
        Assert.Equal(2, store.Prune(TimeSpan.FromDays(1)));

        Assert.Equal(11, Assert.Single(store.GetSamples(DateTimeOffset.MinValue, DateTimeOffset.MaxValue)).SessionPercent);
        Assert.Single(store.GetTurns(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void Clear_deletes_everything_and_the_next_sample_is_saved_at_once()
    {
        var store = Open();
        store.AddSample(Snapshot(10));
        store.AddTurns([new TurnRecord(Start, "tab-1", null, "fable", 1, 1, 1, 1, 0)]);

        store.Clear();

        Assert.Null(store.GetLatestSample());
        Assert.Empty(store.GetTurns(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.Empty(store.GetTokensByTab(DateTimeOffset.MinValue));
        Assert.True(store.AddSample(Snapshot(10)));
    }

    [Fact]
    public void A_file_that_is_not_a_database_is_kept_aside()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        File.WriteAllText(DatabasePath, string.Concat(Enumerable.Repeat("This is not a database. ", 200)));

        var store = Open();

        Assert.True(store.AddSample(Snapshot(10)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(DatabasePath)!, "usage.db.*.bad"));
    }

    [Fact]
    public void A_database_damaged_past_its_header_is_found_when_opened_and_kept_aside()
    {
        // Opening reads only the header; without a check, every later write would fail and the history quietly stop.
        var store = Open();
        store.AddTurns(Enumerable.Range(0, 400).Select(i => new TurnRecord(Start.AddSeconds(i), $"tab-{i}", "s1", "claude-fable-5", 1, 2, 3, 4, 0.5)));
        store.Dispose();
        _stores.Remove(store);
        using (var file = new FileStream(DatabasePath, FileMode.Open, FileAccess.Write))
        {
            Assert.True(file.Length > 4096 * 3);
            // The page headers of the tables' pages: their structure, not just their contents.
            for (var page = 1; page * 4096 < file.Length; page++)
            {
                file.Position = page * 4096;
                file.Write(Enumerable.Repeat((byte)0xA5, 16).ToArray());
            }
        }

        var reopened = Open();

        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(DatabasePath)!, "usage.db.*.bad"));
        Assert.Empty(reopened.GetTurns(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
        Assert.True(reopened.AddSample(Snapshot(10)));
    }

    [Fact]
    public void A_disposed_store_refuses_calls()
    {
        var store = Open();
        store.Dispose();

        Assert.Throws<ObjectDisposedException>(() => store.GetLatestSample());
    }

    private UsageStore Open()
    {
        var store = new UsageStore(DatabasePath, _time);
        _stores.Add(store);
        return store;
    }

    private UsageSnapshot Snapshot(double session, DateTimeOffset? resetsAt = null, double? weekly = null, double? fable = null) =>
        UsageFixtures.Snapshot(_time.GetUtcNow(), session, resetsAt, weekly, fable);

    private static UsageSample Shared(DateTimeOffset at, double session) => new(at, session, UsageFixtures.SessionResetsAt, 40, UsageFixtures.WeekResetsAt, []);
}
