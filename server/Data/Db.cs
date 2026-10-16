using Microsoft.Data.Sqlite;

namespace ResuClean.Data;

/// <summary>
/// Thin SQLite access layer. One short-lived pooled connection per operation, WAL journaling,
/// no ORM and no change tracking, so the working set stays small.
/// </summary>
public sealed class Db
{
    private readonly string _connectionString;
    private readonly string _schemaPath;

    public Db(Configuration.AppConfig app)
    {
        Directory.CreateDirectory(app.DataDir);
        var dbPath = Path.Combine(app.DataDir, "resu-clean.db");

        _schemaPath = Path.Combine(AppContext.BaseDirectory, "Data", "Schema.sql");
        if (!File.Exists(_schemaPath)) throw new FileNotFoundException("Data/Schema.sql was not copied to the output directory.", _schemaPath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            DefaultTimeout = 15
        }.ToString();

        using var conn = Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        Exec(conn, "PRAGMA synchronous=NORMAL;");
        Exec(conn, "PRAGMA foreign_keys=ON;");
        Exec(conn, "PRAGMA temp_store=MEMORY;");
        Exec(conn, "PRAGMA mmap_size=0;");
        Exec(conn, "PRAGMA busy_timeout=15000;");
        ApplySchema(conn);
        ApplyMigrations(conn);
    }

    /// <summary>
    /// Column-level migrations, for databases that already exist.
    ///
    /// CREATE TABLE IF NOT EXISTS is enough while every release only adds tables: a table that is
    /// absent gets created, and one that is present is left alone. It is not enough the moment a
    /// release adds a *column*. An existing database keeps the shape it was created with, so the
    /// CREATE silently does nothing and the query that reads the new column fails at runtime, on a
    /// user's machine, with their data in it. There is no error at startup to warn anyone.
    ///
    /// So each column added after the first release gets one line here, and adding a column without
    /// one is a bug that only an upgrading user would find. Idempotent, and cheap: a PRAGMA per
    /// column at startup, against a local file.
    ///
    /// SQLite's ALTER TABLE ADD COLUMN is the only migration primitive available and it is enough,
    /// because every column added here is nullable or has a constant default.
    /// </summary>
    private static readonly (string Table, string Column, string Definition)[] ColumnMigrations =
    {
        // Phase five: verification and closing dates. Both nullable, because a posting saved before
        // this release was never verified and states no deadline, and NULL is the honest value.
        ("jobs", "last_verified_at", "TEXT"),
        ("jobs", "deadline_at", "TEXT"),
        ("jobs", "status", "TEXT NOT NULL DEFAULT ''"),
        // Where a source's postings come from. Unknown by default on purpose: a source we cannot
        // place is not an employer's own system, and pretending otherwise is what made an
        // aggregator look like evidence.
        ("job_sources", "provenance", "TEXT NOT NULL DEFAULT 'unknown'"),
        // Separate from created_at, which is always "when this row was written". Without this an
        // imported position you never applied to reports today as the application date.
        ("applications", "applied_at", "TEXT"),
        // When the verification verdict last changed, so a daily digest can report what changed
        // rather than only what was confirmed open. last_verified_at deliberately does not move on
        // a failed check, so it cannot answer that question on its own.
        ("jobs", "status_changed_at", "TEXT")
    };

    private static void ApplyMigrations(SqliteConnection conn)
    {
        foreach (var (table, column, definition) in ColumnMigrations)
        {
            if (HasColumn(conn, table, column)) continue;
            try
            {
                Exec(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
            }
            catch (SqliteException ex)
            {
                // A duplicate-column race (two processes starting at once) is not a failure worth
                // stopping for. Anything else is: it means the database is not what we think, and
                // carrying on would corrupt data later rather than now.
                if (!ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) throw;
            }
        }
    }

    private static bool HasColumn(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public string DatabasePath { get; private set; } = "";

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void ApplySchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = File.ReadAllText(_schemaPath);
        cmd.CommandTimeout = 30;
        cmd.ExecuteNonQuery();
    }

    public static void Exec(SqliteConnection conn, string sql, params SqliteParameter[] parameters) =>
        Execute(conn, sql, parameters);

    public static int Execute(SqliteConnection conn, string sql, params SqliteParameter[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var parameter in parameters) cmd.Parameters.Add(parameter);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection conn, string sql, params SqliteParameter[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var parameter in parameters) cmd.Parameters.Add(parameter);
        return cmd.ExecuteScalar();
    }

    public static List<T> Query<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map, params SqliteParameter[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var parameter in parameters) cmd.Parameters.Add(parameter);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    public static T? QueryOne<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map, params SqliteParameter[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var parameter in parameters) cmd.Parameters.Add(parameter);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? map(reader) : default;
    }

    public static SqliteParameter P(string name, object? value) => new(name, value ?? DBNull.Value);

    /// <summary>Clears the pooled connections. Called on shutdown and between test runs.</summary>
    public void Dispose() => SqliteConnection.ClearAllPools();

    public static string Str(SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? "" : r.GetString(r.GetOrdinal(column));

    public static string? StrOrNull(SqliteDataReader r, string column) =>
        r.IsDBNull(r.GetOrdinal(column)) ? null : r.GetString(r.GetOrdinal(column));

    public static int Int(SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? 0 : r.GetInt32(r.GetOrdinal(column));

    public static long Long(SqliteDataReader r, string column) => r.IsDBNull(r.GetOrdinal(column)) ? 0 : r.GetInt64(r.GetOrdinal(column));

    public static bool Bool(SqliteDataReader r, string column) => Int(r, column) != 0;
}