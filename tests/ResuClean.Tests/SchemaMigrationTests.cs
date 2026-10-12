using Microsoft.Data.Sqlite;
using ResuClean.Data;
using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// The one upgrade path that cannot be checked by starting from nothing.
///
/// Every table is created with CREATE TABLE IF NOT EXISTS, so a brand new database is always
/// correct and every test passes. That is exactly what makes it dangerous: an existing database
/// silently keeps the shape it was created with, and a column added since then is simply not
/// there. The failure is not at startup, it is when that query first runs.
///
/// So this builds a database the way an older release would have, and asks the questions a user
/// with real data would hit first.
/// </summary>
public class SchemaMigrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "resu-clean-tests", Guid.NewGuid().ToString("N")[..10]);

    private string DbPath => Path.Combine(_dir, "resu-clean.db");

    public SchemaMigrationTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// The schema as it stood before phase five: the same tables minus the five this phase added.
    /// Any future release that adds a column should extend this, which is the point of the test.
    /// </summary>
    private const string LegacySchema = """
        CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS resumes (
          id TEXT PRIMARY KEY, label TEXT NOT NULL, created_at TEXT NOT NULL,
          updated_at TEXT NOT NULL, current_version_id TEXT);
        CREATE TABLE IF NOT EXISTS resume_versions (
          id TEXT PRIMARY KEY, resume_id TEXT NOT NULL REFERENCES resumes(id) ON DELETE CASCADE,
          label TEXT NOT NULL, text TEXT NOT NULL, source_kind TEXT NOT NULL, parent_id TEXT,
          best_fit INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL,
          docx_path TEXT, pdf_path TEXT);
        CREATE TABLE IF NOT EXISTS profile (
          id INTEGER PRIMARY KEY CHECK (id = 1), voice TEXT NOT NULL DEFAULT '',
          preferences TEXT NOT NULL DEFAULT '', contact TEXT NOT NULL DEFAULT '{}', updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS profile_facts (
          id TEXT PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL, meta TEXT NOT NULL DEFAULT '{}',
          origin TEXT NOT NULL DEFAULT 'manual', match_key TEXT NOT NULL, verified_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS pending_facts (
          id TEXT PRIMARY KEY, kind TEXT NOT NULL, text TEXT NOT NULL, meta TEXT NOT NULL DEFAULT '{}',
          origin TEXT NOT NULL DEFAULT 'manual', context TEXT NOT NULL DEFAULT '', match_key TEXT NOT NULL,
          status TEXT NOT NULL DEFAULT 'pending', created_at TEXT NOT NULL, decided_at TEXT);
        CREATE TABLE IF NOT EXISTS job_sources (
          id TEXT PRIMARY KEY, name TEXT NOT NULL, type TEXT NOT NULL, url TEXT, url_template TEXT,
          regions TEXT NOT NULL DEFAULT '[]', enabled INTEGER NOT NULL DEFAULT 1,
          rate_limit_ms INTEGER NOT NULL DEFAULT 2000, field_map TEXT NOT NULL DEFAULT '{}',
          selectors TEXT NOT NULL DEFAULT '{}', requires_key TEXT NOT NULL DEFAULT '',
          notes TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS source_packs (
          id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT NOT NULL DEFAULT '',
          installed_at TEXT);
        CREATE TABLE IF NOT EXISTS jobs (
          id TEXT PRIMARY KEY, title TEXT NOT NULL, company TEXT NOT NULL DEFAULT '',
          location TEXT NOT NULL DEFAULT '', url TEXT NOT NULL DEFAULT '', posted_at TEXT,
          description TEXT NOT NULL DEFAULT '', source_id TEXT, source_name TEXT NOT NULL DEFAULT '',
          match_pct INTEGER NOT NULL DEFAULT 0, remote INTEGER NOT NULL DEFAULT 0,
          is_manual INTEGER NOT NULL DEFAULT 0, best_version_id TEXT,
          raw TEXT NOT NULL DEFAULT '{}', created_at TEXT NOT NULL, UNIQUE(url, title, company));
        CREATE TABLE IF NOT EXISTS search_runs (
          id TEXT PRIMARY KEY, query TEXT NOT NULL, location TEXT NOT NULL DEFAULT '',
          remote_only INTEGER NOT NULL DEFAULT 0, version_id TEXT, created_at TEXT NOT NULL,
          stats TEXT NOT NULL DEFAULT '{}');
        CREATE TABLE IF NOT EXISTS kit_emails (
          id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
          version_id TEXT NOT NULL, subject TEXT NOT NULL, body TEXT NOT NULL,
          cover_letter TEXT NOT NULL DEFAULT '', to_address TEXT NOT NULL DEFAULT '',
          apply_url TEXT NOT NULL DEFAULT '', generated_by TEXT NOT NULL DEFAULT 'template',
          created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS applications (
          id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
          kit_id TEXT, version_id TEXT NOT NULL DEFAULT '', status TEXT NOT NULL DEFAULT 'prepared',
          notes TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS providers (
          id TEXT PRIMARY KEY, name TEXT NOT NULL, type TEXT NOT NULL, base_url TEXT NOT NULL,
          env_var TEXT NOT NULL DEFAULT '', api_key_enc TEXT NOT NULL DEFAULT '',
          models TEXT NOT NULL DEFAULT '[]', enabled INTEGER NOT NULL DEFAULT 1, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS routes (
          workflow TEXT NOT NULL, ordinal INTEGER NOT NULL,
          provider_id TEXT NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
          model TEXT NOT NULL, PRIMARY KEY (workflow, ordinal));
        CREATE TABLE IF NOT EXISTS ops (
          id TEXT PRIMARY KEY, kind TEXT NOT NULL, state TEXT NOT NULL, payload TEXT NOT NULL DEFAULT '{}',
          result TEXT NOT NULL DEFAULT '', error TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL,
          finished_at TEXT);
        CREATE TABLE IF NOT EXISTS http_cache (
          url TEXT PRIMARY KEY, body TEXT NOT NULL, status INTEGER NOT NULL, fetched_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS model_cache (
          key TEXT PRIMARY KEY, workflow TEXT NOT NULL, provider_id TEXT NOT NULL DEFAULT '',
          model TEXT NOT NULL DEFAULT '', response TEXT NOT NULL, created_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS model_usage (
          id TEXT PRIMARY KEY, workflow TEXT NOT NULL, provider_id TEXT NOT NULL DEFAULT '',
          provider_name TEXT NOT NULL DEFAULT '', model TEXT NOT NULL DEFAULT '',
          prompt_tokens INTEGER NOT NULL DEFAULT 0, completion_tokens INTEGER NOT NULL DEFAULT 0,
          cached INTEGER NOT NULL DEFAULT 0, elapsed_ms INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL);
        """;

    private void SeedLegacyDatabase()
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = LegacySchema;
        cmd.ExecuteNonQuery();

        // Data a real user would already have: a resume, a verified fact, a saved job.
        using var seed = conn.CreateCommand();
        seed.CommandText = """
            INSERT INTO resumes (id, label, created_at, updated_at, current_version_id)
            VALUES ('res_1', 'My resume', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z', 'ver_1');
            INSERT INTO resume_versions (id, resume_id, label, text, source_kind, best_fit, created_at)
            VALUES ('ver_1', 'res_1', 'Original', 'JANE DOE' || char(10) || '- Reduced costs 20%.', 'paste', 1, '2026-01-01T00:00:00Z');
            INSERT INTO profile_facts (id, kind, text, meta, origin, match_key, verified_at)
            VALUES ('fact_1', 'achievement', 'Cut reporting time from 9 days to 2', '{}', 'manual', 'cut-reporting', '2026-01-01T00:00:00Z');
            INSERT INTO jobs (id, title, company, location, url, description, source_name, created_at)
            VALUES ('job_1', 'Data Engineer', 'Acme', 'Nairobi', 'https://example.org/1',
                    'Must have Terraform and Kubernetes experience.', 'manual', '2026-01-01T00:00:00Z');
            """;
        seed.ExecuteNonQuery();
    }

    /// <summary>Boots the real services against the seeded directory, exactly as the app would.</summary>
    private TestHost Boot() => new(dataDir: _dir, ownsDataDir: false);

    /// <summary>
    /// The failure CREATE TABLE IF NOT EXISTS cannot catch, and the reason this class exists.
    ///
    /// A new column in Schema.sql is applied only to a database that does not yet have the table.
    /// Every other test in the suite starts from nothing, so they all pass while an existing user's
    /// database is silently missing the column — and nothing fails until the query that reads it
    /// runs, on their machine, with their data in it. There is no startup error to notice.
    ///
    /// So: seed the old shape, boot, and ask for the column as a user would.
    /// </summary>
    [Fact]
    public void A_column_added_since_the_last_release_reaches_an_existing_database()
    {
        SeedLegacyDatabase();
        using var host = Boot();

        // Written against the current schema on purpose. If a future release adds a column and
        // forgets the migration, this is the assertion that fails.
        var deadline = host.Jobs.UpdateVerification("job_1", lastVerifiedAt: "2026-10-12T09:00:00Z",
            deadlineAt: "2026-10-30T23:59:59Z", status: "open");

        Assert.Equal("2026-10-12T09:00:00Z", deadline.LastVerifiedAt);
        Assert.Equal("2026-10-30T23:59:59Z", deadline.DeadlineAt);

        // And it must survive a read, not just a write.
        Assert.Equal("2026-10-12T09:00:00Z", host.Jobs.GetJob("job_1")?.LastVerifiedAt);
    }

    [Fact]
    public void A_posting_saved_before_this_release_reads_as_never_verified()
    {
        SeedLegacyDatabase();
        using var host = Boot();

        // NULL is the honest value and must not be papered over with a creation date. "We applied
        // to this" and "this was open when we checked" are different facts.
        Assert.Null(host.Jobs.GetJob("job_1")?.LastVerifiedAt);
    }

    [Fact]
    public void An_existing_database_is_upgraded_rather_than_recreated()
    {
        SeedLegacyDatabase();
        using var host = Boot();

        // The phase-five tables did not exist before; applying the schema must add them in place.
        using var conn = host.Db.Open();
        foreach (var table in new[] { "rewrite_evaluations", "fact_check_reports", "job_requirements", "skill_dictionary" })
        {
            Assert.NotNull(Db.Scalar(conn,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name = @t",
                Db.P("@t", table)));
        }
    }

    [Fact]
    public void The_data_that_was_already_there_survives_the_upgrade()
    {
        SeedLegacyDatabase();
        using var host = Boot();

        // A schema that drops or recreates a table would take the user's work with it. This is the
        // assertion that makes "apply the schema at startup" safe rather than merely idempotent.
        Assert.NotNull(host.Store.Get("res_1"));
        Assert.Equal(1, host.Profile.ListFacts().Count);
        Assert.NotNull(host.Jobs.GetJob("job_1"));
    }

    [Fact]
    public void A_resume_written_before_the_upgrade_still_tails_and_cleans()
    {
        SeedLegacyDatabase();
        using var host = Boot();
        var text = host.Store.GetText("ver_1");

        Assert.NotNull(text);
        Assert.NotNull(AtsScorer.Score("v", text!, null));
        // The keyword dictionary is process-wide state, so an upgraded database has to work with
        // the same dictionary a fresh one does.
        var posting = Keywords.Extract("Must have Terraform and Kubernetes experience.");
        Assert.Contains("terraform", posting);
        Assert.Contains("kubernetes", posting);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* a leftover temp directory is not worth failing a run over */ }
    }
}