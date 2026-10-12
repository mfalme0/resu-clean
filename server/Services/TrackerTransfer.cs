using System.Text;
using System.Text.Json;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Moves the application tracker in and out as one small JSON file.
///
/// The file is keyed by a slug of the company and carries only what a person actually tracks by
/// hand: which stage it is at, a note, when you applied and when it last changed. Deliberately
/// narrow — no resume text, no job descriptions, no URLs, no kit bodies. It is meant to be
/// readable, diffable and safe to keep in a notes file.
///
/// The round trip is lossless. Both vocabularies are mapped in both directions, so exporting a
/// tracker this tool wrote and importing it again reproduces the same stages and dates.
/// </summary>
public sealed class TrackerTransfer
{
    private readonly Db _db;
    private readonly JobSearchService _jobs;

    public TrackerTransfer(Db db, JobSearchService jobs)
    {
        _db = db;
        _jobs = jobs;
    }

    /// <summary>
    /// Internal status -> the short word this file format uses. "prepared" means you have not sent
    /// anything, and "sent" means you sent it yourself, so they read as todo and applied.
    /// </summary>
    private static string ToStage(string status) => status switch
    {
        "prepared" => "todo",
        "sent" => "applied",
        _ => status
    };

    private static string FromStage(string? stage) => (stage ?? "").Trim().ToLowerInvariant() switch
    {
        "todo" or "" => "prepared",
        "applied" or "sent" or "submitted" => "sent",
        "replied" or "response" => "replied",
        "interview" or "interviewed" => "interview",
        "rejected" or "closed" => "rejected",
        _ => "prepared"
    };

    /// <summary>Lowercase, non-alphanumerics collapsed to underscores: "Mkopa Ltd" and "mkopa" agree.</summary>
    public static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var slug = sb.ToString().Trim('_');
        while (slug.Contains("__")) slug = slug.Replace("__", "_");
        return slug;
    }

    /// <summary>Turns a slug back into something a human would recognise: "tatu_ict" -> "Tatu Ict".</summary>
    private static string Humanise(string slug) =>
        string.Join(" ", slug.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    public string ExportJson()
    {
        var rows = Db.Query(_db.Open(),
            "SELECT a.id, a.status, a.notes, a.created_at, a.updated_at, COALESCE(j.company,'') AS company, "
            + "COALESCE(j.title,'') AS title FROM applications a "
            + "LEFT JOIN jobs j ON j.id = a.job_id ORDER BY a.updated_at DESC",
            r => new
            {
                Id = Db.Str(r, "id"),
                Stage = ToStage(Db.Str(r, "status")),
                Note = Db.Str(r, "notes"),
                Company = Db.Str(r, "company"),
                Title = Db.Str(r, "title"),
                Created = Db.Str(r, "created_at"),
                Updated = Db.Str(r, "updated_at")
            }, Array.Empty<Microsoft.Data.Sqlite.SqliteParameter>());

        // One entry per company, first one wins. A tracker with two applications to the same company
        // is a mistake the user can fix; silently dropping one in the export would be a worse one.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payload = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var key = Slug(row.Company) is { Length: > 0 } c ? c : Slug(row.Title);
            if (key.Length == 0 || !seen.Add(key)) continue;
            var todo = row.Stage == "todo";
            payload[key] = new Dictionary<string, string>
            {
                ["stage"] = row.Stage,
                ["note"] = row.Note,
                ["applied"] = todo ? "" : ShortDate(row.Created),
                ["updated"] = todo ? "" : ShortDate(row.Updated)
            };
        }

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;


    }

    /// <summary>ISO date only. The timestamps in the database carry a time nobody tracks by.</summary>
    private static string ShortDate(string iso) =>
        DateTime.TryParse(iso, out var when) ? when.ToString("yyyy-MM-dd") : "";

    public TrackerImportSummary ImportJson(string json, bool overwrite)
    {
        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"That is not valid JSON: {ex.Message}");
        }
        if (parsed is null || parsed.Count == 0)
            throw new ArgumentException("The file has no entries in it.");

        var now = DateTime.UtcNow.ToString("O");
        var summary = new TrackerImportSummary();

        using var conn = _db.Open();
        var tx = conn.BeginTransaction();
        try
        {
            foreach (var (key, value) in parsed)
            {
                if (value.ValueKind != JsonValueKind.Object) { summary.Skipped.Add($"{key}: not an object."); continue; }

                var stage = FromStage(Str(value, "stage"));
                var note = Str(value, "note");
                var applied = Str(value, "applied");
                var updated = Str(value, "updated");
                var status = stage;
                var company = Humanise(key);
                var jobId = ResolveJob(conn, key, company);
                if (jobId is null) { summary.Skipped.Add($"{key}: no job could be created."); continue; }

                var existingId = Db.QueryOne(conn,
                    "SELECT id FROM applications WHERE job_id = @j ORDER BY updated_at DESC LIMIT 1",
                    r => Db.Str(r, "id"), Db.P("@j", jobId));

                if (existingId is not null && !overwrite) { summary.Skipped.Add($"{key}: already tracked."); continue; }

                // A todo entry has no applied date; a row that was already sent keeps its real one.
                var created = DateTime.TryParse(applied, out var a) ? a.ToString("O") : now;
                var touched = DateTime.TryParse(updated, out var u) ? u.ToString("O") : now;

                if (existingId is null)
                {
                    Db.Exec(conn,
                        "INSERT INTO applications (id, job_id, kit_id, version_id, status, notes, created_at, updated_at) "
                        + "VALUES (@id,@j,'','',@s,@n,@c,@t)",
                        Db.P("@id", ProfileService.NewId("app")), Db.P("@j", jobId), Db.P("@s", status),
                        Db.P("@n", note), Db.P("@c", created), Db.P("@t", touched));
                    summary.Created++;
                }
                else
                {
                    Db.Exec(conn, "UPDATE applications SET status=@s, notes=@n, created_at=@c, updated_at=@t WHERE id=@id",
                        Db.P("@s", status), Db.P("@n", note), Db.P("@c", created), Db.P("@t", touched),
                        Db.P("@id", existingId));
                    summary.Updated++;
                }
                summary.Imported++;
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return summary;
    }

    /// <summary>
    /// Finds the job this entry belongs to, or makes one. The file format carries a company slug and
    /// nothing else, so a new entry becomes a manual job with the slug as its title — which is the
    /// honest amount of information available, and the user can fill the rest in afterwards.
    ///
    /// Matching goes through the same <see cref="Slug"/> the export uses. Reproducing it in SQL would
    /// be two implementations of one rule, and the two would drift on the first punctuation mark.
    /// </summary>
    private static string? ResolveJob(Microsoft.Data.Sqlite.SqliteConnection conn, string key, string company)
    {
        var candidates = Db.Query(conn, "SELECT id, company, title FROM jobs",
            r => (Id: Db.Str(r, "id"), Company: Db.Str(r, "company"), Title: Db.Str(r, "title")),
            Array.Empty<Microsoft.Data.Sqlite.SqliteParameter>());

        foreach (var candidate in candidates)
        {
            if (string.Equals(Slug(candidate.Company), key, StringComparison.OrdinalIgnoreCase)) return candidate.Id;
            if (string.Equals(Slug(candidate.Title), key, StringComparison.OrdinalIgnoreCase)) return candidate.Id;
        }

        var now = DateTime.UtcNow.ToString("O");
        var id = ProfileService.NewId("job");
        Db.Exec(conn,
            "INSERT OR IGNORE INTO jobs (id, title, company, location, url, posted_at, description, source_id, "
            + "source_name, match_pct, remote, is_manual, best_version_id, raw, created_at) "
            + "VALUES (@i,@t,@c,'','import-' || @i,NULL,'','imported','import',0,0,1,'','',@n)",
            Db.P("@i", id), Db.P("@t", company), Db.P("@c", company), Db.P("@n", now));

        return Db.QueryOne(conn, "SELECT id FROM jobs WHERE id = @i", r => Db.Str(r, "id"), Db.P("@i", id));
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";
}

/// <summary>Counts and reasons for one import run.</summary>
public sealed class TrackerImportSummary
{
    public int Imported { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    /// <summary>Entries left alone, each with the reason. Shown rather than swallowed.</summary>
    public List<string> Skipped { get; } = new();
}
