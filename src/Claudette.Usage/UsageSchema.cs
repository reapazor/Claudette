using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Claudette.Usage;

/// <summary>
/// The usage history's SQLite file (DESIGN.md §6, "Usage history"): opening it, creating its tables or bringing an older
/// file's up to <see cref="Version"/>, and checking it isn't damaged.
/// </summary>
internal static class UsageSchema
{
    public const int Version = 4;

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    /// <summary>The file is damaged, or isn't a database at all.</summary>
    public static bool IsCorrupt(SqliteException ex) => ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase;

    /// <summary>
    /// Opens the file at <paramref name="path"/>, creating it and its tables or upgrading them, and checks all of it. A
    /// damaged file throws a <see cref="SqliteException"/> that <see cref="IsCorrupt"/> recognizes.
    /// </summary>
    public static SqliteConnection Open(string path)
    {
        // No pooling: disposing the store closes the file, so it can be deleted or moved straight away.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            Migrate(connection);
            Check(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Creates the tables of a new file, and adds what later versions added to an older one.</summary>
    private static void Migrate(SqliteConnection connection)
    {
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        if (Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture) >= Version)
        {
            return;
        }
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
            CREATE TABLE IF NOT EXISTS tabs (
                tab_id TEXT PRIMARY KEY,
                name TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS imports (
                machine TEXT PRIMARY KEY,
                through INTEGER NOT NULL
            );
            """;
        create.ExecuteNonQuery();
        // Version 3: the machine a shared sample came from; null for this machine's own.
        if (!HasColumn(connection, transaction, "samples", "machine"))
        {
            create.CommandText = "ALTER TABLE samples ADD COLUMN machine TEXT";
            create.ExecuteNonQuery();
        }
        // Version 4: the tab's folder, for usage by project; null for turns recorded before.
        if (!HasColumn(connection, transaction, "turns", "project"))
        {
            create.CommandText = "ALTER TABLE turns ADD COLUMN project TEXT";
            create.ExecuteNonQuery();
        }
        create.CommandText = $"PRAGMA user_version = {Version}";
        create.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Reads the whole file. Opening reads only the header: damage further in would fail every later write instead. The
    /// history is small (it's pruned), so checking all of it once is quick.
    /// </summary>
    private static void Check(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check";
        if (check.ExecuteScalar() is not "ok" and var result)
        {
            throw new SqliteException($"The usage history is damaged: {result}", SqliteCorrupt);
        }
    }

    private static bool HasColumn(SqliteConnection connection, SqliteTransaction transaction, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $column";
        command.Parameters.AddWithValue("$column", column);
        return command.ExecuteScalar() is not null;
    }
}
