using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Flags the things about a posting that a ranking cannot see.
///
/// A job list can order by overlap and never notice that Samsung's "System Administrator" is really
/// service operations, that a posting is seventeen months old, or that "Worldwide" means the US and
/// EU and not Nairobi. The last one is the expensive mistake: you do not discover it until after the
/// cover letter is written.
///
/// Staleness is arithmetic and needs no model. Title mismatch and eligibility caveats are judgement,
/// so a model can add them, but only when it can quote the posting for the flag it raises. A caveat
/// without a quote is a guess about someone else's hiring policy, and those are not welcome here.
/// </summary>
public sealed partial class JobQualityService
{
    public const string Workflow = "job_quality";

    /// <summary>Postings older than this are called stale. Days, not months, so the rule is visible.</summary>
    public const int StaleAfterDays = 45;

    private readonly ModelRouter _router;
    private readonly ProfileService _profile;

    public JobQualityService(ModelRouter router, ProfileService profile)
    {
        _router = router;
        _profile = profile;
    }

    public sealed record Flag(string Kind, string Severity, string Detail, string? Quote);

    public sealed record Report(string JobId, IReadOnlyList<Flag> Flags, bool UsedModel, string Method)
    {
        public bool HasWarnings => Flags.Any(f => f.Severity == "warn");
    }

    [GeneratedRegex(@"(?i)\b(worldwide|anywhere|global|remote[- ]first|work from anywhere|distributed team)\b", RegexOptions.None, 2000)]
    private static partial Regex BroadRemoteClaim();

    // "authorised" is as common as "authorized" on the boards this reads, so both spellings count.
    [GeneratedRegex(@"(?i)\b(?:must|required to|only)\b[^.]{0,80}\b(?:be (?:based|located|resident)|authori[sz]\w*|eligible to work|work permit|right to work|citizens?)\b", RegexOptions.None, 2000)]
    private static partial Regex EligibilityRestriction();

    [GeneratedRegex(@"(?i)\b(us|u\.s\.|usa|united states|eu|european union|uk|canada|emea|apac)\s*(?:only|based|residents?|citizens?|time ?zones?)", RegexOptions.None, 2000)]
    private static partial Regex RegionRestriction();

    private static readonly string[] OpsWords =
    {
        "service desk", "helpdesk", "help desk", "ticket", "sla", "incident", "shift", "rota", "call centre",
        "call center", "level 1", "l1", "l2", "escalation", "onboarding users", "password reset"
    };

    private static readonly string[] EngineeringWords =
    {
        "design", "architect", "build", "implement", "codebase", "infrastructure", "deploy", "pipeline",
        "stack", "api", "database", "kubernetes", "terraform", "ci/cd"
    };

    public Report Analyse(string jobId, JobDto job)
    {
        var flags = new List<Flag>();
        flags.AddRange(Staleness(job));
        flags.AddRange(TitleMismatch(job));
        flags.AddRange(Eligibility(job));
        flags.AddRange(Closure(job));

        return new Report(jobId, flags, false, "deterministic");
    }

    /// <summary>
    /// Has this posting actually closed? Three ways to tell, in the order they can be trusted.
    ///
    /// A board saying "no longer accepting applications" is stating a fact about its own listing and
    /// outranks everything else. A stated closing date that has passed is arithmetic. And a deadline
    /// that has not passed says nothing at all — it is not evidence the role is open, it is only
    /// evidence that it has not been given up, which is why it is reported as information.
    ///
    /// The case this exists for: a posting two days old whose deadline was yesterday. Staleness looks
    /// at the posted date and sees nothing, so without this the tracker carried a deadline for several
    /// rounds of planning while the page had been saying "expired" the whole time.
    /// </summary>
    private static IEnumerable<Flag> Closure(JobDto job)
    {
        if (DeadlineReader.StatesClosed(job.Description))
        {
            yield return new Flag("closed", "warn",
                "The posting itself says this role is closed. Do not spend an evening on it.",
                null);
            yield break;
        }

        if (job.DeadlineAt is not { Length: > 0 } stated) yield break;
        if (!DateTime.TryParse(stated, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            yield break;

        var days = (when - DateTime.UtcNow).TotalDays;
        if (days < 0)
        {
            // The plural is a local because a ternary cannot sit inside an interpolation directly:
            // the compiler reads the colon as the start of a format specifier.
            var ago = (int)-days;
            var plural = ago == 1 ? "" : "s";
            yield return new Flag("closed", "warn",
                $"The posting's own closing date passed {ago} day{plural} ago. It states it closed on {when:yyyy-MM-dd}.",
                null);
        }
        else if (days <= 7)
        {
            var left = (int)Math.Ceiling(days);
            var plural = left == 1 ? "" : "s";
            yield return new Flag("deadline", "info",
                $"This posting states it closes on {when:yyyy-MM-dd}, in {left} day{plural}. "
                + "That is not evidence it is still easy to get.",
                null);
        }
    }

    /// <summary>The one part that is pure arithmetic, and therefore never wrong.</summary>
    private static IEnumerable<Flag> Staleness(JobDto job)
    {
        if (string.IsNullOrWhiteSpace(job.PostedAt)) yield break;

        // Posted dates arrive over the wire as ISO-8601 with a Z, and a plain TryParse converts
        // them to local time. Subtract local from UTC and the day count drifts by the machine's
        // offset, which is how a 520-day-old posting reports 519. Assume UTC, then convert, so the
        // arithmetic is the same everywhere.
        if (!DateTime.TryParse(job.PostedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var posted))
            yield break;

        var days = (DateTime.UtcNow - DateTime.SpecifyKind(posted, DateTimeKind.Utc)).TotalDays;
        if (days < StaleAfterDays) yield break;

        yield return new Flag("stale", days > 180 ? "warn" : "info",
            $"Posted {(int)days} days ago. Boards rarely close a listing, so this may be filled already.",
            null);
    }

    /// <summary>
    /// Does the title match what the description actually describes? A "System Administrator" whose
    /// text is all tickets and SLAs is a service-desk job wearing an engineering title, and that
    /// changes whether it is worth applying at all.
    /// </summary>
    private static IEnumerable<Flag> TitleMismatch(JobDto job)
    {
        if (string.IsNullOrWhiteSpace(job.Description) || job.Description.Length < 120) yield break;

        var lower = job.Description.ToLowerInvariant();
        var title = job.Title.ToLowerInvariant();
        var ops = OpsWords.Count(lower.Contains);
        var eng = EngineeringWords.Count(lower.Contains);

        var titleLooksEngineering = eng > 0 || title.Contains("engineer") || title.Contains("developer")
            || title.Contains("admin") || title.Contains("architect");
        if (!titleLooksEngineering) yield break;

        // Only flag when the description is dominated by operations and contains almost no
        // engineering language, so a genuine engineering rota role is not mislabelled.
        if (ops >= 3 && eng <= 1)
        {
            yield return new Flag("title-mismatch", "warn",
                "The title reads as an engineering role but the description is dominated by ticket, shift and SLA language. This is likely service operations.",
                null);
        }
    }

    /// <summary>
    /// "Remote" and "Worldwide" are not promises about your country. The posting has to actually
    /// restrict eligibility before this fires, and the restriction is quoted so you can read it.
    /// </summary>
    private static IEnumerable<Flag> Eligibility(JobDto job)
    {
        if (string.IsNullOrWhiteSpace(job.Description)) yield break;

        var broad = BroadRemoteClaim().Match(job.Description);
        var restriction = EligibilityRestriction().Match(job.Description);
        var region = RegionRestriction().Match(job.Description);

        if (restriction.Success)
        {
            yield return new Flag("eligibility", "warn",
                "The posting states a work-authorisation requirement. Confirm you meet it before applying.",
                Sentence(job.Description, restriction));
        }
        else if (region.Success)
        {
            yield return new Flag("eligibility", "warn",
                "The posting names a region, so \"remote\" likely means that region only.",
                Sentence(job.Description, region));
        }
        else if (broad.Success && !job.Remote)
        {
            yield return new Flag("eligibility", "info",
                "The posting claims to be location-agnostic but the listing is not marked remote. Check where it is actually based.",
                Sentence(job.Description, broad));
        }
    }

    /// <summary>
    /// The whole sentence a match sits in, not just the words that matched.
    ///
    /// Quoting "must be authorised" back at someone tells them nothing; quoting "Candidates must be
    /// authorised to work in Kenya for this role" tells them the restriction and who it applies to.
    /// The point of a quote is that it can be read as the posting actually wrote it.
    /// </summary>
    private static string Sentence(string description, Match match)
    {
        var start = 0;
        for (var i = match.Index - 1; i >= 0; i--)
        {
            if (description[i] is '.' or '!' or '?' or '\n') { start = i + 1; break; }
        }

        var end = description.Length;
        for (var i = match.Index + match.Length; i < description.Length; i++)
        {
            if (description[i] is '.' or '!' or '?' or '\n') { end = i + 1; break; }
        }

        var quote = description[start..end].Trim().TrimEnd('.', ',', ';');
        return quote.Length > 240 ? quote[..240] + "…" : quote;
    }

    // ---- Model pass (optional) ---------------------------------------------

    /// <summary>
    /// A model reads the posting and raises mismatches with a quote. A flag with no verbatim quote is
    /// discarded, because the only thing that makes a claim about a hiring policy defensible is the
    /// posting saying it.
    /// </summary>
    public async Task<Report> AnalyseAsync(string jobId, JobDto job, CancellationToken ct = default)
    {
        var deterministic = Analyse(jobId, job);
        if (!_router.AnyConfigured(Workflow) || string.IsNullOrWhiteSpace(job.Description))
            return deterministic;

        var system = ModelRouter.SystemPrompt(Workflow, _profile.BuildReference());
        var user =
            $"TITLE: {job.Title}\nCOMPANY: {job.Company}\nLOCATION: {job.Location}\n\nPOSTING:\n<<<{job.Description}>>>\n\n" +
            "List problems with this posting as a JSON array. Each entry: {\"kind\": string, \"severity\": \"info\"|\"warn\", " +
            "\"detail\": string, \"quote\": string}. Raise \"title-mismatch\" when the title and the description disagree about " +
            "the role, and \"eligibility\" when the posting restricts who may apply. \"quote\" MUST be copied verbatim from the " +
            "posting; if you cannot quote it, do not raise it. Return a JSON array and nothing else.";

        var run = await _router.RunStructuredAsync(Workflow, system, user, ParseFlags, ct: ct).ConfigureAwait(false);
        if (run.Value is null) return deterministic;

        // The model cannot subtract the deterministic findings, only add to them.
        var merged = deterministic.Flags.ToList();
        foreach (var flag in run.Value)
        {
            if (string.IsNullOrWhiteSpace(flag.Quote)) continue;
            if (job.Description.IndexOf(flag.Quote.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (merged.Any(f => f.Kind == flag.Kind && f.Detail == flag.Detail)) continue;
            merged.Add(flag);
        }
        return new Report(jobId, merged, true, "model");
    }

    private sealed record ModelFlag(string Kind, string Severity, string Detail, string Quote);

    private static List<Flag>? ParseFlags(string reply)
    {
        var start = reply.IndexOf('[');
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<ModelFlag>>(reply[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (parsed is null) return null;
            return parsed
                .Where(f => !string.IsNullOrWhiteSpace(f.Detail))
                .Select(f => new Flag(
                    string.IsNullOrWhiteSpace(f.Kind) ? "other" : f.Kind.Trim().ToLowerInvariant(),
                    f.Severity?.ToLowerInvariant() == "warn" ? "warn" : "info",
                    f.Detail.Trim(),
                    string.IsNullOrWhiteSpace(f.Quote) ? null : f.Quote.Trim()))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}