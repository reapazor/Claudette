using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Usage;

/// <summary>
/// The local usage history: a SQLite file in the app data folder (DESIGN.md §6, "Usage history").
/// <list type="bullet">
/// <item>Plan usage samples, app-wide. One is saved only when a value changed, and at most once a minute.</item>
/// <item>Samples other machines shared through the session library, imported with the name of the machine they came
/// from (DESIGN.md §6, "Sharing across machines").</item>
/// <item>Per-turn token records, per tab, and each tab's last known name.</item>
/// </list>
/// No conversation content is ever stored. Synchronous and safe to call from any thread; keep it off the UI thread.
/// Times are stored to the millisecond.
/// </summary>
public sealed class UsageStore : IDisposable
{
    public const int SchemaVersion = UsageSchema.Version;

    /// <summary>The <c>imports</c> row that holds back every machine's samples up to a time: the last Clear.</summary>
    private const string EveryMachine = "*";
    public static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMinutes(1);

    private const string SelectSamples =
        "SELECT timestamp, session_percent, session_resets_at, weekly_percent, weekly_resets_at, models FROM samples";

    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private SqliteConnection _connection;
    private SampleValues? _lastSaved;
    private DateTimeOffset? _lastWrite;
    private bool _disposed;

    /// <summary>How far ahead of this machine's clock another machine's sample may be and still be imported.</summary>
    public static readonly TimeSpan FutureAllowance = TimeSpan.FromMinutes(5);

    /// <summary>Opens the file, creating it, its folder and the schema as needed.</summary>
    /// <remarks>A file that isn't a readable database is kept aside as <c>*.bad</c> and a new one is started.</remarks>
    public UsageStore(string databasePath, TimeProvider time, ILogger? logger = null)
    {
        Path = System.IO.Path.GetFullPath(databasePath);
        _time = time;
        _logger = logger ?? NullLogger.Instance;
        if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }
        _connection = OpenOrReplace();

        if (ReadLatestSample(ownOnly: true) is { } latest)
        {
            _lastSaved = SampleValues.From(latest);
            _lastWrite = latest.Timestamp;
        }
    }

    public string Path { get; }

    /// <summary>
    /// Saves the snapshot's session, weekly and model-specific readings if any of them changed since the last saved
    /// sample and a minute has passed since then. A change inside the minute is held: the next call after the minute
    /// writes it, if the values still differ. Returns whether a sample was written.
    /// </summary>
    public bool AddSample(UsageSnapshot snapshot)
    {
        if (snapshot.Limits.Count == 0)
        {
            return false;
        }
        var values = SampleValues.From(snapshot);
        return Locked(() =>
        {
            if (values == _lastSaved)
            {
                return false;
            }
            var now = _time.GetUtcNow();
            // A last write in the future means the clock was set back since: don't wait for it to catch up.
            if (_lastWrite is { } last && now >= last && now - last < MinimumSampleInterval)
            {
                return false;
            }

            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO samples (timestamp, session_percent, session_resets_at, weekly_percent, weekly_resets_at, models)
                VALUES ($timestamp, $sessionPercent, $sessionResetsAt, $weeklyPercent, $weeklyResetsAt, $models)
                """;
            Add(command, "$timestamp", now.ToUnixTimeMilliseconds());
            Add(command, "$sessionPercent", values.SessionPercent);
            Add(command, "$sessionResetsAt", values.SessionResetsAt);
            Add(command, "$weeklyPercent", values.WeeklyPercent);
            Add(command, "$weeklyResetsAt", values.WeeklyResetsAt);
            Add(command, "$models", values.Models);
            command.ExecuteNonQuery();
            _lastSaved = values;
            _lastWrite = now;
            return true;
        });
    }

    /// <summary>The newest sample, from this machine or another, so the header can show something after a restart.</summary>
    public UsageSample? GetLatestSample() => Locked(() => ReadLatestSample(ownOnly: false));

    /// <summary>This machine's own samples since <paramref name="from"/>, oldest first: what it shares, never another's.</summary>
    public IReadOnlyList<UsageSample> GetOwnSamples(DateTimeOffset from) => Locked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"{SelectSamples} WHERE machine IS NULL AND timestamp >= $from ORDER BY timestamp, id";
        Add(command, "$from", from.ToUnixTimeMilliseconds());
        return ReadSamples(command);
    });

    /// <summary>
    /// Adds the samples another machine shared that are newer than any imported from it before, and newer than the last
    /// Clear, so each is imported once and cleared history doesn't come back. Returns how many were added.
    /// </summary>
    public int ImportSamples(string machine, IEnumerable<UsageSample> samples)
    {
        ArgumentException.ThrowIfNullOrEmpty(machine);
        return Locked(() =>
        {
            using var transaction = _connection.BeginTransaction();
            using var watermark = _connection.CreateCommand();
            watermark.Transaction = transaction;
            watermark.CommandText = "SELECT MAX(through) FROM imports WHERE machine IN ($machine, $every)";
            Add(watermark, "$machine", machine);
            Add(watermark, "$every", EveryMachine);
            var through = watermark.ExecuteScalar() is long stored ? stored : long.MinValue;

            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO samples (timestamp, session_percent, session_resets_at, weekly_percent, weekly_resets_at, models, machine)
                VALUES ($timestamp, $sessionPercent, $sessionResetsAt, $weeklyPercent, $weeklyResetsAt, $models, $machine)
                """;
            var added = 0;
            var newest = through;
            // A machine whose clock is ahead shares samples from the future. Imported, they'd move its mark ahead and
            // stand as the newest reading; left for now, they come in once their time has come.
            var latest = (_time.GetUtcNow() + FutureAllowance).ToUnixTimeMilliseconds();
            foreach (var sample in samples)
            {
                var timestamp = sample.Timestamp.ToUnixTimeMilliseconds();
                if (timestamp <= through || timestamp > latest)
                {
                    continue;
                }
                var values = SampleValues.From(sample);
                insert.Parameters.Clear();
                Add(insert, "$timestamp", timestamp);
                Add(insert, "$sessionPercent", values.SessionPercent);
                Add(insert, "$sessionResetsAt", values.SessionResetsAt);
                Add(insert, "$weeklyPercent", values.WeeklyPercent);
                Add(insert, "$weeklyResetsAt", values.WeeklyResetsAt);
                Add(insert, "$models", values.Models);
                Add(insert, "$machine", machine);
                insert.ExecuteNonQuery();
                added++;
                newest = Math.Max(newest, timestamp);
            }
            if (newest > through)
            {
                using var mark = _connection.CreateCommand();
                mark.Transaction = transaction;
                mark.CommandText = "INSERT INTO imports (machine, through) VALUES ($machine, $through) ON CONFLICT (machine) DO UPDATE SET through = excluded.through";
                Add(mark, "$machine", machine);
                Add(mark, "$through", newest);
                mark.ExecuteNonQuery();
            }
            transaction.Commit();
            return added;
        });
    }

    /// <summary>Samples from <paramref name="from"/> to <paramref name="to"/> (both inclusive), oldest first.</summary>
    public IReadOnlyList<UsageSample> GetSamples(DateTimeOffset from, DateTimeOffset to) => Locked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"{SelectSamples} WHERE timestamp >= $from AND timestamp <= $to ORDER BY timestamp, id";
        Add(command, "$from", from.ToUnixTimeMilliseconds());
        Add(command, "$to", to.ToUnixTimeMilliseconds());
        return ReadSamples(command);
    });

    /// <summary>
    /// The highest usage each window reached, for every window in the history that reset by <paramref name="now"/>, newest
    /// first (the Usage panel's past sessions and weeks, DESIGN.md §6). Reset times are compared to the second, since the
    /// two usage sources differ by fractions of one, so they are rounded to it.
    /// </summary>
    public IReadOnlyList<WindowPeak> GetPastWindows(UsageWindow window, DateTimeOffset now)
    {
        var (percent, resetsAt) = window == UsageWindow.Session ? ("session_percent", "session_resets_at") : ("weekly_percent", "weekly_resets_at");
        return Locked(() =>
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"""
                SELECT ({resetsAt} + 500) / 1000, MAX({percent}) FROM samples
                WHERE {resetsAt} IS NOT NULL AND {percent} IS NOT NULL AND {resetsAt} <= $now
                GROUP BY ({resetsAt} + 500) / 1000
                ORDER BY 1 DESC
                """;
            Add(command, "$now", now.ToUnixTimeMilliseconds());
            using var reader = command.ExecuteReader();
            var peaks = new List<WindowPeak>();
            while (reader.Read())
            {
                peaks.Add(new WindowPeak(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetDouble(1)));
            }
            return peaks;
        });
    }

    public void AddTurns(IEnumerable<TurnRecord> turns) => Locked(() =>
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO turns (timestamp, tab_id, session_id, model, input, output, cache_write, cache_read, cost_usd, project)
            VALUES ($timestamp, $tabId, $sessionId, $model, $input, $output, $cacheWrite, $cacheRead, $costUsd, $project)
            """;
        foreach (var turn in turns)
        {
            command.Parameters.Clear();
            Add(command, "$timestamp", turn.Timestamp.ToUnixTimeMilliseconds());
            Add(command, "$tabId", turn.TabId);
            Add(command, "$sessionId", turn.SessionId);
            Add(command, "$model", turn.Model);
            Add(command, "$input", turn.Input);
            Add(command, "$output", turn.Output);
            Add(command, "$cacheWrite", turn.CacheWrite);
            Add(command, "$cacheRead", turn.CacheRead);
            Add(command, "$costUsd", turn.CostUsd);
            Add(command, "$project", turn.Project);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    });

    /// <summary>Turn records from <paramref name="from"/> to <paramref name="to"/> (both inclusive), oldest first.</summary>
    public IReadOnlyList<TurnRecord> GetTurns(DateTimeOffset from, DateTimeOffset to, string? tabId = null) => Locked(() =>
    {
        using var command = _connection.CreateCommand();
        // Two queries rather than "$tabId IS NULL OR tab_id = $tabId", which SQLite can't answer from the
        // tab's index (turns_by_tab), so it would read every turn in the range.
        command.CommandText = tabId is null
            ? """
              SELECT timestamp, tab_id, session_id, model, input, output, cache_write, cache_read, cost_usd, project FROM turns
              WHERE timestamp >= $from AND timestamp <= $to
              ORDER BY timestamp, id
              """
            : """
              SELECT timestamp, tab_id, session_id, model, input, output, cache_write, cache_read, cost_usd, project FROM turns
              WHERE tab_id = $tabId AND timestamp >= $from AND timestamp <= $to
              ORDER BY timestamp, id
              """;
        Add(command, "$from", from.ToUnixTimeMilliseconds());
        Add(command, "$to", to.ToUnixTimeMilliseconds());
        if (tabId is not null)
        {
            Add(command, "$tabId", tabId);
        }
        using var reader = command.ExecuteReader();
        var turns = new List<TurnRecord>();
        while (reader.Read())
        {
            turns.Add(new TurnRecord(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return turns;
    });

    /// <summary>
    /// Keeps <paramref name="name"/> as the tab's name, so its turns are still named once it's closed. Nothing is stored
    /// for a tab without turn records.
    /// </summary>
    public void SetTabName(string tabId, string name) => Locked(() =>
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tabs (tab_id, name) SELECT $tabId, $name WHERE EXISTS (SELECT 1 FROM turns WHERE tab_id = $tabId)
            ON CONFLICT (tab_id) DO UPDATE SET name = excluded.name
            """;
        Add(command, "$tabId", tabId);
        Add(command, "$name", name);
        command.ExecuteNonQuery();
    });

    /// <summary>Tokens per project since <paramref name="from"/>, the heaviest first; turns without a project together.</summary>
    public IReadOnlyList<ProjectTokenSum> GetTokensByProject(DateTimeOffset from) => Locked(() =>
    {
        using var command = _connection.CreateCommand();
        // A turn's records (one per model) share its timestamp and tab.
        command.CommandText = """
            SELECT project, SUM(input), SUM(output), SUM(cache_write), SUM(cache_read), SUM(cost_usd), COUNT(DISTINCT tab_id || '|' || timestamp)
            FROM turns
            WHERE timestamp >= $from
            GROUP BY project
            ORDER BY SUM(input + output + cache_write + cache_read) DESC, project
            """;
        Add(command, "$from", from.ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        var sums = new List<ProjectTokenSum>();
        while (reader.Read())
        {
            sums.Add(new ProjectTokenSum(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetDouble(5),
                reader.GetInt32(6)));
        }
        return sums;
    });

    /// <summary>Each tab's tokens since <paramref name="from"/>, the heaviest first, with its last known name.</summary>
    public IReadOnlyList<TabTokenSum> GetTokensByTab(DateTimeOffset from) => Locked(() =>
    {
        using var command = _connection.CreateCommand();
        // A turn's records (one per model) share its timestamp.
        command.CommandText = """
            SELECT turns.tab_id, SUM(input), SUM(output), SUM(cache_write), SUM(cache_read), SUM(cost_usd), COUNT(DISTINCT timestamp), tabs.name
            FROM turns LEFT JOIN tabs ON tabs.tab_id = turns.tab_id
            WHERE timestamp >= $from
            GROUP BY turns.tab_id
            ORDER BY SUM(input + output + cache_write + cache_read) DESC, turns.tab_id
            """;
        Add(command, "$from", from.ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        var sums = new List<TabTokenSum>();
        while (reader.Read())
        {
            sums.Add(new TabTokenSum(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetDouble(5),
                reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return sums;
    });

    /// <summary>
    /// Deletes samples and turn records older than <paramref name="keepFor"/> (DESIGN.md §6, "Retention"), and the names
    /// of tabs with no turns left. Null keeps everything. Returns the number of samples and turn records deleted.
    /// </summary>
    public int Prune(TimeSpan? keepFor)
    {
        if (keepFor is not { } keep)
        {
            return 0;
        }
        return Locked(() =>
        {
            var cutoff = (_time.GetUtcNow() - keep).ToUnixTimeMilliseconds();
            using var transaction = _connection.BeginTransaction();
            var deleted = Execute(transaction, "DELETE FROM samples WHERE timestamp < $cutoff", cutoff)
                + Execute(transaction, "DELETE FROM turns WHERE timestamp < $cutoff", cutoff);
            Execute(transaction, "DELETE FROM tabs WHERE tab_id NOT IN (SELECT tab_id FROM turns)", null);
            transaction.Commit();
            return deleted;
        });
    }

    /// <summary>
    /// Deletes every sample, turn record and tab name ("Clear usage history"). Samples other machines shared up to now
    /// aren't imported again.
    /// </summary>
    public void Clear() => Locked(() =>
    {
        using (var transaction = _connection.BeginTransaction())
        {
            Execute(transaction, "DELETE FROM samples", null);
            Execute(transaction, "DELETE FROM turns", null);
            Execute(transaction, "DELETE FROM tabs", null);
            Execute(transaction, "DELETE FROM imports", null);
            using var floor = _connection.CreateCommand();
            floor.Transaction = transaction;
            floor.CommandText = "INSERT INTO imports (machine, through) VALUES ($every, $now)";
            Add(floor, "$every", EveryMachine);
            Add(floor, "$now", _time.GetUtcNow().ToUnixTimeMilliseconds());
            floor.ExecuteNonQuery();
            transaction.Commit();
        }
        // Give the space back and leave nothing of the old rows in the file.
        Execute(null, "VACUUM", null);
        _lastSaved = null;
        _lastWrite = null;
    });

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _connection.Dispose();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> under the lock, on a store that isn't disposed. A damaged file found on the way is kept
    /// aside and a new one started (<see cref="StartAfresh"/>); the call that found it still fails.
    /// </summary>
    private T Locked<T>(Func<T> work)
    {
        lock (_lock)
        {
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return work();
            }
            catch (SqliteException ex) when (UsageSchema.IsCorrupt(ex))
            {
                StartAfresh(ex);
                throw;
            }
        }
    }

    /// <inheritdoc cref="Locked{T}"/>
    private void Locked(Action work) => Locked<object?>(() =>
    {
        work();
        return null;
    });

    private SqliteConnection OpenOrReplace()
    {
        try
        {
            return UsageSchema.Open(Path);
        }
        catch (SqliteException ex) when (UsageSchema.IsCorrupt(ex))
        {
            SetAside(ex);
            return UsageSchema.Open(Path);
        }
    }

    /// <summary>
    /// The file turned out damaged after it was opened: every later write would fail too, and the history would quietly
    /// stop. Keep it aside and carry on with a new one; the operation that found it still fails. Called under the lock.
    /// </summary>
    private void StartAfresh(SqliteException ex)
    {
        _connection.Dispose();
        SetAside(ex);
        _connection = UsageSchema.Open(Path);
        _lastSaved = default;
        _lastWrite = null;
    }

    /// <summary>Keeps a damaged file as <c>*.bad</c>, with its journal and write-ahead log, so none of them is rolled into the new one.</summary>
    private void SetAside(SqliteException ex)
    {
        var backup = $"{Path}.{_time.GetUtcNow():yyyyMMddHHmmss}.bad";
        _logger.LogWarning(ex, "Couldn't read the usage history {Path}; keeping it as {Backup} and starting a new one.", Path, backup);
        File.Move(Path, backup, overwrite: true);
        foreach (var suffix in (string[])["-journal", "-wal", "-shm"])
        {
            if (File.Exists(Path + suffix))
            {
                File.Move(Path + suffix, backup + suffix, overwrite: true);
            }
        }
    }

    private UsageSample? ReadLatestSample(bool ownOnly)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"{SelectSamples} {(ownOnly ? "WHERE machine IS NULL " : "")}ORDER BY timestamp DESC, id DESC LIMIT 1";
        return ReadSamples(command) is [var latest] ? latest : null;
    }

    private static List<UsageSample> ReadSamples(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var samples = new List<UsageSample>();
        while (reader.Read())
        {
            samples.Add(new UsageSample(
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)),
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                reader.IsDBNull(5) ? [] : ModelsFromJson(reader.GetString(5))));
        }
        return samples;
    }

    private int Execute(SqliteTransaction? transaction, string sql, long? cutoff)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (cutoff is not null)
        {
            Add(command, "$cutoff", cutoff);
        }
        return command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? ModelsToJson(IReadOnlyList<LimitReading> models)
    {
        if (models.Count == 0)
        {
            return null;
        }
        var array = new JsonArray();
        foreach (var model in models)
        {
            array.Add(new JsonObject
            {
                ["label"] = model.Label,
                ["percent"] = model.Percent,
                ["resetsAt"] = model.ResetsAt?.ToUnixTimeMilliseconds(),
            });
        }
        return array.ToJsonString();
    }

    private static IReadOnlyList<ModelSample> ModelsFromJson(string json)
    {
        try
        {
            return JsonTree.Parse(json) is JsonArray array
                ? [.. array.OfType<JsonObject>()
                    .Where(m => m.GetString("label") is not null && m.GetDouble("percent") is not null)
                    .Select(m => new ModelSample(
                        m.GetString("label")!,
                        m.GetDouble("percent")!.Value,
                        m.GetDouble("resetsAt") is { } resetsAt ? DateTimeOffset.FromUnixTimeMilliseconds((long)resetsAt) : null))]
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>What a sample holds, in stored form, to tell whether anything changed.</summary>
    private sealed record SampleValues(double? SessionPercent, long? SessionResetsAt, double? WeeklyPercent, long? WeeklyResetsAt, string? Models)
    {
        public static SampleValues From(UsageSnapshot snapshot) => new(
            snapshot.Session?.Percent,
            snapshot.Session?.ResetsAt?.ToUnixTimeMilliseconds(),
            snapshot.WeeklyAll?.Percent,
            snapshot.WeeklyAll?.ResetsAt?.ToUnixTimeMilliseconds(),
            ModelsToJson(snapshot.WeeklyModels));

        public static SampleValues From(UsageSample sample) => From(sample.ToSnapshot());
    }
}
