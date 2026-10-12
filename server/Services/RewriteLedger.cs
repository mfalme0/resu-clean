using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// The record of which model rewrites were worth accepting.
///
/// Each entry is one model proposal measured against its own baseline. This is an evaluation harness
/// built out of the scorer that already exists: it answers "does routing clean to this model make
/// my resume better or worse" with counts rather than impressions, and it is the only place in the
/// app that judges model output on a number instead of on how it reads.
/// </summary>
public sealed class RewriteLedger
{
    private readonly Db _db;

    public RewriteLedger(Db db) => _db = db;

    public string Record(
        string workflow,
        ModelRouter.RoutedResult routed,
        string versionId,
        RewriteDecision decision,
        string? providerId = null)
    {
        var id = ProfileService.NewId("rwv");
        try
        {
            using var conn = _db.Open();
            Db.Exec(conn,
                "INSERT INTO rewrite_evaluations (id, workflow, provider_id, provider_name, model, version_id, "
                + "baseline_score, candidate_score, baseline_missing, candidate_missing, accepted, reason, created_at) "
                + "VALUES (@i,@w,@pid,@pn,@m,@v,@bs,@cs,@bm,@cm,@a,@r,@n)",
                Db.P("@i", id), Db.P("@w", workflow),
                Db.P("@pid", providerId ?? ""), Db.P("@pn", routed.Provider ?? ""), Db.P("@m", routed.Model ?? ""),
                Db.P("@v", versionId),
                Db.P("@bs", decision.BaselineScore), Db.P("@cs", decision.CandidateScore),
                Db.P("@bm", decision.BaselineMissing), Db.P("@cm", decision.CandidateMissing),
                Db.P("@a", decision.Accepted ? 1 : 0), Db.P("@r", decision.Reason),
                Db.P("@n", DateTime.UtcNow.ToString("O")));
        }
        catch
        {
            // Judging a rewrite must never be the thing that fails.
        }
        return id;
    }

    /// <summary>Accept rate per workflow, so a model earning its place is visible.</summary>
    public List<RewriteLedgerRow> Summary() => Db.Query(_db.Open(),
        "SELECT workflow, COUNT(*) AS total, SUM(accepted) AS accepted, model, "
        + "CAST(AVG(candidate_score - baseline_score) AS INTEGER) AS avg_delta "
        + "FROM rewrite_evaluations GROUP BY workflow, model ORDER BY workflow, model",
        r => new RewriteLedgerRow(
            Db.Str(r, "workflow"), Db.Str(r, "model"), Db.Int(r, "total"),
            Db.Int(r, "accepted"), Db.Int(r, "avg_delta")));

    public List<RewriteEvaluationDto> Recent(int limit = 50) => Db.Query(_db.Open(),
        "SELECT id, workflow, model, baseline_score, candidate_score, baseline_missing, candidate_missing, accepted, reason, created_at "
        + "FROM rewrite_evaluations ORDER BY created_at DESC, rowid DESC LIMIT @l",
        r => new RewriteEvaluationDto(
            Db.Str(r, "id"), Db.Str(r, "workflow"), Db.Str(r, "model"),
            Db.Int(r, "baseline_score"), Db.Int(r, "candidate_score"),
            Db.Int(r, "baseline_missing"), Db.Int(r, "candidate_missing"),
            Db.Bool(r, "accepted"), Db.Str(r, "reason"), Db.Str(r, "created_at")),
        Db.P("@l", Math.Clamp(limit, 1, 500)));
}

public sealed record RewriteLedgerRow(string Workflow, string Model, int Total, int Accepted, int AvgDelta);