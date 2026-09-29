using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Usage;

/// <summary>
/// The local usage history: a SQLite file in the app data folder (DESIGN.md §6, "Usage history").
/// <list type="bullet">
/// <item>Plan usage samples, app-wide. One is saved only when a value changed, and at most once a minute.</item>
/// <item>Per-turn token records, per tab.</item>
/// </list>
/// No conversation content is ever stored. Synchronous and safe to call from any thread; keep it off the UI thread.
/// Times are stored to the millisecond.
/// </summary>
public sealed class UsageStore : IDisposable
{
    public const int SchemaVersion = 1;
    public static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMinutes(1);

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;
    private const string SelectSamples =
        "SELECT timestamp, session_percent, session_resets_at, weekly_percent, weekly_resets_at, models FROM samples";

    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly SqliteConnection _connection;
    private SampleValues? _lastSaved;
    private DateTimeOffset? _lastWrite;
    private bool _disposed;

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

        if (ReadLatestSample() is { } latest)
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
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (values == _lastSaved)
            {
                return false;
            }
            var now = _time.GetUtcNow();
            if (_lastWrite is { } last && now - last < MinimumSampleInterval)
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
        }
    }

    /// <summary>The newest sample, so the header can show something after a restart.</summary>
    public UsageSample? GetLatestSample()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ReadLatestSample();
        }
    }

    /// <summary>Samples from <paramref name="from"/> to <paramref name="to"/> (both inclusive), oldest first.</summary>
    public IReadOnlyList<UsageSample> GetSamples(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _connection.CreateCommand();
            command.CommandText = $"{SelectSamples} WHERE timestamp >= $from AND timestamp <= $to ORDER BY timestamp, id";
            Add(command, "$from", from.ToUnixTimeMilliseconds());
            Add(command, "$to", to.ToUnixTimeMilliseconds());
            return ReadSamples(command);
        }
    }

    /// <summary>
    /// The highest usage each window reached, for every window in the history that reset by <paramref name="now"/>, newest
    /// first (the Usage panel's past sessions and weeks, DESIGN.md §6). Reset times are compared to the second, since the
    /// two usage sources differ by fractions of one, so they are rounded to it.
    /// </summary>
    public IReadOnlyList<WindowPeak> GetPastWindows(UsageWindow window, DateTimeOffset now)
    {
        var (percent, resetsAt) = window == UsageWindow.Session ? ("session_percent", "session_resets_at") : ("weekly_percent", "weekly_resets_at");
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
        }
    }

    public void AddTurns(IEnumerable<TurnRecord> turns)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO turns (timestamp, tab_id, session_id, model, input, output, cache_write, cache_read, cost_usd)
                VALUES ($timestamp, $tabId, $sessionId, $model, $input, $output, $cacheWrite, $cacheRead, $costUsd)
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
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    /// <summary>Turn records from <paramref name="from"/> to <paramref name="to"/> (both inclusive), oldest first.</summary>
    public IReadOnlyList<TurnRecord> GetTurns(DateTimeOffset from, DateTimeOffset to, string? tabId = null)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT timestamp, tab_id, session_id, model, input, output, cache_write, cache_read, cost_usd FROM turns
                WHERE timestamp >= $from AND timestamp <= $to AND ($tabId IS NULL OR tab_id = $tabId)
                ORDER BY timestamp, id
                """;
            Add(command, "$from", from.ToUnixTimeMilliseconds());
            Add(command, "$to", to.ToUnixTimeMilliseconds());
            Add(command, "$tabId", tabId);
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
                    reader.GetDouble(8)));
            }
            return turns;
        }
    }

    /// <summary>Each tab's tokens since <paramref name="from"/>, the heaviest first.</summary>
    public IReadOnlyList<TabTokenSum> GetTokensByTab(DateTimeOffset from)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var command = _connection.CreateCommand();
            // A turn's records (one per model) share its timestamp.
            command.CommandText = """
                SELECT tab_id, SUM(input), SUM(output), SUM(cache_write), SUM(cache_read), SUM(cost_usd), COUNT(DISTINCT timestamp)
                FROM turns WHERE timestamp >= $from
                GROUP BY tab_id
                ORDER BY SUM(input + output + cache_write + cache_read) DESC, tab_id
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
                    reader.GetInt32(6)));
            }
            return sums;
        }
    }

    /// <summary>
    /// Deletes samples and turn records older than <paramref name="keepFor"/> (DESIGN.md §6, "Retention"). Null keeps
    /// everything. Returns the number of records deleted.
    /// </summary>
    public int Prune(TimeSpan? keepFor)
    {
        if (keepFor is not { } keep)
        {
            return 0;
        }
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var cutoff = (_time.GetUtcNow() - keep).ToUnixTimeMilliseconds();
            using var transaction = _connection.BeginTransaction();
            var deleted = Execute(transaction, "DELETE FROM samples WHERE timestamp < $cutoff", cutoff)
                + Execute(transaction, "DELETE FROM turns WHERE timestamp < $cutoff", cutoff);
            transaction.Commit();
            return deleted;
        }
    }

    /// <summary>Deletes every sample and turn record ("Clear usage history").</summary>
    public void Clear()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using (var transaction = _connection.BeginTransaction())
            {
                Execute(transaction, "DELETE FROM samples", null);
                Execute(transaction, "DELETE FROM turns", null);
                transaction.Commit();
            }
            // Give the space back and leave nothing of the old rows in the file.
            Execute(null, "VACUUM", null);
            _lastSaved = null;
            _lastWrite = null;
        }
    }

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

    private SqliteConnection OpenOrReplace()
    {
        try
        {
            return Open();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            var backup = $"{Path}.{_time.GetUtcNow():yyyyMMddHHmmss}.bad";
            _logger.LogWarning(ex, "Couldn't read the usage history {Path}; keeping it as {Backup} and starting a new one.", Path, backup);
            File.Move(Path, backup, overwrite: true);
            return Open();
        }
    }

    private SqliteConnection Open()
    {
        // No pooling: disposing the store closes the file, so it can be deleted or moved straight away.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt64(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) < SchemaVersion)
            {
                using var transaction = connection.BeginTransaction();
                using var create = connection.CreateCommand();
                create.Transaction = transaction;
                create.CommandText = $"""
                    CREATE TABLE IF NOT EXISTS samples (
                        id INTEGER PRIMARY KEY,
                        timestamp INTEGER NOT NULL,
                        session_percent REAL,
                        session_resets_at INTEGER,
                        weekly_percent REAL,
                        weekly_resets_at INTEGER,
                        models TEXT
                    );
                    CREATE INDEX IF NOT EXISTS samples_by_time ON samples (timestamp);
                    CREATE TABLE IF NOT EXISTS turns (
                        id INTEGER PRIMARY KEY,
                        timestamp INTEGER NOT NULL,
                        tab_id TEXT NOT NULL,
                        session_id TEXT,
                        model TEXT NOT NULL,
                        input INTEGER NOT NULL,
                        output INTEGER NOT NULL,
                        cache_write INTEGER NOT NULL,
                        cache_read INTEGER NOT NULL,
                        cost_usd REAL NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS turns_by_time ON turns (timestamp);
                    CREATE INDEX IF NOT EXISTS turns_by_tab ON turns (tab_id, timestamp);
                    PRAGMA user_version = {SchemaVersion};
                    """;
                create.ExecuteNonQuery();
                transaction.Commit();
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private UsageSample? ReadLatestSample()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"{SelectSamples} ORDER BY timestamp DESC, id DESC LIMIT 1";
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
            return JsonNode.Parse(json) is JsonArray array
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
