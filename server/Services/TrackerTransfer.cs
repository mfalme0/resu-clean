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
        "rejected" => "rejected",
        // Kept as itself rather than folded into rejected: see ApplicationService.NormalizeStatus.
        "closed" => "closed",
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

    public string ExportJson(bool includeAnalysis = true)
    {
        var rows = Db.Query(_db.Open(),
            "SELECT a.id, a.status, a.notes, a.created_at, a.updated_at, COALESCE(a.applied_at,'') AS applied_at, COALESCE(j.raw,'') AS raw, COALESCE(j.company,'') AS company, "
            + "COALESCE(j.title,'') AS title, COALESCE(j.url,'') AS url, COALESCE(j.posted_at,'') AS posted, "
            + "COALESCE(j.match_pct,0) AS match_pct, COALESCE(j.status,'') AS verify_status, "
            + "COALESCE(j.last_verified_at,'') AS verified_at, COALESCE(j.deadline_at,'') AS deadline "
            + "FROM applications a "
            + "LEFT JOIN jobs j ON j.id = a.job_id ORDER BY a.updated_at DESC",
            r => new
            {
                Id = Db.Str(r, "id"),
                Stage = ToStage(Db.Str(r, "status")),
                Note = Db.Str(r, "notes"),
                Company = Db.Str(r, "company"),
                Title = Db.Str(r, "title"),
                Url = Db.Str(r, "url"),
                Posted = Db.Str(r, "posted"),
                MatchPct = Db.Int(r, "match_pct"),
                VerifyStatus = Db.Str(r, "verify_status"),
                VerifiedAt = Db.Str(r, "verified_at"),
                DeadlineAt = Db.Str(r, "deadline"),
                Created = Db.Str(r, "created_at"),
                Applied = Db.Str(r, "applied_at"),
                Raw = Db.Str(r, "raw"),
                Updated = Db.Str(r, "updated_at")
            }, Array.Empty<Microsoft.Data.Sqlite.SqliteParameter>());

        // One entry per company, first one wins. A tracker with two applications to the same company
        // is a mistake the user can fix; silently dropping one in the export would be a worse one.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A typed entry rather than a string map, so the score serialises as a number. A hand-edited
        // file is meant to be readable and writable by a person, and "score": "88" is the kind of
        // quoting that makes a file annoying to edit correctly.
        var payload = new SortedDictionary<string, TrackerExportEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            // A position that came from an import keeps its own key, because keying on the company would
            // collapse four Jumia roles into one entry and silently drop three of them. Jobs added
            // through the tracker itself have no such key and fall back to the company as before.
            var importedKey = ImportKey(row.Raw);
            var key = importedKey.Length > 0 ? importedKey : Slug(row.Company) is { Length: > 0 } c ? c : Slug(row.Title);
            if (key.Length == 0 || !seen.Add(key)) continue;
            var todo = row.Stage == "todo";

            var entry = new TrackerExportEntry
            {
                Stage = row.Stage,
                Note = row.Note,
                // applied_at only. created_at is when the row was written, which for an import is today
                // whatever the file said, so reading it here is how "never applied" became "applied
                // on the day you imported it". Whoever creates the row is what stamps this.
                Applied = todo ? "" : ShortDate(row.Applied),
                Updated = todo ? "" : ShortDate(row.Updated)
            };

            // The analysis tier, and only when asked for. Everything here is something a person
            // established by reading a posting — the gaps they named, the fit they judged, the date
            // they confirmed the role open. Re-deriving any of it after a reinstall would be worse
            // than useless, because the tool would be guessing at a conclusion the user reached.
            if (includeAnalysis)
            {
                var posted = ShortDate(row.Posted);
                var checkedOn = ShortDate(row.VerifiedAt);
                var deadline = ShortDate(row.DeadlineAt);

                if (row.Url.Length > 0) entry.Url = row.Url;
                if (posted.Length > 0) entry.Posted = posted;
                if (row.MatchPct > 0) entry.Score = row.MatchPct;
                if (row.VerifyStatus.Length > 0) entry.Verified = row.VerifyStatus;
                // Only an open verdict carries a date: a check that confirmed nothing has no timestamp.
                if (row.VerifyStatus == "open" && checkedOn.Length > 0) entry.Checked = checkedOn;
                if (deadline.Length > 0) entry.Deadline = deadline;
            }

            payload[key] = entry;
        }

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            // Lowercase, to match the file format this feature already shipped. The export is a
            // file people read and hand-edit, and its keys have always been lowercase; changing that
            // would break every file anyone has already written or scripted against.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Nulls are omitted, not written as null. An absent analysis key means "never
            // determined", and writing "verified": null would make that indistinguishable from a
            // verdict that was checked and found to be nothing.
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        }) + Environment.NewLine;
    }

    /// <summary>ISO date only. The timestamps in the database carry a time nobody tracks by.</summary>
    private static string ShortDate(string iso) =>
        DateTime.TryParse(iso, out var when) ? when.ToString("yyyy-MM-dd") : "";

    /// <summary>
    /// The key a position was imported under, read back out of the job's raw JSON.
    /// Empty when there is none, which is every job the user added themselves.
    /// </summary>
    private static string ImportKey(string raw)
    {
        if (raw.Length == 0) return "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("importKey", out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "")
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

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
                var jobId = ResolveJob(conn, key, company, Str(value, "title"), Str(value, "url"));
                if (jobId is null) { summary.Skipped.Add($"{key}: no job could be created."); continue; }

                // The analysis tier. A posting someone hand-verified carries its verdict with it, so
                // a reinstall cannot quietly downgrade "confirmed open on the 12th" back to a guess.
                // Names that were read off a posting are appended to the note rather than stored
                // separately: they are prose the user wrote, and the note is where prose belongs.
                if (note.Length == 0) note = Str(value, "gaps");
                else if (Str(value, "gaps").Length > 0) note = $"{note}\n\nGaps: {Str(value, "gaps")}";
                ApplyAnalysis(conn, jobId, value, summary, key);

                var existingId = Db.QueryOne(conn,
                    "SELECT id FROM applications WHERE job_id = @j ORDER BY updated_at DESC LIMIT 1",
                    r => Db.Str(r, "id"), Db.P("@j", jobId));

                if (existingId is not null && !overwrite) { summary.Skipped.Add($"{key}: already tracked."); continue; }

                // A todo entry has no applied date, and neither does a file that omits one. Only a
                // stated date is written; created_at is when the row was written, which is a
                // different fact and was previously being read back as an application you never made.
                var appliedAt = DateTime.TryParse(applied, out var a) ? a.ToString("O") : null;
                var created = appliedAt ?? now;
                var touched = DateTime.TryParse(updated, out var u) ? u.ToString("O") : now;

                if (existingId is null)
                {
                    Db.Exec(conn,
                        "INSERT INTO applications (id, job_id, kit_id, version_id, status, notes, applied_at, created_at, updated_at) "
                        + "VALUES (@id,@j,'','',@s,@n,@a,@c,@t)",
                        Db.P("@id", ProfileService.NewId("app")), Db.P("@j", jobId), Db.P("@s", status),
                        Db.P("@n", note), Db.P("@a", appliedAt), Db.P("@c", created), Db.P("@t", touched));
                    summary.Created++;
                }
                else
                {
                    Db.Exec(conn, "UPDATE applications SET status=@s, notes=@n, applied_at=COALESCE(@a, applied_at), created_at=@c, updated_at=@t WHERE id=@id",
                        Db.P("@s", status), Db.P("@n", note), Db.P("@a", appliedAt), Db.P("@c", created),
                        Db.P("@t", touched), Db.P("@id", existingId));
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
    private static string? ResolveJob(
        Microsoft.Data.Sqlite.SqliteConnection conn, string key, string company, string title = "", string url = "")
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

        // The role title and posting URL come from the analysis tier when the file carries them, so
        // a restored tracker shows what the role actually was instead of repeating the company name.
        var role = title.Length > 0 ? title : company;
        var link = url.Length > 0 ? url : "import-" + id;

        Db.Exec(conn,
            "INSERT OR IGNORE INTO jobs (id, title, company, location, url, posted_at, description, source_id, "
            + "source_name, match_pct, remote, is_manual, best_version_id, raw, created_at) "
            + "VALUES (@i,@t,@c,'',@u,NULL,'','imported','import',0,0,1,'','',@n)",
            Db.P("@i", id), Db.P("@t", role), Db.P("@c", company), Db.P("@u", link), Db.P("@n", now));

        return Db.QueryOne(conn, "SELECT id FROM jobs WHERE id = @i", r => Db.Str(r, "id"), Db.P("@i", id));
    }

    /// <summary>
    /// Restores the verification verdict, the closing date and the score from an exported file.
    ///
    /// A recorded verdict is written as-is and never re-derived. That is the whole point: the person
    /// who read the posting already checked it, and a tool that recomputed the answer would be
    /// guessing at a conclusion somebody reached by looking.
    ///
    /// An absent field leaves the existing value alone rather than clearing it, so importing an
    /// older file — one written before the analysis tier existed — cannot erase a verdict this tool
    /// already holds.
    /// </summary>
    private static void ApplyAnalysis(
        Microsoft.Data.Sqlite.SqliteConnection conn, string jobId, JsonElement entry,
        TrackerImportSummary summary, string key)
    {
        var score = Int(entry, "score");
        if (score > 0)
            Db.Exec(conn, "UPDATE jobs SET match_pct = @s WHERE id = @id", Db.P("@s", score), Db.P("@id", jobId));

        var deadline = Str(entry, "deadline");
        if (deadline.Length > 0)
            Db.Exec(conn, "UPDATE jobs SET deadline_at = @d WHERE id = @id", Db.P("@d", deadline), Db.P("@id", jobId));

        var verdict = Str(entry, "verified").ToLowerInvariant();
        if (verdict.Length == 0) return;

        if (verdict is not ("open" or "closed" or "unverifiable"))
        {
            summary.Skipped.Add($"{key}: unknown verification verdict \"{verdict}\" ignored.");
            return;
        }

        // Only an open verdict carries a date: "we checked and could not tell" has no timestamp,
        // because it did not confirm anything.
        var checkedAt = verdict == "open" ? Str(entry, "checked") : "";
        if (checkedAt.Length > 0 && !DateTime.TryParse(checkedAt, out _)) checkedAt = "";

        Db.Exec(conn,
            "UPDATE jobs SET status = @st, last_verified_at = COALESCE(NULLIF(@lv, ''), last_verified_at) WHERE id = @id",
            Db.P("@st", verdict), Db.P("@lv", checkedAt), Db.P("@id", jobId));
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "")
            : "";

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : 0;
}

/// <summary>
/// One line per company in the exported file.
///
/// Stage, note and dates are always written. The rest is the analysis tier: optional, and omitted
/// entirely when it was never established. An absent key means "never determined", which is
/// different from a present key holding an empty value, and the distinction is the point — an
/// unverified posting must not look like one somebody checked and found nothing.
/// </summary>
public sealed class TrackerExportEntry
{
    public string Stage { get; set; } = "todo";
    public string Note { get; set; } = "";
    public string Applied { get; set; } = "";
    public string Updated { get; set; } = "";

    // ---- Analysis tier. Null means never established. ----

    /// <summary>The posting itself, so re-verifying is one click rather than a search.</summary>
    public string? Url { get; set; }
    public string? Posted { get; set; }
    public int? Score { get; set; }
    /// <summary>open | closed | unverifiable, as established by a person reading the posting.</summary>
    public string? Verified { get; set; }
    /// <summary>When it was confirmed open. Null when nothing was confirmed.</summary>
    public string? Checked { get; set; }
    public string? Deadline { get; set; }
    /// <summary>Named gaps, folded into the note on import because they are prose, not a field.</summary>
    public string? Gaps { get; set; }
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
