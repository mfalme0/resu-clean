using System.Text.Json;
using System.Text.RegularExpressions;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Keeps the keyword dictionary current without letting it change behind your back.
///
/// The built-in list is a C# array, and hardcoding your own stack into it fixes the tool for one
/// person and breaks it for the next. Editing it by hand is better but still silent: the list is
/// scoring-critical, and a term appearing in it changes every ATS score and every job ranking.
///
/// So additions go through the same approval queue as a profile fact. The service proposes terms it
/// notices in your resumes that the dictionary does not know, each with how often it appears; nothing
/// joins the dictionary until you approve it. Approving writes a row here and refreshes the live
/// dictionary; rejecting leaves it exactly as it was.
/// </summary>
public sealed partial class SkillDictionaryService
{
    public const string Workflow = "dictionary";

    /// <summary>A term has to appear this often across your material before it is worth proposing.</summary>
    public const int ProposalThreshold = 3;

    private readonly Db _db;
    private readonly ModelRouter _router;
    private readonly ProfileService _profile;
    private readonly ResumeStore _store;

    public SkillDictionaryService(Db db, ModelRouter router, ProfileService profile, ResumeStore store)
    {
        _db = db;
        _router = router;
        _profile = profile;
        _store = store;
    }

    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9+#.\-]{2,24}\b", RegexOptions.None, 2000)]
    private static partial Regex CandidateTerm();

    public sealed record Proposal(string Term, int Occurrences, string Origin);

    // ---- Reading -----------------------------------------------------------

    public List<SkillTermDto> List() => Db.Query(_db.Open(),
        "SELECT id, term, normalized, occurrences, origin, created_at FROM skill_dictionary ORDER BY term",
        r => new SkillTermDto(
            Db.Str(r, "id"), Db.Str(r, "term"), Db.Str(r, "normalized"),
            Db.Int(r, "occurrences"), Db.Str(r, "origin"), Db.Str(r, "created_at")),
        Array.Empty<Microsoft.Data.Sqlite.SqliteParameter>());

    public IReadOnlyList<string> Terms() => List().Select(t => t.Term).ToList();

    /// <summary>Pushes the approved terms into the live dictionary. Called at startup and on every change.</summary>
    public void Refresh()
    {
        try
        {
            Keywords.SetExtraSkills(Terms());
        }
        catch
        {
            // A dictionary refresh failure must leave the built-in list in place rather than empty
            // the dictionary: changing scoring because a read failed is worse than a stale term.
        }
    }

    // ---- Proposing ---------------------------------------------------------

    /// <summary>
    /// Finds terms that recur in your resumes and are absent from the dictionary, and queues them for
    /// approval. Deterministic: it counts what is actually written, so it cannot invent a skill.
    /// </summary>
    public List<PendingFactDto> ProposeAsync(CancellationToken ct = default)
    {
        var corpus = Corpus();
        if (string.IsNullOrWhiteSpace(corpus)) return new List<PendingFactDto>();

        var known = new HashSet<string>(AllKnownTerms(), StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in CandidateTerm().Matches(corpus))
        {
            var term = m.Value.Trim('.', '-', '#');
            if (term.Length is < 3 or > 24) continue;
            if (known.Contains(term)) continue;
            if (StopWords.Contains(term)) continue;
            counts[term] = counts.TryGetValue(term, out var n) ? n + 1 : 1;
            display.TryAdd(term, m.Value.Trim('.', '-', '#'));
        }

        var queued = new List<PendingFactDto>();
        foreach (var (term, count) in counts.Where(c => c.Value >= ProposalThreshold)
                     .OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            queued.AddRange(_profile.AddFactToPending(
                "skill", display[term], "dictionary",
                context: $"Appears {count} times across your resumes and is not in the keyword dictionary. " +
                         $"Approving it makes it count towards ATS keyword coverage and job ranking."));
        }
        return queued;
    }

    /// <summary>Every term the dictionary would already match, so it is never proposed back to you.</summary>
    private static IEnumerable<string> AllKnownTerms() => Keywords.ExtraSkills.Concat(BuiltIn());

    private static IEnumerable<string> BuiltIn()
    {
        // The built-in list is private to Keywords by design. Ask it to extract from its own words
        // rather than duplicating the array here, so the two can never drift apart.
        return Keywords.KnownSkillTerms();
    }

    private string Corpus()
    {
        var parts = new List<string>();
        foreach (var resume in _store.List(1, 25).Items)
        {
            var detail = _store.Get(resume.Id);
            var versionId = detail?.Summary.CurrentVersionId ?? detail?.Versions.FirstOrDefault()?.Id;
            if (versionId is null) continue;
            var text = _store.GetText(versionId);
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
        }
        parts.AddRange(_profile.ListFacts().Select(f => f.Text));
        return string.Join("\n", parts);
    }

    // ---- Approving ---------------------------------------------------------

    /// <summary>
    /// Adds an approved term to the live dictionary. Called for a pending item whose origin is
    /// "dictionary"; separate from the profile-fact approval path because it writes to its own table.
    /// </summary>
    public SkillTermDto Add(string term, int occurrences = 0, string origin = "approved")
    {
        var clean = (term ?? "").Trim();
        if (clean.Length < 3)
            throw new ArgumentException("A dictionary term needs at least three characters, or it matches inside unrelated words.");
        var normalized = ProfileService.NormalizeKey(clean);
        var now = DateTime.UtcNow.ToString("O");
        var id = ProfileService.NewId("skill");

        using var conn = _db.Open();
        var existing = Db.QueryOne(conn, "SELECT id FROM skill_dictionary WHERE normalized = @n", r => Db.Str(r, "id"),
            Db.P("@n", normalized));
        if (existing is null)
        {
            Db.Exec(conn,
                "INSERT INTO skill_dictionary (id, term, normalized, occurrences, origin, created_at) VALUES (@i,@t,@n,@o,@r,@d)",
                Db.P("@i", id), Db.P("@t", clean), Db.P("@n", normalized), Db.P("@o", occurrences),
                Db.P("@r", origin), Db.P("@d", now));
        }
        else
        {
            id = existing;
        }

        Refresh();
        return Db.QueryOne(conn, "SELECT id, term, normalized, occurrences, origin, created_at FROM skill_dictionary WHERE id = @i",
            r => new SkillTermDto(Db.Str(r, "id"), Db.Str(r, "term"), Db.Str(r, "normalized"),
                Db.Int(r, "occurrences"), Db.Str(r, "origin"), Db.Str(r, "created_at")), Db.P("@i", id))!;
    }

    public bool Remove(string id)
    {
        using var conn = _db.Open();
        var removed = Db.Execute(conn, "DELETE FROM skill_dictionary WHERE id = @i", Db.P("@i", id)) > 0;
        if (removed) Refresh();
        return removed;
    }

    // ---- Model proposal (optional upgrade) ---------------------------------

    /// <summary>
    /// Asks a model which recurring terms are missing. Falls back to the deterministic count when no
    /// model is routed or the reply is not usable, so the feature never depends on a provider.
    /// </summary>
    public async Task<List<PendingFactDto>> ProposeAsync(bool useModel, CancellationToken ct = default)
    {
        if (!useModel || !_router.AnyConfigured(Workflow)) return ProposeAsync(ct);

        var corpus = Corpus();
        if (string.IsNullOrWhiteSpace(corpus)) return new List<PendingFactDto>();

        var system = ModelRouter.SystemPrompt(Workflow, string.Empty);
        var user =
            "List technical skills, tools and platforms that appear in the text below and are NOT in this list: " +
            string.Join(", ", Keywords.KnownSkillTerms().Take(200)) + ". " +
            "Return a JSON array of objects with \"term\" (the tool as written) and \"occurrences\" (how many times it appears). " +
            "Only terms that actually appear in the text. Return a JSON array and nothing else.\n\n" +
            $"TEXT:\n<<<{corpus}>>>";

        var run = await _router.RunStructuredAsync(Workflow, system, user, ParseProposals, ct: ct).ConfigureAwait(false);
        if (run.Value is null) return ProposeAsync(ct);

        var known = new HashSet<string>(AllKnownTerms(), StringComparer.OrdinalIgnoreCase);
        var queued = new List<PendingFactDto>();
        foreach (var proposal in run.Value.Where(p => p.Occurrences >= ProposalThreshold && !known.Contains(p.Term)))
        {
            // Only propose what is genuinely in the text: a model naming a tool it inferred is the
            // exact failure this feature must not introduce.
            if (corpus.IndexOf(proposal.Term, StringComparison.OrdinalIgnoreCase) < 0) continue;
            queued.AddRange(_profile.AddFactToPending("skill", proposal.Term, "dictionary",
                context: $"Appears {proposal.Occurrences} times across your resumes and is not in the keyword dictionary."));
        }
        // Whatever the model missed still gets proposed.
        queued.AddRange(ProposeDeterministicOnly(known));
        return queued;
    }

    private List<PendingFactDto> ProposeDeterministicOnly(HashSet<string> known)
    {
        var corpus = Corpus();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CandidateTerm().Matches(corpus))
        {
            var term = m.Value.Trim('.', '-', '#');
            if (term.Length is < 3 or > 24 || known.Contains(term) || StopWords.Contains(term)) continue;
            counts[term] = counts.TryGetValue(term, out var n) ? n + 1 : 1;
        }
        var queued = new List<PendingFactDto>();
        foreach (var (term, count) in counts.Where(c => c.Value >= ProposalThreshold))
            queued.AddRange(_profile.AddFactToPending("skill", term, "dictionary",
                context: $"Appears {count} times across your resumes and is not in the keyword dictionary."));
        return queued;
    }

    private sealed record ProposalModel(string Term, int Occurrences);

    private static List<ProposalModel>? ParseProposals(string reply)
    {
        var start = reply.IndexOf('[');
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<ProposalModel>>(reply[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed is { Count: > 0 } ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "The","And","For","With","You","Your","Our","This","That","Will","Experience","Responsibilities","Requirements",
        "Summary","Skills","Education","Present","Nairobi","Kenya","January","February","March","April","May","June",
        "July","August","September","October","November","December","Company","Management","Engineering","Analyst"
    };
}