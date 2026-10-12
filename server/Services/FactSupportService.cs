using System.Text.Json;
using System.Text.RegularExpressions;
using ResuClean.Models;
using ResuClean.Services.Providers;

namespace ResuClean.Services;

/// <summary>
/// Checks whether a resume is actually grounded in the verified profile.
///
/// The no-fabrication guard already stops a model inventing a number mid-rewrite, but it has nothing
/// to say about a claim that was typed by hand, arrived from an earlier AI paste, or drifted over
/// months of edits. This is the pass that closes that loop: it takes each claim the resume makes and
/// asks whether anything backs it up.
///
/// Two implementations of the same question. With a model routed, each claim is extracted and
/// grounded with a citation to the fact id it came from. With no model, the narrower question is
/// asked directly: does this bullet carry a figure that appears nowhere in the profile and nowhere
/// in the resume's own employment scaffolding? Those are the claims worth a second look, and finding
/// them needs no provider at all.
///
/// Nothing is deleted. An unsupported claim is surfaced for approval, which is the same gate every
/// other piece of new information passes through.
/// </summary>
public sealed partial class FactSupportService
{
    /// <summary>Route used for the model-assisted pass. Separate from the writing workflows.</summary>
    public const string Workflow = "fact_check";

    private readonly ModelRouter _router;
    private readonly ProfileService _profile;
    private readonly ResuClean.Data.Db _db;

    public FactSupportService(ModelRouter router, ProfileService profile, ResuClean.Data.Db db)
    {
        _router = router;
        _profile = profile;
        _db = db;
    }

    // A figure worth checking: anything with a digit run, but not a bare year in a date range,
    // which is scaffolding rather than an achievement.
    [GeneratedRegex(@"\b\d[\d,]*(?:\.\d+)?\s*(?:%|percent|k|m|bn|million|billion|thousand)?\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Figure();

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b", RegexOptions.None, 2000)]
    private static partial Regex Year();

    [GeneratedRegex(@"^[\s\u00A0]*[-*\u2022\u25AA\u25CF\u25E6\u2192\u2794>\u00BB]+\s+", RegexOptions.None, 2000)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"\b[\w.+-]+@[\w-]+\.[\w.-]{2,}\b", RegexOptions.None, 2000)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?:\+?\d[\s\-().]{0,3}){7,15}\d", RegexOptions.None, 2000)]
    private static partial Regex Phone();

    [GeneratedRegex(@"(?i)\b(b\.?sc|b\.?a|b\.?com|b\.?ed|b\.?tech|m\.?sc|m\.?a|m\.?com|ph\.?d|degree|diploma|certificate|university|college|school)\b", RegexOptions.None, 2000)]
    private static partial Regex Education();

    public sealed record Claim(
        string Text,
        string Category,
        IReadOnlyList<string> FactIds,
        bool Grounded,
        string Note);

    public sealed record Report(
        string VersionId,
        int Checked,
        int Grounded,
        IReadOnlyList<Claim> Claims,
        bool UsedModel,
        string? Model,
        /// <summary>"model" or "deterministic", so the UI never implies more was checked than was.</summary>
        string Method,
        IReadOnlyList<PendingFactDto> Queued)
    {
        public int Ungrounded => Checked - Grounded;
    }

    // ---- Entry point -------------------------------------------------------

    public async Task<Report> CheckAsync(string versionId, string resumeText, CancellationToken ct = default)
    {
        var lines = resumeText.Replace("\r\n", "\n").Split('\n');
        var claimLines = ClaimLineIndexes(lines);

        // The deterministic pass always runs. It is the floor, and it is what a claim is judged by
        // when no model is available to attribute it.
        var figures = UnverifiedFigures(resumeText, claimLines);
        var deterministic = DeterministicClaims(lines, claimLines, figures);

        List<Claim> claims = deterministic;
        bool usedModel = false;
        string? model = null;

        if (_router.AnyConfigured(Workflow))
        {
            var run = await ModelClaimsAsync(resumeText, ct).ConfigureAwait(false);
            // A malformed or empty model reply falls back to the deterministic pass rather than
            // reporting "nothing found", which would read as a clean bill of health.
            if (run.Claims is not null)
            {
                claims = run.Claims;
                usedModel = true;
                model = run.Model;
            }
        }

        // A model claim is trusted for attribution but not for coverage: anything the deterministic
        // pass flagged stays flagged unless the model cited a fact for it.
        claims = Merge(claims, figures);

        var queued = QueueForApproval(claims);
        var report = new Report(versionId, claims.Count, claims.Count(c => c.Grounded), claims,
            usedModel, model, usedModel ? "model" : "deterministic", queued);
        Save(report);
        return report;
    }

    /// <summary>
    /// Every figure that nothing else accounts for, as a pipe-separated list.
    ///
    /// The allowed set is built from the lines that are *not* claims — headings, the contact block,
    /// employer and date lines, education — plus the verified profile. It deliberately excludes claim
    /// lines: a claim that sourced its own figures would be self-grounding, which is the failure this
    /// whole check exists to catch.
    /// </summary>
    private string UnverifiedFigures(string resumeText, HashSet<int> claimLines)
    {
        var lines = resumeText.Replace("\r\n", "\n").Split('\n');
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            if (claimLines.Contains(i)) continue;
            foreach (var value in FiguresIn(lines[i])) allowed.Add(value);
        }
        foreach (var value in _profile.ListFacts().SelectMany(f => FiguresIn(f.Text))) allowed.Add(value);

        var flagged = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!claimLines.Contains(i)) continue;
            var line = lines[i];
            foreach (var value in FiguresIn(line))
            {
                // A year inside a date range belongs to the employment history, not an achievement.
                if (Year().IsMatch(value) && DateRangeLike(line)) continue;
                if (allowed.Contains(value)) continue;
                if (!flagged.Contains(value, StringComparer.OrdinalIgnoreCase)) flagged.Add(value);
            }
        }
        return string.Join("|", flagged);
    }

    private static IEnumerable<string> FiguresIn(string line) =>
        Figure().Matches(line).Select(m => m.Value.Trim().TrimEnd('.'));


    private static bool DateRangeLike(string line) =>
        Regex.IsMatch(line, @"(?i)\b(19|20)\d{2}\b\s*(?:-|–|—|to)\s*(?:\b(19|20)\d{2}\b|current|present|now)");

    // ---- Deterministic claims ---------------------------------------------

    /// <summary>Which lines carry claims, decided once so the figure pass and the claim pass agree.</summary>
    private static HashSet<int> ClaimLineIndexes(string[] lines)
    {
        var claims = new HashSet<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length is < 8 or > 300) continue;
            if (IsScaffolding(line)) continue;
            if (Bullet().IsMatch(lines[i]) || AchievementLike(line)) claims.Add(i);
        }
        return claims;
    }

    private static List<Claim> DeterministicClaims(string[] lines, HashSet<int> claimLines, string figures)
    {
        var flagged = figures.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var claims = new List<Claim>();
        foreach (var i in claimLines.OrderBy(x => x))
        {
            var line = lines[i].Trim();
            var hits = FiguresIn(line).Where(flagged.Contains).ToList();

            claims.Add(hits.Count > 0
                ? new Claim(line, "achievement", Array.Empty<string>(), false,
                    $"Figure not found in your verified profile: {string.Join(", ", hits)}.")
                : new Claim(line, "achievement", Array.Empty<string>(), true, ""));
        }
        return claims;
    }

    /// <summary>
    /// The lines that describe the shape of a career rather than assert anything about it: headings,
    /// the contact block, employer and date lines, and education entries. Treating these as claims
    /// would bury the real findings under noise, and a report that cries wolf gets ignored.
    /// </summary>
    private static bool IsScaffolding(string line) =>
        DateRangeLike(line)
        || Email().IsMatch(line)
        || Phone().IsMatch(line)
        || line.EndsWith(':')
        || Education().IsMatch(line)
        || line.All(c => char.IsUpper(c) || char.IsWhiteSpace(c) || char.IsPunctuation(c));

    private static bool AchievementLike(string line) =>
        line.Length > 24 && !line.EndsWith(':') && !line.All(c => char.IsUpper(c) || char.IsWhiteSpace(c));

    /// <summary>A model claim the deterministic pass flagged only survives if it cited a fact.</summary>
    private static List<Claim> Merge(List<Claim> claims, string figures)
    {
        var flagged = figures.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return claims.Select(c =>
        {
            var carriesFlaggedFigure = Figure().Matches(c.Text)
                .Select(m => m.Value.Trim().TrimEnd('.'))
                .Any(flagged.Contains);
            if (c.Grounded || !carriesFlaggedFigure) return c;
            return c with
            {
                Grounded = false,
                Note = string.IsNullOrWhiteSpace(c.Note)
                    ? "This figure is not in your verified profile and no fact was cited."
                    : c.Note
            };
        }).ToList();
    }

    // ---- Model claims ------------------------------------------------------

    private async Task<(List<Claim>? Claims, string? Model)> ModelClaimsAsync(
        string resumeText, CancellationToken ct)
    {
        var system = ModelRouter.SystemPrompt(Workflow, _profile.BuildReference());
        var user =
            "List every factual claim this resume makes, one per entry, as a JSON array. Each entry is an object " +
            "with: \"text\" (the claim, quoted from the resume), \"category\" (one of: role, achievement, skill, " +
            "education, cert, date, contact, other), \"factIds\" (an array of the ids of the verified facts that " +
            "support it, or an empty array if none do), and \"grounded\" (true only if a fact supports it). " +
            "Do not add claims that are not in the resume. Do not invent fact ids: only use ids that appear in " +
            "the verified facts above. Return a JSON array and nothing else.\n\n" +
            $"RESUME:\n<<<{resumeText}>>>";

        var run = await _router.RunStructuredAsync(Workflow, system, user, ParseClaims, ct: ct).ConfigureAwait(false);
        if (run.Value is null) return (null, null);
        return (run.Value, run.Routed.Model);
    }

    private sealed record ModelClaim(string Text, string Category, string[]? FactIds, bool Grounded);

    /// <summary>
    /// Tolerant of a model that wraps its JSON in a fence or a sentence of preamble, because a reply
    /// that is nearly right should not be thrown away. What it is not tolerant of is a missing field:
    /// without a claim text there is nothing to check.
    /// </summary>
    private static List<Claim>? ParseClaims(string reply)
    {
        var json = ExtractJsonArray(reply);
        if (json is null) return null;

        List<ModelClaim>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<ModelClaim>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed is null || parsed.Count == 0) return null;

        return parsed
            .Where(c => !string.IsNullOrWhiteSpace(c.Text))
            .Select(c => new Claim(
                c.Text.Trim(),
                string.IsNullOrWhiteSpace(c.Category) ? "other" : c.Category.Trim().ToLowerInvariant(),
                c.FactIds ?? Array.Empty<string>(),
                c.Grounded && c.FactIds is { Length: > 0 },
                c.Grounded && c.FactIds is { Length: > 0 }
                    ? ""
                    : "No verified fact supports this."))
            .ToList();
    }

    private static string? ExtractJsonArray(string reply)
    {
        var start = reply.IndexOf('[');
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        return reply[start..(end + 1)];
    }

    // ---- Approval ----------------------------------------------------------

    private List<PendingFactDto> QueueForApproval(List<Claim> claims)
    {
        var queued = new List<PendingFactDto>();
        foreach (var claim in claims.Where(c => !c.Grounded))
        {
            queued.AddRange(_profile.AddFactToPending(
                claim.Category, claim.Text, "fact_check", context: claim.Note));
        }
        return queued;
    }

    // ---- History -----------------------------------------------------------

    /// <summary>
    /// Keeps the report. A grounding result that vanishes when the tab closes cannot be compared
    /// with one from next month, and that comparison is the point of running it at all.
    /// </summary>
    private void Save(Report report)
    {
        try
        {
            using var conn = _db.Open();
            ResuClean.Data.Db.Exec(conn,
                "INSERT INTO fact_check_reports (id, version_id, model, method, checked, ungrounded, claims, created_at) "
                + "VALUES (@i,@v,@m,@me,@c,@u,@cl,@n)",
                ResuClean.Data.Db.P("@i", ProfileService.NewId("fcr")), ResuClean.Data.Db.P("@v", report.VersionId),
                ResuClean.Data.Db.P("@m", report.Model ?? ""), ResuClean.Data.Db.P("@me", report.Method),
                ResuClean.Data.Db.P("@c", report.Checked), ResuClean.Data.Db.P("@u", report.Ungrounded),
                ResuClean.Data.Db.P("@cl", JsonSerializer.Serialize(report.Claims)),
                ResuClean.Data.Db.P("@n", DateTime.UtcNow.ToString("O")));
        }
        catch
        {
            // Recording a report must never be the thing that fails the check.
        }
    }

    public List<FactCheckDto> Recent(int limit = 25) => ResuClean.Data.Db.Query(_db.Open(),
        "SELECT id, version_id, model, method, checked, ungrounded, created_at "
        + "FROM fact_check_reports ORDER BY created_at DESC, rowid DESC LIMIT @l",
        r => new FactCheckDto(
            ResuClean.Data.Db.Str(r, "id"), ResuClean.Data.Db.Str(r, "version_id"),
            ResuClean.Data.Db.Str(r, "model"), ResuClean.Data.Db.Str(r, "method"),
            ResuClean.Data.Db.Int(r, "checked"), ResuClean.Data.Db.Int(r, "ungrounded"),
            ResuClean.Data.Db.Str(r, "created_at")),
        ResuClean.Data.Db.P("@l", Math.Clamp(limit, 1, 200)));
}
