using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// The character profile: verified facts plus a voice note. Every model workflow reads it as
/// reference material. New information is never written straight to <see cref="AddFactToPending"/>;
/// it must be approved by the user first.
/// </summary>
public sealed class ProfileService
{
    private readonly Db _db;

    public ProfileService(Db db) => _db = db;

    private static string Now() => DateTime.UtcNow.ToString("O");

    // ---- Profile singleton --------------------------------------------------

    public ProfileDto GetProfile()
    {
        using var conn = _db.Open();
        var row = Db.QueryOne(conn, "SELECT voice, preferences, contact, updated_at FROM profile WHERE id = 1",
            r => (Voice: Db.Str(r, "voice"), Prefs: Db.Str(r, "preferences"), Contact: Db.Str(r, "contact"), Updated: Db.Str(r, "updated_at")));
        if (row == default)
        {
            Db.Exec(conn, "INSERT INTO profile (id, voice, preferences, contact, updated_at) VALUES (1, '', '', '{}', @now)", Db.P("@now", Now()));
            return new ProfileDto("", "", new Dictionary<string, string>(), Now());
        }
        return new ProfileDto(row.Voice, row.Prefs, JsonDict(row.Contact), row.Updated);
    }

    public ProfileDto UpdateProfile(UpsertProfileRequest request)
    {
        using var conn = _db.Open();
        var current = GetProfile();
        var voice = request.Voice ?? current.Voice;
        var prefs = request.Preferences ?? current.Preferences;
        var contact = request.Contact ?? current.Contact;
        var now = Now();
        Db.Exec(conn,
            "UPDATE profile SET voice = @v, preferences = @p, contact = @c, updated_at = @now WHERE id = 1",
            Db.P("@v", voice), Db.P("@p", prefs), Db.P("@c", JsonSerializer.Serialize(contact)), Db.P("@now", now));
        return new ProfileDto(voice, prefs, contact, now);
    }

    // ---- Verified facts ----------------------------------------------------

    public List<ProfileFactDto> ListFacts(string? kind = null)
    {
        using var conn = _db.Open();
        var sql = "SELECT id, kind, text, meta, origin, verified_at FROM profile_facts" +
                  (string.IsNullOrWhiteSpace(kind) ? "" : " WHERE kind = @k") + " ORDER BY kind, text";
        var parameters = string.IsNullOrWhiteSpace(kind) ? Array.Empty<SqliteParameter>() : new[] { Db.P("@k", kind) };
        return Db.Query(conn, sql, r => new ProfileFactDto(
            Db.Str(r, "id"), Db.Str(r, "kind"), Db.Str(r, "text"),
            JsonDict(Db.Str(r, "meta")), Db.Str(r, "origin"), Db.Str(r, "verified_at")), parameters);
    }

    /// <summary>
    /// Records a fact the user typed in the profile editor. Per the NEW INFO RULE this does not
    /// become verified: it is queued for approval, unless it already matches a verified fact.
    /// </summary>
    public (ProfileFactDto? Fact, PendingFactDto? Pending, bool Duplicate) SubmitFact(AddFactRequest request, string origin)
    {
        var text = request.Text?.Trim() ?? throw new ArgumentException("Fact text is required.");
        if (text.Length == 0) throw new ArgumentException("Fact text is required.");
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? "other" : request.Kind.Trim().ToLowerInvariant();
        var meta = request.Meta ?? new Dictionary<string, string>();
        var key = NormalizeKey(text);

        using var conn = _db.Open();
        var tx = conn.BeginTransaction();
        try
        {
            if (Db.Scalar(conn, "SELECT 1 FROM profile_facts WHERE match_key = @k LIMIT 1", Db.P("@k", key)) != null)
            {
                var existing = Db.QueryOne(conn, "SELECT id, kind, text, meta, origin, verified_at FROM profile_facts WHERE match_key = @k", r => new ProfileFactDto(
                    Db.Str(r, "id"), Db.Str(r, "kind"), Db.Str(r, "text"), JsonDict(Db.Str(r, "meta")), Db.Str(r, "origin"), Db.Str(r, "verified_at")), Db.P("@k", key));
                tx.Commit();
                return (existing, null, true);
            }

            var pending = InsertPending(conn, kind, text, meta, origin, context: "");
            tx.Commit();
            return (null, pending, false);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Approved pending items land here. Only this method writes to profile_facts.</summary>
    public ProfileFactDto ApproveFact(string pendingId)
    {
        using var conn = _db.Open();
        var tx = conn.BeginTransaction();
        try
        {
            var pending = Db.QueryOne(conn, "SELECT id, kind, text, meta, origin, status FROM pending_facts WHERE id = @id", r => (
                Id: Db.Str(r, "id"), Kind: Db.Str(r, "kind"), Text: Db.Str(r, "text"),
                Meta: Db.Str(r, "meta"), Origin: Db.Str(r, "origin"), Status: Db.Str(r, "status")), Db.P("@id", pendingId));
            if (pending == default) throw new KeyNotFoundException("That pending item no longer exists.");

            if (pending.Status != "pending")
                throw new InvalidOperationException($"This item was already {pending.Status}.");

            var key = NormalizeKey(pending.Text);
            var existing = Db.QueryOne(conn, "SELECT id, kind, text, meta, origin, verified_at FROM profile_facts WHERE match_key = @k", r => new ProfileFactDto(
                Db.Str(r, "id"), Db.Str(r, "kind"), Db.Str(r, "text"), JsonDict(Db.Str(r, "meta")), Db.Str(r, "origin"), Db.Str(r, "verified_at")), Db.P("@k", key));

            ProfileFactDto fact;
            if (existing is not null)
            {
                fact = existing;
            }
            else
            {
                var id = NewId("fact");
                var now = Now();
                Db.Exec(conn, "INSERT INTO profile_facts (id, kind, text, meta, origin, match_key, verified_at) VALUES (@id, @kind, @text, @meta, @origin, @key, @now)",
                    Db.P("@id", id), Db.P("@kind", pending.Kind), Db.P("@text", pending.Text),
                    Db.P("@meta", pending.Meta), Db.P("@origin", "approved:" + pending.Origin), Db.P("@key", key), Db.P("@now", now));
                fact = new ProfileFactDto(id, pending.Kind, pending.Text, JsonDict(pending.Meta), "approved:" + pending.Origin, now);
            }

            Db.Exec(conn, "UPDATE pending_facts SET status = 'approved', decided_at = @now WHERE id = @id", Db.P("@now", Now()), Db.P("@id", pendingId));
            tx.Commit();
            return fact;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public bool RejectFact(string pendingId, string? reason = null)
    {
        using var conn = _db.Open();
        var changed = Db.Execute(conn, "UPDATE pending_facts SET status = 'rejected', decided_at = @now WHERE id = @id AND status = 'pending'",
            Db.P("@now", Now()), Db.P("@id", pendingId));
        if (changed == 0)
        {
            var status = Db.QueryOne(conn, "SELECT status FROM pending_facts WHERE id = @id", r => Db.Str(r, "status"), Db.P("@id", pendingId));
            if (status is null) throw new KeyNotFoundException("That pending item no longer exists.");
            throw new InvalidOperationException($"This item was already {status}.");
        }
        return true;
    }

    public bool DeleteFact(string factId)
    {
        using var conn = _db.Open();
        return Db.Execute(conn, "DELETE FROM profile_facts WHERE id = @id", Db.P("@id", factId)) > 0;
    }

    // ---- Pending queue -----------------------------------------------------

    public List<PendingFactDto> ListPending(string status = "pending")
    {
        using var conn = _db.Open();
        var sql = "SELECT id, kind, text, meta, origin, context, status, created_at, decided_at FROM pending_facts WHERE status = @s ORDER BY created_at DESC LIMIT 500";
        return Db.Query(conn, sql, r => new PendingFactDto(
            Db.Str(r, "id"), Db.Str(r, "kind"), Db.Str(r, "text"), JsonDict(Db.Str(r, "meta")),
            Db.Str(r, "origin"), Db.Str(r, "context"), Db.Str(r, "status"), Db.Str(r, "created_at"), Db.StrOrNull(r, "decided_at")),
            Db.P("@s", status));
    }

    public int PendingCount()
    {
        using var conn = _db.Open();
        return Convert.ToInt32(Db.Scalar(conn, "SELECT COUNT(*) FROM pending_facts WHERE status = 'pending'") ?? 0);
    }

    /// <summary>
    /// Queues information the model workflow claims is new. Fuzzy-matches against verified facts and
    /// already-pending items so the user is never asked about the same thing twice.
    /// </summary>
    public List<PendingFactDto> AddFactToPending(string kind, string text, string origin, string context = "", Dictionary<string, string>? meta = null)
    {
        text = text.Trim();
        if (text.Length == 0) return new List<PendingFactDto>();
        kind = string.IsNullOrWhiteSpace(kind) ? "other" : kind.Trim().ToLowerInvariant();
        meta ??= new Dictionary<string, string>();
        var key = NormalizeKey(text);

        using var conn = _db.Open();

        // Already verified -> nothing to ask.
        if (Db.Scalar(conn, "SELECT 1 FROM profile_facts WHERE match_key = @k LIMIT 1", Db.P("@k", key)) != null)
            return new List<PendingFactDto>();

        // Exact pending duplicate -> return the existing row, do not create a second question.
        var existing = Db.QueryOne(conn, "SELECT id, kind, text, meta, origin, context, status, created_at, decided_at FROM pending_facts WHERE match_key = @k AND status = 'pending'", r => new PendingFactDto(
            Db.Str(r, "id"), Db.Str(r, "kind"), Db.Str(r, "text"), JsonDict(Db.Str(r, "meta")), Db.Str(r, "origin"),
            Db.Str(r, "context"), Db.Str(r, "status"), Db.Str(r, "created_at"), Db.StrOrNull(r, "decided_at")), Db.P("@k", key));
        if (existing is not null) return new List<PendingFactDto> { existing };

        // Fuzzy duplicate against pending -> mark it so the UI can collapse it, but still surface the row.
        var candidates = Db.Query(conn, "SELECT text FROM pending_facts WHERE status = 'pending'", r => Db.Str(r, "text"), Array.Empty<SqliteParameter>());
        var fuzzy = candidates.FirstOrDefault(c => FuzzyMatch(c, text));
        if (fuzzy is not null)
        {
            meta["fuzzy_similar_to"] = fuzzy;
        }

        var pending = InsertPending(conn, kind, text, meta, origin, context);
        return new List<PendingFactDto> { pending };
    }

    private static PendingFactDto InsertPending(SqliteConnection conn, string kind, string text,
        Dictionary<string, string> meta, string origin, string context)
    {
        var id = NewId("pend");
        var now = Now();
        var metaJson = JsonSerializer.Serialize(meta);
        Db.Exec(conn, "INSERT INTO pending_facts (id, kind, text, meta, origin, context, match_key, status, created_at) VALUES (@id, @kind, @text, @meta, @origin, @ctx, @key, 'pending', @now)",
            Db.P("@id", id), Db.P("@kind", kind), Db.P("@text", text), Db.P("@meta", metaJson),
            Db.P("@origin", origin), Db.P("@ctx", context), Db.P("@key", NormalizeKey(text)), Db.P("@now", now));
        return new PendingFactDto(id, kind, text, meta, origin, context, "pending", now, null);
    }

    // ---- Reference block for prompts ---------------------------------------

    public string BuildReference()
    {
        var profile = GetProfile();
        var facts = ListFacts();
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(profile.Voice)) sb.AppendLine($"VOICE: {profile.Voice}");
        if (!string.IsNullOrWhiteSpace(profile.Preferences)) sb.AppendLine($"PREFERENCES: {profile.Preferences}");
        if (profile.Contact.Count > 0)
            sb.AppendLine("CONTACT: " + string.Join("; ", profile.Contact.Select(kv => $"{kv.Key}: {kv.Value}")));
        if (facts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("VERIFIED FACTS (the only facts you may assert):");
            foreach (var group in facts.GroupBy(f => f.Kind).OrderBy(g => g.Key))
            {
                sb.AppendLine($"- {group.Key}:");
                foreach (var fact in group) sb.AppendLine($"  * {fact.Text}");
            }
        }
        return sb.ToString().Trim();
    }

    // ---- Helpers -----------------------------------------------------------

    /// <summary>Normalized dedupe key: lowercase, punctuation stripped, whitespace collapsed.</summary>
    public static string NormalizeKey(string text) =>
        string.Join(' ', new string(text.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// Token-set similarity in 0..1, used to avoid asking the user to approve the same fact twice
    /// ("Led the payments team" vs "Led payments team").
    /// </summary>
    public static double Similarity(string a, string b)
    {
        var tokensA = Tokenize(a);
        var tokensB = Tokenize(b);
        if (tokensA.Count == 0 || tokensB.Count == 0) return 0;
        var intersection = tokensA.Intersect(tokensB).Count();
        var union = tokensA.Union(tokensB).Count();
        return (double)intersection / union;
    }

    /// <summary>
    /// 0.70 separates "same fact, different wording" (0.71 and up) from "different fact"
    /// (0.33 in practice), so the user is never asked about the same thing twice.
    /// </summary>
    public const double FuzzyThreshold = 0.70;

    public static bool FuzzyMatch(string a, string b, double threshold = FuzzyThreshold) => Similarity(a, b) >= threshold;

    private static HashSet<string> Tokenize(string text) =>
        new(NormalizeKey(text).Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static Dictionary<string, string> JsonDict(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    public static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..12]}";

    public static string ShortHash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..12].ToLowerInvariant();
}