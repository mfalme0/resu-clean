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