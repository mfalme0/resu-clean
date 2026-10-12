using System.Text.Json;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Predicts the technical questions a posting is likely to produce, and names which of your own
/// claims each one probes.
///
/// The valuable output is not "tell me about yourself". It is "they named Kubernetes under real
/// load, so they will probe whether you scaled a cluster or only deployed one" — which tells you
/// exactly which line on your resume is about to be tested. That is why the input is the actual
/// posting plus a named resume version and not a generic question bank.
///
/// The second half matters more than the first: a claim the interview will test is the same claim
/// the grounding check would flag. Linking the two turns two features into one warning.
/// </summary>
public sealed partial class InterviewService
{
    public const string Workflow = "interview";

    private readonly ModelRouter _router;
    private readonly ProfileService _profile;

    public InterviewService(ModelRouter router, ProfileService profile)
    {
        _router = router;
        _profile = profile;
    }

    /// <param name="GroundedIn">The phrase from the posting or resume this question is anchored to.</param>
    /// <param name="Risk">True when the claim this probes is not backed by a verified fact.</param>
    public sealed record Question(
        string Text,
        string Probes,
        string GroundedIn,
        bool Risk,
        string RiskNote);

    public sealed record Report(
        string JobId,
        string VersionId,
        IReadOnlyList<Question> Questions,
        bool UsedModel,
        string? Model,
        string Method,
        IReadOnlyList<string> Notes);

    /// <summary>
    /// A deterministic pass cannot write questions, but it can do the part that matters most:
    /// identify the claims on the resume that nothing in the profile supports. Those are the
    /// landmines, and finding them needs no provider.
    /// </summary>
    public Report RisksOnly(string jobId, string versionId, IReadOnlyList<FactSupportService.Claim> claims) =>
        new(jobId, versionId, Array.Empty<Question>(), false, null, "deterministic",
            claims.Where(c => !c.Grounded)
                .Select(c => c.Text)
                .ToList());

    public async Task<Report> PredictAsync(
        string jobId,
        string versionId,
        string posting,
        string resumeText,
        IReadOnlyList<FactSupportService.Claim> claims,
        CancellationToken ct = default)
    {
        var risky = claims.Where(c => !c.Grounded).Select(c => c.Text.Trim()).ToList();
        if (!_router.AnyConfigured(Workflow))
            return RisksOnly(jobId, versionId, claims);

        var system = ModelRouter.SystemPrompt(Workflow, _profile.BuildReference());
        var user =
            "From the posting and the resume below, predict the technical questions this person will actually be asked. " +
            "Anchor every question to a phrase that appears in one of them: it is the evidence that the question is likely. " +
            "For each, say which claim on the resume it probes. " +
            "Return a JSON array of {\"question\": string, \"probes\": string, \"groundedIn\": string} and nothing else.\n\n" +
            $"POSTING:\n<<<{posting}>>>\n\nRESUME:\n<<<{resumeText}>>>";

        var run = await _router.RunStructuredAsync(Workflow, system, user, Parse, ct: ct).ConfigureAwait(false);
        if (run.Value is null) return RisksOnly(jobId, versionId, claims);

        var questions = run.Value.Select(q => Annotate(q, risky)).ToList();
        return new Report(jobId, versionId, questions, true, run.Routed.Model, "model", new List<string>());
    }

    /// <summary>
    /// Marks a question as a risk when the claim it probes is one the profile does not back. That is
    /// the same finding the grounding screen shows, read from the other direction.
    /// </summary>
    private static Question Annotate(Question question, IReadOnlyList<string> risky)
    {
        var match = risky.FirstOrDefault(r =>
            !string.IsNullOrWhiteSpace(question.Probes) &&
            (r.Contains(question.Probes.Trim(), StringComparison.OrdinalIgnoreCase)
             || question.Probes.Contains(r.Trim(), StringComparison.OrdinalIgnoreCase)
             || Overlaps(r, question.Probes)));

        return match is null
            ? question with { Risk = false, RiskNote = "" }
            : question with
            {
                Risk = true,
                RiskNote = "This probes a claim your verified profile does not support. Fix the claim before the interview, not during it."
            };
    }

    private static bool Overlaps(string a, string b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        return ta.Count > 0 && tb.Count > 0 && ta.Intersect(tb).Count() >= 2;
    }

    private static HashSet<string> Tokens(string text) =>
        new(text.ToLowerInvariant()
                .Split(new[] { ' ', ',', '.', ';', ':', '(', ')', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 4),
            StringComparer.Ordinal);

    private static List<Question>? Parse(string reply)
    {
        var start = reply.IndexOf('[');
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<RawQuestion>>(reply[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed?
                .Where(q => !string.IsNullOrWhiteSpace(q.Question))
                .Select(q => new Question(
                    q.Question.Trim(),
                    q.Probes?.Trim() ?? "",
                    q.GroundedIn?.Trim() ?? "",
                    false, ""))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record RawQuestion(string Question, string? Probes, string? GroundedIn);
}