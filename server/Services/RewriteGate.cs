using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Decides whether a model rewrite is actually an improvement, by measuring it instead of trusting it.
///
/// A rewrite that reads better but scores worse is still a worse resume for the purpose it exists
/// for, so the rule is mechanical: the candidate must not lose ATS points, and it must not lose
/// keyword coverage against the posting being targeted. Both conditions, because each catches
/// something the other misses — a rewrite can tighten prose while dropping a keyword the ATS wanted,
/// and it can stuff keywords while wrecking the structure that scores.
///
/// Rejection is not deletion. The candidate text and the reason come back with the decision, because
/// a user who can see why a rewrite was refused can still judge it themselves, and a user who cannot
/// is stuck trusting a heuristic.
/// </summary>
public static class RewriteGate
{
    public static RewriteDecision Evaluate(string baselineText, string candidateText, string? jobDescription)
    {
        var baseline = AtsScorer.Score("baseline", baselineText, jobDescription);
        var candidate = AtsScorer.Score("candidate", candidateText, jobDescription);

        // A posting with no text yields no keyword check at all; comparing those counts would be
        // comparing two meaningless zeros, so the keyword condition is skipped rather than passed.
        var baselineMissing = baseline.Keywords?.MissingCount ?? 0;
        var candidateMissing = candidate.Keywords?.MissingCount ?? 0;
        var hasPosting = baseline.Keywords is not null;

        if (candidate.Score < baseline.Score)
        {
            return new RewriteDecision(false, baseline.Score, candidate.Score,
                baselineMissing, candidateMissing, candidateText,
                $"The rewrite scored {baseline.Score - candidate.Score} points lower ({baseline.Score} to {candidate.Score}). The original was kept.");
        }

        if (hasPosting && candidateMissing > baselineMissing)
        {
            var lost = candidateMissing - baselineMissing;
            return new RewriteDecision(false, baseline.Score, candidate.Score,
                baselineMissing, candidateMissing, candidateText,
                $"The rewrite lost {lost} job-description {Plural(lost, "keyword")} ({baselineMissing} to {candidateMissing} missing). The original was kept.");
        }

        var reason = candidate.Score > baseline.Score
            ? $"Accepted: the rewrite scored {candidate.Score - baseline.Score} points higher ({baseline.Score} to {candidate.Score})."
            : hasPosting && candidateMissing < baselineMissing
                ? $"Accepted: the rewrite covered {Plural(baselineMissing - candidateMissing, "more job-description keyword")} at the same score."
                : "Accepted: the rewrite matched the original's score and keyword coverage, so it was taken on wording alone.";

        return new RewriteDecision(true, baseline.Score, candidate.Score,
            baselineMissing, candidateMissing, candidateText, reason);
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <param name="CandidateText">The rejected rewrite, kept so the user can still read and use it.</param>
public sealed record RewriteDecision(
    bool Accepted,
    int BaselineScore,
    int CandidateScore,
    int BaselineMissing,
    int CandidateMissing,
    string CandidateText,
    string Reason);