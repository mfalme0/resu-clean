using System.Text.Json;
using System.Text.RegularExpressions;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Separates what a posting actually demands from what it merely mentions.
///
/// Keyword matching treats "AWS" the same whether the ad says "you must have AWS" or "we run on
/// AWS". That collapses the one distinction that decides whether a role is worth an afternoon:
/// a blocker nobody can close this weekend and a preferred skill nobody will miss. This service
/// classifies each requirement and checks it against the verified profile, so the list is ranked by
/// what is actually hard rather than by what is merely absent.
///
/// Works without a model. Section headings and marker phrases classify the large majority of postings
/// correctly, because postings are written to a template; a route adds judgement for the ones that
/// are not.
///
/// Which side of the line a requirement falls on is decided here. Whether you meet it is decided
/// against the verified profile by <see cref="IsMet"/>, never by the model — a model is good at
/// reading the ad and bad at knowing what you have done.
/// </summary>
public sealed partial class RequirementsService
{
    public const string Workflow = "requirements";

    private readonly ModelRouter _router;
    private readonly ProfileService _profile;
    private readonly Db _db;

    public RequirementsService(ModelRouter router, ProfileService profile, Db db)
    {
        _router = router;
        _profile = profile;
        _db = db;
    }

    /// <summary>blocker = cannot proceed without it. required = expected. preferred = a plus. implicit = assumed.</summary>
    public const string Blocker = "blocker";
    public const string Required = "required";
    public const string Preferred = "preferred";
    public const string Implicit = "implicit";

    public sealed record Requirement(
        string Text,
        string Class,
        bool Met,
        IReadOnlyList<string> Missing,
        string? Evidence);

    public sealed record Report(
        string JobId,
        IReadOnlyList<Requirement> Requirements,
        bool UsedModel,
        string? Model,
        string Method)
    {
        public IReadOnlyList<Requirement> Blockers => Requirements.Where(r => r.Class == Blocker).ToList();
        public IReadOnlyList<Requirement> RequiredItems => Requirements.Where(r => r.Class == Required).ToList();
        public int UnmetBlockers => Blockers.Count(r => !r.Met);
        public int UnmetRequired => RequiredItems.Count(r => !r.Met);
    }

    // ---- Section headings in a posting -------------------------------------

    // Sections that state demands rather than duties. Order matters: the first match wins, so the
    // more specific "nice to have" is tested before the broad "requirements".
    private static readonly (string Section, string Class)[] SectionMarkers =
    {
        ("nice to have", Preferred), ("preferred qualifications", Preferred), ("preferred", Preferred),
        ("bonus", Preferred), ("desirable", Preferred), ("a plus", Preferred), ("nice-to-have", Preferred),
        ("must have", Blocker), ("must-have", Blocker), ("essential", Blocker), ("minimum qualifications", Blocker),
        ("required qualifications", Blocker), ("hard requirements", Blocker), ("you must", Blocker),
        ("requirements", Required), ("required", Required), ("required skills", Required),
        ("what you will bring", Required), ("what you'll bring", Required), ("what we are looking for", Required),
        ("who you are", Required), ("qualifications", Required), ("skills", Required),
        ("technical skills", Required), ("what we look for", Required), ("about you", Required),
        ("responsibilities", Implicit), ("what you'll do", Implicit), ("what you will do", Implicit),
        ("the role", Implicit), ("your role", Implicit), ("day to day", Implicit), ("duties", Implicit)
    };

    [GeneratedRegex(@"^\s*(?:[-*\u2022\u25AA\u25CF\u25E6\u2192\u2794>\u00BB]+\s*)?(?:\d+[.)]\s*)?(?<text>\S.*)$", RegexOptions.None, 2000)]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"(?i)\b(must have|must possess|required|essential|mandatory|non-negotiable|professional certification|professional licence|professional license|at least|minimum of|no fewer than)\b", RegexOptions.None, 2000)]
    private static partial Regex BlockerPhrase();

    [GeneratedRegex(@"(?i)\b(nice to have|a plus|bonus|desirable|preferred|ideally|advantageous|would be great|familiarity with|exposure to)\b", RegexOptions.None, 2000)]
    private static partial Regex PreferredPhrase();

    [GeneratedRegex(@"(?i)\b(\d+)\+?\s*(?:years?|yrs?)\b", RegexOptions.None, 2000)]
    private static partial Regex YearsRequired();

    // Salient words: what a requirement is actually about. Function words carry no signal, so a
    // requirement is not judged met because both texts contain the word "experience".
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","with","you","your","our","their","this","that","will","have","has","had","are","was",
        "were","been","not","but","can","could","should","would","may","might","must","from","into","than","then",
        "them","they","who","what","when","where","why","how","which","while","work","working","role","team",
        "teams","job","jobs","candidate","including","include","includes","also","such","other","years","year",
        "experience","experienced","ability","able","strong","good","great","high","level","plus","years","using",
        "use","used","knowledge","understanding","familiarity","demonstrated","proven","track","record","minimum",
        "at","least","ideally","preferably","especially","particularly","within","across","environment","environments"
    };

    // ---- Entry point -------------------------------------------------------

    public async Task<Report> AnalyseAsync(string jobId, string jobDescription, string resumeText, CancellationToken ct = default)
    {
        var (requirements, usedModel, model) = _router.AnyConfigured(Workflow)
            ? await ModelRequirementsAsync(jobDescription, ct).ConfigureAwait(false)
            : (DeterministicRequirements(jobDescription), false, (string?)null);

        if (requirements.Count == 0) requirements = DeterministicRequirements(jobDescription);

        var evidence = EvidenceCorpus(resumeText);
        var met = requirements
            .Select(r => r with
            {
                Met = IsMet(r.Text, evidence),
                Missing = MissingParts(r.Text, evidence)
            })
            .ToList();

        var report = new Report(jobId, met, usedModel, model, usedModel ? "model" : "deterministic");
        Save(jobId, report);
        return report;
    }

    /// <summary>Everything you can legitimately claim: the resume plus the approved profile.</summary>
    private string EvidenceCorpus(string resumeText) =>
        $"{resumeText}\n{string.Join("\n", _profile.ListFacts().Select(f => f.Text))}";

    // ---- Deterministic classification --------------------------------------

    public List<Requirement> DeterministicRequirements(string jobDescription)
    {
        var requirements = new List<Requirement>();
        var current = string.Empty;

        foreach (var raw in jobDescription.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var asHeading = LinePattern().Match(raw);
            var text = asHeading.Groups["text"].Value.Trim();
            if (text.Length is < 4 or > 300) continue;

            // A short line with no sentence punctuation is a heading and sets the section for what
            // follows. Everything else is a requirement candidate.
            var looksLikeHeading = text.Length <= 60 && !text.EndsWith('.') && !text.EndsWith(',');
            if (looksLikeHeading)
            {
                var marker = SectionMarkers.FirstOrDefault(m => text.Contains(m.Section, StringComparison.OrdinalIgnoreCase));
                if (marker.Section is not null)
                {
                    current = marker.Class;
                    continue;
                }
                // An unrecognised heading still ends the previous section rather than leaking it.
                current = string.Empty;
                continue;
            }

            var requirement = Classify(text, current);
            if (requirement is not null) requirements.Add(requirement);
        }

        return requirements;
    }

    /// <summary>
    /// Marker phrases beat the section, because a posting that says "nice to have: Terraform" inside
    /// a requirements section is still a nice-to-have. A years requirement is always a blocker:
    /// nobody closes seven years of experience in a weekend, and pretending otherwise is exactly
    /// the advice that wastes an afternoon.
    /// </summary>
    private static Requirement? Classify(string text, string section)
    {
        var trimmed = text.TrimEnd('.', ';').Trim();
        if (trimmed.Length < 8) return null;

        var evidence = YearsRequired().Match(trimmed).Value is { Length: > 0 } years
            ? (string?)years
            : null;

        // Order matters. "Ideally 3 years with Terraform" is still a nice-to-have, so the preferred
        // phrase is checked first. Anything else with a years requirement is a blocker regardless of
        // which section it sits in: a duration is the one demand no amount of study closes.
        var cls = PreferredPhrase().IsMatch(trimmed) ? Preferred
            : YearsRequired().IsMatch(trimmed) ? Blocker
            : BlockerPhrase().IsMatch(trimmed) ? Blocker
            : section is not (null or "") ? section
            : Implicit;

        return new Requirement(trimmed, cls, false, Array.Empty<string>(), evidence);
    }

    // ---- Is it met? --------------------------------------------------------

    /// <summary>
    /// Decided against your own material, never by a model. A requirement counts as met when enough
    /// of the words it is actually about appear in your resume or your approved facts.
    /// </summary>
    public static bool IsMet(string requirementText, string evidence)
    {
        var parts = Salient(requirementText);
        if (parts.Count == 0) return true;

        var haystack = evidence.ToLowerInvariant();
        var found = parts.Count(p => Keywords.ContainsTerm(haystack, p));
        return found / (double)parts.Count >= 0.6;
    }

    /// <summary>The parts of a requirement nothing in your evidence accounts for, named explicitly.</summary>
    public static List<string> MissingParts(string requirementText, string evidence)
    {
        var haystack = evidence.ToLowerInvariant();
        return Salient(requirementText).Where(p => !Keywords.ContainsTerm(haystack, p)).ToList();
    }

    private static List<string> Salient(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}+#.\-]{1,24}")
            .Select(m => m.Value.Trim('.', '#', '-'))
            .Where(t => t.Length >= 3 && t.Length <= 24 && !StopWords.Contains(t))
            .Distinct()
            .ToList();

    // ---- Model classification ----------------------------------------------

    private sealed record ModelRequirement(string Text, string Class, string? Evidence);

    private async Task<(List<Requirement> Requirements, bool UsedModel, string? Model)> ModelRequirementsAsync(
        string jobDescription, CancellationToken ct)
    {
        var system = ModelRouter.SystemPrompt(Workflow, string.Empty);
        var user =
            "Read this job posting and list what it requires, one per entry, as a JSON array. Each entry: " +
            "{\"text\": string, \"class\": \"blocker\"|\"required\"|\"preferred\"|\"implicit\", \"evidence\": string}. " +
            "Use blocker when the posting makes it compulsory (\"must have\", \"required\", \"professional certification\", " +
            "a minimum number of years), required when it appears in the qualifications or what-you-will-bring section, " +
            "preferred for \"nice to have\" or \"a plus\", and implicit for anything the role assumes without stating it. " +
            "Quote the posting's own wording in \"evidence\". Do not invent requirements that are not in the posting. " +
            "Return a JSON array and nothing else.\n\n" +
            $"POSTING:\n<<<{jobDescription}>>>";

        var run = await _router.RunStructuredAsync(Workflow, system, user, Parse, ct: ct).ConfigureAwait(false);
        if (run.Value is null) return (new List<Requirement>(), false, null);
        return (run.Value, true, run.Routed.Model);
    }

    private static List<Requirement>? Parse(string reply)
    {
        var start = reply.IndexOf('[');
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start) return null;

        List<ModelRequirement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<ModelRequirement>>(reply[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }

        return parsed?
            .Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => new Requirement(
                r.Text.Trim(),
                NormaliseClass(r.Class),
                false,
                Array.Empty<string>(),
                string.IsNullOrWhiteSpace(r.Evidence) ? null : r.Evidence.Trim()))
            .ToList();
    }

    private static string NormaliseClass(string? cls) => (cls ?? "").Trim().ToLowerInvariant() switch
    {
        "blocker" or "must" or "must_have" => Blocker,
        "required" or "requirement" => Required,
        "preferred" or "nice_to_have" or "nice to have" or "bonus" => Preferred,
        _ => Implicit
    };

    // ---- Persistence -------------------------------------------------------

    private void Save(string jobId, Report report)
    {
        try
        {
            using var conn = _db.Open();
            var tx = conn.BeginTransaction();
            Db.Exec(conn, "DELETE FROM job_requirements WHERE job_id = @j", Db.P("@j", jobId));
            foreach (var r in report.Requirements)
            {
                Db.Exec(conn,
                    "INSERT INTO job_requirements (id, job_id, text, class, met, missing, evidence, created_at) "
                    + "VALUES (@i,@j,@t,@c,@m,@mi,@e,@n)",
                    Db.P("@i", ProfileService.NewId("req")), Db.P("@j", jobId), Db.P("@t", r.Text),
                    Db.P("@c", r.Class), Db.P("@m", r.Met ? 1 : 0),
                    Db.P("@mi", JsonSerializer.Serialize(r.Missing)),
                    Db.P("@e", r.Evidence ?? ""), Db.P("@n", DateTime.UtcNow.ToString("O")));
            }
            tx.Commit();
        }
        catch
        {
            // Classification is an aid. Failing to store it must not fail the request.
        }
    }

    public List<Requirement> ForJob(string jobId) => Db.Query(_db.Open(),
        "SELECT text, class, met, missing, evidence FROM job_requirements WHERE job_id = @j ORDER BY rowid",
        r => new Requirement(
            Db.Str(r, "text"), Db.Str(r, "class"), Db.Bool(r, "met"),
            ReadMissing(Db.Str(r, "missing")), Db.StrOrNull(r, "evidence")),
        Db.P("@j", jobId));

    private static IReadOnlyList<string> ReadMissing(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Jobs whose postings carry enough text to be worth classifying.</summary>
    public List<(string JobId, string Description)> Classifiable(int limit = 200) => Db.Query(_db.Open(),
        "SELECT id, description FROM jobs WHERE LENGTH(description) > 200 ORDER BY created_at DESC LIMIT @l",
        r => (Db.Str(r, "id"), Db.Str(r, "description")),
        Db.P("@l", Math.Clamp(limit, 1, 1000)));
}