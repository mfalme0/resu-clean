using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Interview rehearsal: you answer the questions a posting is likely to produce, and the tool tells
/// you which claims in your answer are not backed by a verified fact. Everything is local.
///
/// A figure in your answer (a number, a percentage, a years figure) that does not appear anywhere in
/// your verified profile is a claim you would be making up under pressure. That is the failure this
/// exists to catch, before the interview room.
///
/// Works with no model: the grounding check is deterministic. A model, when configured, can phrase
/// the feedback more helpfully but cannot change the verdict — an ungrounded figure stays flagged.
/// </summary>
public sealed partial class InterviewRehearsalService
{
    private readonly Db _db;
    private readonly ProfileService _profile;
    private readonly InterviewService _interview;
    private readonly JobSearchService _jobs;

    public InterviewRehearsalService(Db db, ProfileService profile, InterviewService interview, JobSearchService jobs)
    {
        _db = db;
        _profile = profile;
        _interview = interview;
        _jobs = jobs;
    }

    public sealed record QuestionDto(
        string Question,
        string Probes,
        string GroundedIn,
        bool Risk,
        string RiskNote);

    public sealed record AnswerDto(
        string Id,
        string Question,
        string Answer,
        bool Grounded,
        string Feedback,
        string CreatedAt);

    public sealed record RehearsalState(
        List<QuestionDto> Questions,
        List<AnswerDto> Answers,
        bool UsedModel,
        string Method);

    /// <summary>Questions for this job, generated once and reused across rehearsals.</summary>
    public async Task<RehearsalState> GetOrCreate(string jobId, string? resumeVersionId = null, CancellationToken ct = default)
    {
        var job = _jobs.GetJob(jobId) ?? throw new KeyNotFoundException($"No job called '{jobId}'.");
        var versionId = resumeVersionId ?? job.BestVersionId ?? throw new KeyNotFoundException("Mark a resume version as best-fit first.");

        string resumeText;
        using (var conn = _db.Open())
        {
            resumeText = Db.QueryOne(conn, "SELECT text FROM resume_versions WHERE id = @v",
                r => Db.Str(r, "text"), Db.P("@v", versionId)) ?? throw new KeyNotFoundException("That resume version no longer exists.");
        }

        var report = await _interview.PredictAsync(jobId, versionId, job.Description, resumeText,
            new List<FactSupportService.Claim>(), ct).ConfigureAwait(false);

        var answers = ListAnswers(jobId);
        var questions = report.Questions.Select(q => new QuestionDto(q.Text, q.Probes, q.GroundedIn, q.Risk, q.RiskNote)).ToList();

        return new RehearsalState(questions, answers, report.UsedModel, report.Method);
    }

    /// <summary>Submit an answer to one question. Returns grounded status and feedback.</summary>
    public AnswerDto Submit(string jobId, string question, string answer)
    {
        var (grounded, feedback) = CheckGrounding(answer);
        var id = ProfileService.NewId("rehearsal");
        var now = DateTime.UtcNow.ToString("O");

        using var conn = _db.Open();
        Db.Exec(conn,
            "INSERT INTO rehearsal_answers (id, job_id, question, answer, grounded, feedback, created_at) " +
            "VALUES (@i,@j,@q,@a,@g,@f,@n)",
            Db.P("@i", id), Db.P("@j", jobId), Db.P("@q", question), Db.P("@a", answer),
            Db.P("@g", grounded ? 1 : 0), Db.P("@f", feedback), Db.P("@n", now));

        return new AnswerDto(id, question, answer, grounded, feedback, now);
    }

    /// <summary>All answers for a job, most recent first.</summary>
    public List<AnswerDto> ListAnswers(string jobId)
    {
        using var conn = _db.Open();
        return Db.Query(conn,
            "SELECT id, question, answer, grounded, feedback, created_at FROM rehearsal_answers " +
            "WHERE job_id = @j ORDER BY created_at DESC",
            r => new AnswerDto(
                Db.Str(r, "id"), Db.Str(r, "question"), Db.Str(r, "answer"),
                Db.Bool(r, "grounded"), Db.Str(r, "feedback"), Db.Str(r, "created_at")),
            Db.P("@j", jobId));
    }

    // ---- Grounding -----------------------------------------------------------

    /// <summary>
    /// Checks every figure in the answer against the verified profile. A figure that appears nowhere
    /// in the profile is ungrounded: you would be inventing it under pressure.
    /// </summary>
    private (bool Grounded, string Feedback) CheckGrounding(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return (false, "Say something — an empty answer teaches nothing.");

        var figures = FiguresIn(answer).Select(f => f.Trim().TrimEnd('.')).Where(f => f.Length > 0).ToList();
        if (figures.Count == 0)
        {
            // No figures at all: cannot prove it is grounded, but also cannot flag a specific number.
            return (true, "No figures to check. Keep every number you quote traceable to your profile.");
        }

        var facts = _profile.ListFacts();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in facts)
            foreach (var f in FiguresIn(fact.Text)) allowed.Add(f.Trim().TrimEnd('.'));

        var ungrounded = figures.Where(f => !allowed.Contains(f)).ToList();
        if (ungrounded.Count == 0)
            return (true, "Every figure in your answer appears in your verified profile. You can say this under pressure.");

        return (false, $"These figures are not in your verified profile: {string.Join(", ", ungrounded)}. " +
                       $"Do not quote them in the interview unless you can back them.");
    }

    private static IEnumerable<string> FiguresIn(string text) =>
        Figure().Matches(text).Select(m => m.Value);

    [GeneratedRegex(@"\b\d[\d,]*(?:\.\d+)?\s*(?:%|percent|k|m|bn|million|billion|thousand)?\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Figure();
}
