using System.Text.Json;
using System.Text.RegularExpressions;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Imports job positions tracked somewhere else, given as one JSON file.
///
/// The format is deliberately the same shape this tool exports — a key per position, each with a
/// stage, a note and two dates — because it came from a tracker someone else maintained and the
/// point is to move it, not to re-key it. But the two are not the same data, and treating them as
/// interchangeable corrupts a record:
///
///   - the existing import keys on *company*, so four Jumia roles collapse into one entry
///   - a "closed" posting became "rejected", which is a different claim about what happened
///   - an empty "applied" date was backfilled with today, inventing an application you never made
///
/// So this reads the same shape and resolves it per position, keeping the key as the identity.
///
/// The note is where the real content is, and most of the value here is getting it out:
/// "Jumia - Senior NodeJS Software Engineer (Nairobi). Node/Express. https://…" carries a company,
/// a title, a location and a posting URL that the flat import threw away, leaving a job you cannot
/// re-verify. That is the whole point of the verification work, so it matters here too.
///
/// Every extraction is narrow and every failure falls back rather than guessing. The note is always
/// preserved verbatim on the application, so nothing is lost even when a field cannot be read.
/// </summary>
public sealed partial class PositionImport
{
    private readonly Db _db;
    private readonly JobSearchService _jobs;

    public PositionImport(Db db, JobSearchService jobs)
    {
        _db = db;
        _jobs = jobs;
    }

    /// <summary>
    /// "Jumia - Senior NodeJS Software Engineer (Nairobi). Node/Express" gives all three.
    /// Anchored so it only fires on the shape it recognises, and never on a bare status note.
    /// </summary>
    [GeneratedRegex(
        @"^\s*(?<company>[^-\r\n]{2,60}?)\s+-\s+(?<title>[^.\r\n]{3,120}?)\s*\((?<location>[^)\r\n]{2,60})\)",
        RegexOptions.None, 2000)]
    private static partial Regex NoteHeader();

    /// <summary>The last URL in a note, which is the posting. A trailing full stop is not part of it.</summary>
    [GeneratedRegex(@"https?://[^\s<>\""']+", RegexOptions.None, 2000)]
    private static partial Regex Url();

    /// <summary>
    /// A verification verdict written into the note, as in "12 Oct verification: OPEN, EMEA".
    /// Anchored on the word "verification" so an ordinary sentence about verifying something does
    /// not set a posting's status.
    /// </summary>
    [GeneratedRegex(@"(?i)\bverification\s*[:\-]\s*(open|closed|unverifiable)\b", RegexOptions.None, 2000)]
    private static partial Regex VerificationNote();

    /// <summary>One position as read from the file, before anything is written.</summary>
    public sealed record ParsedPosition(
        string Key,
        string Stage,
        string Note,
        string? Company,
        string? Title,
        string? Location,
        string? Url,
        string? DeadlineAt,
        string? Verified,
        string Applied,
        string Updated,
        /// <summary>What we could not read out of the note, so the user can fill it in by hand.</summary>
        IReadOnlyList<string> Missing);

    public sealed record Preview(
        int Total,
        IReadOnlyList<ParsedPosition> Positions,
        IReadOnlyList<string> Warnings);

    /// <summary>Counts for one import run. Settable, because it is built up as rows are written.</summary>
    public sealed class Result
    {
        public int Imported { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public List<string> Skipped { get; } = new();
        public IReadOnlyList<string> Warnings { get; }
        public Result(IReadOnlyList<string> warnings) => Warnings = warnings;
    }

    /// <summary>Reads the file and works out what each position would become. Writes nothing.</summary>
    public Preview Parse(string json)
    {
        var root = ParseObject(json);
        var warnings = new List<string>();
        var positions = new List<ParsedPosition>();

        foreach (var (key, value) in root)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"{key}: not an object, skipped.");
                continue;
            }

            var note = Str(value, "note");
            var header = NoteHeader().Match(note);
            var url = Url().Matches(note).LastOrDefault()?.Value.TrimEnd('.', ',', ')');
            var deadline = DeadlineReader.Read(note);
            var verified = VerificationNote().Match(note);

            var missing = new List<string>();
            if (!header.Success) missing.Add("title");
            if (string.IsNullOrEmpty(url)) missing.Add("posting URL");

            positions.Add(new ParsedPosition(
                key,
                Stage(Str(value, "stage")),
                note,
                header.Success ? Clean(header.Groups["company"].Value) : null,
                header.Success ? Clean(header.Groups["title"].Value) : null,
                header.Success ? Clean(header.Groups["location"].Value) : null,
                string.IsNullOrEmpty(url) ? null : url,
                deadline,
                verified.Success ? verified.Groups[1].Value.ToLowerInvariant() : null,
                Str(value, "applied"),
                Str(value, "updated"),
                missing));
        }

        return new Preview(root.Count, positions, warnings);
    }

    /// <summary>
    /// Writes the positions: one job each, one application each.
    ///
    /// Re-running is safe. A job already carrying one of these keys is updated rather than
    /// duplicated, which is what makes this safe to run against a file you keep editing.
    /// </summary>
    public Result Import(string json, bool overwrite)
    {
        var preview = Parse(json);
        var result = new Result(preview.Warnings);
        var now = DateTime.UtcNow.ToString("O");

        using var conn = _db.Open();
        var tx = conn.BeginTransaction();
        try
        {
            foreach (var position in preview.Positions)
            {
                var jobId = ResolveJob(conn, position, overwrite);
                if (jobId is null)
                {
                    result.Skipped.Add($"{position.Key}: no job could be created.");
                    continue;
                }

                var existingId = Db.QueryOne(conn,
                    "SELECT id FROM applications WHERE job_id = @j ORDER BY updated_at DESC LIMIT 1",
                    r => Db.Str(r, "id"), Db.P("@j", jobId));

                // An empty applied date means "not applied", and stays empty. created_at is when this row
                // was written, which is today for an import, and reading that back as an
                // application date invents one you never made.
                var appliedAt = DateOf(position.Applied);
                var created = appliedAt ?? now;
                var touched = DateOf(position.Updated) ?? created;

                if (existingId is null)
                {
                    Db.Exec(conn,
                        "INSERT INTO applications (id, job_id, kit_id, version_id, status, notes, applied_at, created_at, updated_at) "
                        + "VALUES (@id,@j,'','',@s,@n,@a,@c,@t)",
                        Db.P("@id", ProfileService.NewId("app")), Db.P("@j", jobId), Db.P("@s", position.Stage),
                        Db.P("@n", position.Note), Db.P("@a", appliedAt), Db.P("@c", created), Db.P("@t", touched));
                    result.Created++;
                }
                else
                {
                    if (!overwrite) { result.Skipped.Add($"{position.Key}: already tracked."); continue; }
                    Db.Exec(conn,
                        "UPDATE applications SET status=@s, notes=@n, applied_at=COALESCE(@a, applied_at), updated_at=@t WHERE id=@id",
                        Db.P("@s", position.Stage), Db.P("@n", position.Note), Db.P("@a", appliedAt),
                        Db.P("@t", touched), Db.P("@id", existingId));
                    result.Updated++;
                }

                result.Imported++;
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return result;
    }

    /// <summary>
    /// Finds the job this position refers to, or creates one.
    ///
    /// Identity is the import key, stored in the job's existing raw JSON so no migration is needed.
    /// Matching on the key rather than the company is what keeps four Jumia roles as four roles.
    /// </summary>
    private string? ResolveJob(Microsoft.Data.Sqlite.SqliteConnection conn, ParsedPosition position, bool overwrite)
    {
        var marker = "\"importKey\":" + JsonSerializer.Serialize(position.Key);

        var existing = Db.QueryOne(conn,
            "SELECT id FROM jobs WHERE raw LIKE @m ORDER BY created_at DESC LIMIT 1",
            r => Db.Str(r, "id"), Db.P("@m", "%" + marker + "%"));

        if (existing is not null)
        {
            // Never blank a field we did not read from the note on a re-import. A position with no
            // URL in its note must not erase the URL you added by hand last time.
            Db.Exec(conn,
                "UPDATE jobs SET title = COALESCE(NULLIF(@t,''), title), company = COALESCE(NULLIF(@c,''), company), "
                + "location = COALESCE(NULLIF(@l,''), location), url = COALESCE(NULLIF(@u,''), url), "
                + "description = COALESCE(NULLIF(@d,''), description), deadline_at = COALESCE(@dl, deadline_at) "
                + "WHERE id = @id",
                Db.P("@t", position.Title ?? ""), Db.P("@c", position.Company ?? ""), Db.P("@l", position.Location ?? ""),
                Db.P("@u", position.Url ?? ""), Db.P("@d", position.Note), Db.P("@dl", position.DeadlineAt),
                Db.P("@id", existing));

            ApplyVerification(conn, existing, position);
            return existing;
        }

        var now = DateTime.UtcNow.ToString("O");
        var id = ProfileService.NewId("job");

        // The title falls back to the key, humanised. "canonical_data_infra" gives "Canonical Data
        // Infra", which is the user's own label for the position and therefore not an invention.
        var title = position.Title ?? Humanise(position.Key);
        var company = position.Company ?? TitleCase(position.Key.Split('_')[0]);
        var raw = "{\"importKey\":" + JsonSerializer.Serialize(position.Key) + "}";

        Db.Exec(conn,
            "INSERT OR IGNORE INTO jobs (id, title, company, location, url, posted_at, description, source_id, "
            + "source_name, match_pct, remote, is_manual, best_version_id, deadline_at, raw, created_at) "
            + "VALUES (@i,@t,@c,@l,@u,NULL,@d,'imported','import',0,0,1,'',@dl,@raw,@n)",
            Db.P("@i", id), Db.P("@t", title), Db.P("@c", company), Db.P("@l", position.Location ?? ""),
            Db.P("@u", position.Url ?? ""), Db.P("@d", position.Note), Db.P("@dl", position.DeadlineAt),
            Db.P("@raw", raw), Db.P("@n", now));

        var createdId = Db.QueryOne(conn, "SELECT id FROM jobs WHERE raw LIKE @m ORDER BY created_at DESC LIMIT 1",
            r => Db.Str(r, "id"), Db.P("@m", "%" + marker + "%"));

        ApplyVerification(conn, createdId ?? id, position);
        return createdId ?? id;
    }

    /// <summary>
    /// Records a verdict the note states, and only then.
    ///
    /// "12 Oct verification: OPEN" is something a person established by looking, so it is kept. An
    /// absent one leaves the stored status alone rather than resetting it, so importing an older
    /// file cannot undo a verification you have since done properly.
    /// </summary>
    private static void ApplyVerification(Microsoft.Data.Sqlite.SqliteConnection conn, string jobId, ParsedPosition position)
    {
        if (position.Verified is null) return;

        Db.Exec(conn,
            "UPDATE jobs SET status = @s, last_verified_at = COALESCE(NULLIF(@lv,''), last_verified_at) WHERE id = @id",
            // Only "open" carries a date: a check that confirmed nothing has no timestamp, and
            // inventing one would claim a check that never happened.
            Db.P("@s", position.Verified),
            Db.P("@lv", position.Verified == "open" ? DateOf(position.Updated) ?? "" : ""),
            Db.P("@id", jobId));
    }

    /// <summary>Maps the file's stage words onto the tracker's, keeping "closed" as itself.</summary>
    private static string Stage(string stage) => stage.Trim().ToLowerInvariant() switch
    {
        "" or "todo" or "prepared" => "prepared",
        "applied" or "sent" or "submitted" => "sent",
        "replied" or "response" => "replied",
        "interview" or "interviewed" => "interview",
        "rejected" => "rejected",
        "closed" => "closed",
        _ => "prepared"
    };

    private static Dictionary<string, JsonElement> ParseObject(string json)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"That is not valid JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("The file must be an object: one key per position.");

        var entries = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject()) entries[property.Name] = property.Value;
        return entries;
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    /// <summary>An ISO date only when one is really there. Empty stays empty.</summary>
    private static string? DateOf(string value) =>
        DateTime.TryParse(value, out var when) ? when.ToString("O") : null;

    private static string Clean(string value) => value.Trim().Trim('"', '\'', '.');

    private static string Humanise(string key) =>
        string.Join(" ", key.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(TitleCase));

    private static string TitleCase(string word)
    {
        if (word.Length == 0) return word;
        // Acronyms keep their caps. "canonical_sre" otherwise becomes "Canonical Sre", which reads
        // as a typo in a tool that otherwise takes your record seriously.
        if (Acronyms.Contains(word)) return word.ToUpperInvariant();
        return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
    }

    /// <summary>
    /// Short role words that are acronyms. Drawn from what appears in engineering role titles rather
    /// than a general dictionary, because this only ever runs against position keys.
    /// </summary>
    private static readonly HashSet<string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "ai","ml","sre","sre","qa","qe","hr","pr","api","apis","aws","gcp","azure","devops","erp","crm","bi",
        "ios","ui","ux","fe","be","fullstack","fsd","net","sec","ops","db","dba","sql","nosql","etl","ci","cd",
        "mvc","rest","grpc","llm","nlp","seo","sem","crm","b2b","b2c","saas","paas","iac","sre","oncall","nix"
    };
}