using System.Text.RegularExpressions;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Model-assisted workflows. Each one is prompt-first, then validation-first: any new information
/// the model asserts goes to the pending queue instead of straight into output.
/// </summary>
public sealed class ResumeWorkflows
{
    private readonly Db _db;
    private readonly ProfileService _profile;
    private readonly ModelRouter _router;

    public ResumeWorkflows(Db db, ProfileService profile, ModelRouter router)
    {
        _db = db;
        _profile = profile;
        _router = router;
    }

    private sealed record VersionRow(string ResumeId, string Label, string Text);

    private VersionRow LoadVersion(string versionId)
    {
        using var conn = _db.Open();
        return Db.QueryOne(conn, "SELECT resume_id, label, text FROM resume_versions WHERE id = @id",
            r => new VersionRow(Db.Str(r, "resume_id"), Db.Str(r, "label"), Db.Str(r, "text")), Db.P("@id", versionId))
            ?? throw new KeyNotFoundException($"Resume version '{versionId}' does not exist.");
    }

    // ---- Clean -------------------------------------------------------------

    public sealed record CleanOutcome(CleanResult Result, string? VersionId);

    public async Task<CleanOutcome> CleanAsync(string versionId, CleanRequest request, CancellationToken ct = default)
    {
        var version = LoadVersion(versionId);
        var deterministic = Cleaner.Run(version.Text);

        var useModel = request.UseModel ?? true;
        if (!useModel)
        {
            return new CleanOutcome(
                new CleanResult(versionId, deterministic.Text, false, null, Changes(deterministic), deterministic.Warnings),
                SaveVersion(version.ResumeId, versionId, "Cleaned", deterministic.Text, "clean"));
        }

        var system = ModelRouter.SystemPrompt("clean", _profile.BuildReference());
        var user = BuildCleanPrompt(version.Text, deterministic.Text, request.Instruction);
        var routed = await _router.RunAsync("clean", system, user, ct).ConfigureAwait(false);

        var warnings = new List<string>(deterministic.Warnings);
        warnings.AddRange(routed.Attempts.Where(a => a.Contains("Skipped") || a.Contains("failed")));

        if (!routed.UsedModel || string.IsNullOrWhiteSpace(routed.Text))
        {
            return new CleanOutcome(
                new CleanResult(versionId, deterministic.Text, false, null, Changes(deterministic),
                    warnings.Append("No model was available, so only the deterministic clean was applied.").ToList()),
                SaveVersion(version.ResumeId, versionId, "Cleaned", deterministic.Text, "clean"));
        }

        var (body, notes) = SplitNotes(routed.Text);
        var validated = Validate(body, version.Text);
        warnings.AddRange(validated.Notes);
        warnings.AddRange(notes);

        return new CleanOutcome(
            new CleanResult(versionId, validated.Text.Length > 0 ? validated.Text : deterministic.Text, true, routed.Model,
                Changes(deterministic), warnings),
            SaveVersion(version.ResumeId, versionId, "Cleaned", validated.Text.Length > 0 ? validated.Text : deterministic.Text, "clean"));
    }

    private static IReadOnlyList<CleanChange> Changes(Cleaner.Result result) =>
        result.Changes.Select(c => new CleanChange(c.Kind, c.Detail, c.Count)).ToList();

    private string BuildCleanPrompt(string original, string deterministic, string? instruction)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("ORIGINAL RESUME (source of every fact):");
        sb.AppendLine("<<<");
        sb.AppendLine(original);
        sb.AppendLine(">>>");
        sb.AppendLine();
        sb.AppendLine("DETERMINISTICALLY CLEANED VERSION (use this as the baseline):");
        sb.AppendLine("<<<");
        sb.AppendLine(deterministic);
        sb.AppendLine(">>>");
        if (!string.IsNullOrWhiteSpace(instruction))
        {
            sb.AppendLine();
            sb.AppendLine("USER INSTRUCTION:");
            sb.AppendLine(instruction.Trim());
        }
        return sb.ToString();
    }

    // ---- ATS ---------------------------------------------------------------

    public async Task<AtsReport> AtsAsync(string versionId, AtsRequest request, CancellationToken ct = default)
    {
        var version = LoadVersion(versionId);
        var report = AtsScorer.Score(versionId, version.Text, request.JobDescription);

        var useModel = request.UseModel ?? true;
        if (!useModel)
            return report;

        var system = ModelRouter.SystemPrompt("ats", _profile.BuildReference());
        var failed = report.Checks.Where(c => !c.Passed).Select(c => $"- {c.Label}: {c.Tip}").ToList();
        var user = BuildAtsPrompt(report, failed, version.Text, request.JobDescription);
        var routed = await _router.RunAsync("ats", system, user, ct).ConfigureAwait(false);

        if (!routed.UsedModel || string.IsNullOrWhiteSpace(routed.Text)) return report;

        var fixes = ParseFixes(routed.Text);
        return report with { UsedModel = true, Model = routed.Model, PrioritizedFixes = fixes };
    }

    private static string BuildAtsPrompt(AtsReport report, IReadOnlyList<string> failedTips, string resumeText, string? jobDescription)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Rule-based ATS score: {report.Score}/100 ({report.Verdict}).");
        sb.AppendLine("Failed checks and their tips:");
        foreach (var tip in failedTips) sb.AppendLine(tip);
        if (!string.IsNullOrWhiteSpace(jobDescription))
        {
            sb.AppendLine();
            sb.AppendLine($"Keyword match: {report.Keywords?.MatchPct}% matched, {report.Keywords?.MissingCount} missing.");
            sb.AppendLine("Missing keywords: " + string.Join(", ", report.Keywords?.Missing.Take(25) ?? Array.Empty<string>()));
        }
        return sb.ToString();
    }

    private static List<string> ParseFixes(string text)
    {
        var fixes = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("FIXES:", StringComparison.OrdinalIgnoreCase)) continue;
            line = Regex.Replace(line, @"^[\d]+[.)]\s*", "");
            line = Regex.Replace(line, @"^[-*•]\s*", "");
            if (line.Length is < 8 or > 300) continue;
            fixes.Add(line);
            if (fixes.Count == 8) break;
        }
        return fixes;
    }

    // ---- Update resume -----------------------------------------------------

    public async Task<UpdateResumeResponse> UpdateAsync(string versionId, UpdateResumeRequest request, CancellationToken ct = default)
    {
        var version = LoadVersion(versionId);
        var notes = new List<string>();
        var pending = new List<PendingFactDto>();

        // Any explicit new info in the instruction is queued for approval before anything is written.
        if (!string.IsNullOrWhiteSpace(request.Instruction))
        {
            var claims = await ExtractNewFactsAsync(version.Text, request.JobDescription, request.Instruction, notes, ct).ConfigureAwait(false);
            pending.AddRange(claims);
        }

        // NEW INFO RULE: if there is unapproved new information, stop and ask.
        if (pending.Count > 0)
        {
            return new UpdateResumeResponse(null, version.Text, false, null, pending, true, new[]
            {
                "Stopped before making changes: the instruction contained information that is not in your resume or your verified profile.",
                "Approve or reject each item below, then run the update again."
            });
        }

        var system = ModelRouter.SystemPrompt("update_resume", _profile.BuildReference());
        var user = BuildUpdatePrompt(version.Text, request.JobDescription, request.Instruction);
        var routed = await _router.RunAsync("update_resume", system, user, ct).ConfigureAwait(false);

        if (!routed.UsedModel || string.IsNullOrWhiteSpace(routed.Text))
        {
            return new UpdateResumeResponse(null, version.Text, false, null, pending, false, new[]
            {
                "No model route is configured for 'update_resume', so the resume was not rewritten.",
                "Add a route in Models & Routes, or use the deterministic Clean workflow."
            });
        }

        var (body, modelNotes) = SplitNotes(routed.Text);
        var (newInfo, _) = SplitSections(body, "NEW INFO:");
        var validated = Validate(body, version.Text);

        // Anything the model claims as new goes to pending, never to the output.
        if (!string.IsNullOrWhiteSpace(newInfo))
            pending.AddRange(_profile.AddFactToPending("other", newInfo.Trim(), "resume_update", context: request.Instruction ?? ""));

        if (pending.Count > 0)
        {
            return new UpdateResumeResponse(null, version.Text, true, routed.Model, pending, true,
                new[] { "The model introduced information that is not in your resume or profile. Approve each item before the update is applied." }
                    .Concat(modelNotes).ToList());
        }

        notes.AddRange(modelNotes);
        notes.AddRange(validated.Notes);
        notes.Add("Only facts from your resume and approved profile were used.");

        return new UpdateResumeResponse(
            SaveVersion(version.ResumeId, versionId, "Tailored", validated.Text, "update_resume"),
            validated.Text, true, routed.Model, pending, false, notes);
    }

    private async Task<List<PendingFactDto>> ExtractNewFactsAsync(string resumeText, string? jobDescription, string instruction, List<string> notes, CancellationToken ct)
    {
        // The model is asked only to list candidate new facts, never to rewrite the resume.
        var system = ModelRouter.SystemPrompt("profile", _profile.BuildReference());
        var user =
            "Below is a user's instruction to update their resume. List only the pieces of NEW factual information the user supplied " +
            "(employers, titles, dates, metrics, skills, certifications, education) that do NOT already appear in the resume text or the profile. " +
            "Return one standalone fact per line. If the instruction adds nothing new, return exactly: none\n\n" +
            $"RESUME:\n<<<{resumeText}>>>\n\n" +
            (string.IsNullOrWhiteSpace(jobDescription) ? "" : $"JOB DESCRIPTION:\n<<<{jobDescription}>>>\n\n") +
            $"INSTRUCTION:\n<<<{instruction}>>>";

        var routed = await _router.RunAsync("profile", system, user, ct).ConfigureAwait(false);
        if (!routed.UsedModel) return new List<PendingFactDto>();

        var facts = new List<PendingFactDto>();
        foreach (var raw in routed.Text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length < 4 || line.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Length > 300) continue;
            facts.AddRange(_profile.AddFactToPending(GuessKind(line), line, "resume_update", context: instruction));
        }
        return facts;
    }

    private static string GuessKind(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("certificate") || lower.Contains("certified") || lower.Contains("license")) return "cert";
        if (lower.Contains("degree") || lower.Contains("university") || lower.Contains("college") || lower.Contains("diploma")) return "education";
        if (lower.Contains("skill") || lower.Contains("proficient") || lower.Contains("experience with")) return "skill";
        if (Regex.IsMatch(text, @"(?i)\b(19|20)\d{2}\b")) return "role";
        return "achievement";
    }

    private string BuildUpdatePrompt(string resumeText, string? jobDescription, string? instruction)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("CURRENT RESUME (the only source of facts):");
        sb.AppendLine("<<<");
        sb.AppendLine(resumeText);
        sb.AppendLine(">>>");
        if (!string.IsNullOrWhiteSpace(jobDescription))
        {
            sb.AppendLine();
            sb.AppendLine("JOB DESCRIPTION (mirror its wording only where it is truthful):");
            sb.AppendLine("<<<");
            sb.AppendLine(jobDescription);
            sb.AppendLine(">>>");
        }
        if (!string.IsNullOrWhiteSpace(instruction))
        {
            sb.AppendLine();
            sb.AppendLine("INSTRUCTION:");
            sb.AppendLine(instruction.Trim());
        }
        return sb.ToString();
    }

    // ---- Shared validation -------------------------------------------------

    public sealed record Validated(string Text, IReadOnlyList<string> Notes);

    /// <summary>
    /// Final guard before anything reaches the user: look for numbers, dates and proper nouns in
    /// the output that are absent from the input. If found, the output is rejected and the caller
    /// falls back. This is the last line of defence against a fabricated achievement.
    /// </summary>
    public Validated Validate(string output, string input)
    {
        var notes = new List<string>();
        var inventedNumbers = InventedTokens(output, input, @"\b\d[\d,\.]*\b");
        var inventedDates = InventedTokens(output, input, @"\b(?:19|20)\d{2}\b");
        var inventedProper = InventedTokens(output, input, @"\b[A-Z][a-zA-Z]{3,}\b", skipCommon: true);

        if (inventedNumbers.Count > 0 || inventedDates.Count > 0)
        {
            notes.Add($"Rejected the model's output because it introduced numbers or dates that are not in your resume ({string.Join(", ", inventedNumbers.Concat(inventedDates).Take(8))}). The deterministic version was used instead.");
            return new Validated(string.Empty, notes);
        }
        if (inventedProper.Count > 0)
            notes.Add("Check these names appeared in your source material: " + string.Join(", ", inventedProper.Take(8)) + ".");

        return new Validated(output.Trim(), notes);
    }

    private static List<string> InventedTokens(string output, string input, string pattern, bool skipCommon = false)
    {
        var allowed = new HashSet<string>(Regex.Matches(input, pattern).Select(m => m.Value), StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        foreach (Match m in Regex.Matches(output, pattern))
        {
            var value = m.Value.Trim(' ', ',', '.');
            if (value.Length == 0) continue;
            if (allowed.Contains(value)) continue;
            if (skipCommon && !value.Any(char.IsUpper) ) continue;
            if (found.Contains(value, StringComparer.OrdinalIgnoreCase)) continue;
            found.Add(value);
            if (found.Count >= 12) break;
        }
        return found;
    }

    private static (string Body, IReadOnlyList<string> Notes) SplitNotes(string raw)
    {
        var notes = new List<string>();
        var (body, noteText) = SplitSections(raw, "NOTES:");
        if (!string.IsNullOrWhiteSpace(noteText))
        {
            foreach (var line in noteText.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = Regex.Replace(line.Trim(), @"^[-*•]\s*", "");
                if (trimmed.Length > 0) notes.Add(trimmed);
            }
        }
        return (body.Trim(), notes);
    }

    private static (string Body, string Section) SplitSections(string raw, string marker)
    {
        var index = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return (raw, string.Empty);
        return (raw[..index], raw[(index + marker.Length)..]);
    }

    private string SaveVersion(string resumeId, string parentId, string label, string text, string sourceKind)
    {
        var id = ProfileService.NewId("ver");
        var now = DateTime.UtcNow.ToString("O");
        using var conn = _db.Open();
        var tx = conn.BeginTransaction();
        try
        {
            Db.Exec(conn,
                "INSERT INTO resume_versions (id, resume_id, label, text, source_kind, parent_id, best_fit, created_at) VALUES (@id,@r,@l,@t,@s,@p,0,@n)",
                Db.P("@id", id), Db.P("@r", resumeId), Db.P("@l", label), Db.P("@t", text), Db.P("@s", sourceKind), Db.P("@p", parentId), Db.P("@n", now));
            Db.Exec(conn, "UPDATE resumes SET current_version_id = @v, updated_at = @n WHERE id = @r", Db.P("@v", id), Db.P("@n", now), Db.P("@r", resumeId));
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return id;
    }
}