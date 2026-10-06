using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ResuClean.Data;
using ResuClean.Models;
using ResuClean.Services.Kits;

namespace ResuClean.Services;

/// <summary>
/// Assembles application kits and tracks them. The tool prepares files; the user sends them.
/// Nothing in this class transmits anything.
/// </summary>
public sealed class ApplicationService
{
    private readonly Db _db;
    private readonly JobSearchService _jobs;
    private readonly ResumeStore _resumes;
    private readonly ProfileService _profile;
    private readonly ModelRouter _router;

    public ApplicationService(Db db, JobSearchService jobs, ResumeStore resumes, ProfileService profile, ModelRouter router)
    {
        _db = db;
        _jobs = jobs;
        _resumes = resumes;
        _profile = profile;
        _router = router;
    }

    private static string Now() => DateTime.UtcNow.ToString("O");

    /// <summary>Server-side paths for a kit. Never serialised to the client.</summary>
    public sealed record KitFiles(string? Eml, string? Zip, string? EmailText, string? CoverPdf, string? CoverDocx);

    public async Task<(KitDto Kit, KitFiles Files)> CreateKitAsync(CreateKitRequest request, Configuration.AppConfig app, CancellationToken ct = default)
    {
        var job = _jobs.GetJob(request.JobId) ?? throw new KeyNotFoundException($"Job '{request.JobId}' does not exist.");
        var versionId = request.ResumeVersionId ?? job.BestVersionId ?? BestFitVersion();
        if (string.IsNullOrWhiteSpace(versionId))
            throw new InvalidOperationException("No resume version was selected and no best-fit version is marked. Add a resume first, or mark a version as best fit.");

        var versionText = _resumes.GetText(versionId) ?? throw new KeyNotFoundException("That resume version no longer exists.");
        var profile = _profile.GetProfile();
        var facts = _profile.ListFacts();
        var fullName = NameFor(profile);

        var (subject, body) = KitBuilder.TemplateEmail(fullName, job.Title, job.Company, profile, facts);
        var generatedBy = "template";
        var toAddress = ExtractEmail(job.Description);
        var applyUrl = job.Url ?? "";

        if (request.UseModel)
        {
            var system = ModelRouter.SystemPrompt("apply_email", _profile.BuildReference());
            var user =
                $"Write the application email for this role.\n\n" +
                $"ROLE: {job.Title}\nCOMPANY: {job.Company}\nLOCATION: {job.Location}\n\n" +
                $"JOB DESCRIPTION:\n<<<{Trim(job.Description, 4000)}>>>\n\n" +
                $"MY RESUME:\n<<<{Trim(versionText, 6000)}>>>\n\n" +
                $"TONE: {profile.Voice}\n\nRules: under 150 words. No placeholders such as [Company] or YOUR NAME. " +
                $"Mention at most two specific facts from my resume. Plain text only.";

            var routed = await _router.RunAsync("apply_email", system, user, ct).ConfigureAwait(false);
            if (routed.UsedModel && !string.IsNullOrWhiteSpace(routed.Text))
            {
                var candidate = StripSubjectLine(routed.Text);
                if (KitBuilder.WordCount(candidate) <= 170 && !ContainsPlaceholder(candidate))
                {
                    body = candidate;
                    generatedBy = "model";
                }
            }
        }

        var coverLetter = request.CoverLetter
            ? await BuildCoverLetterAsync(job, versionText, fullName, ct).ConfigureAwait(false)
            : string.Empty;

        var dir = Path.Combine(app.DataDir, "kits", request.JobId);
        Directory.CreateDirectory(dir);
        var kitId = ProfileService.NewId("kit");

        var displayName = _resumes.GetLabel(versionId) ?? "resume";
        var (docxPath, pdfPath) = _resumes.EnsureDocuments(versionId, displayName);

        string? coverPdfPath = null;
        string? coverDocxPath = null;
        if (!string.IsNullOrWhiteSpace(coverLetter))
        {
            coverDocxPath = Path.Combine(dir, $"cover-letter-{kitId}.docx");
            coverPdfPath = Path.Combine(dir, $"cover-letter-{kitId}.pdf");
            Extractor.WriteDocx(coverDocxPath, coverLetter, "Cover letter");
            PdfWriter.WriteCoverLetter(coverPdfPath, coverLetter, fullName);
        }

        var emlPath = Path.Combine(dir, $"application-{kitId}.eml");
        var fromAddress = profile.Contact.TryGetValue("email", out var from) ? from : "";
        var resumeFileName = $"{ResumeStore.SafeName(fullName)}-Resume{Path.GetExtension(pdfPath ?? ".pdf")}";
        KitBuilder.WriteEml(emlPath, fromAddress, toAddress, subject, body, pdfPath ?? "", resumeFileName);

        var emailTextPath = Path.Combine(dir, $"email-{kitId}.txt");
        var header = new System.Text.StringBuilder();
        header.AppendLine($"Subject: {subject}");
        header.AppendLine($"To: {(string.IsNullOrWhiteSpace(toAddress) ? "(no application email listed in the posting)" : toAddress)}");
        if (!string.IsNullOrWhiteSpace(applyUrl)) header.AppendLine($"Apply at: {applyUrl}");
        header.AppendLine($"Resume used: {displayName} ({versionId})");
        header.AppendLine();
        await File.WriteAllTextAsync(emailTextPath, header.Append(body).ToString(), ct).ConfigureAwait(false);

        var entries = new List<(string, string)> { (Path.GetFileName(emailTextPath), emailTextPath), (Path.GetFileName(emlPath), emlPath) };
        if (!string.IsNullOrWhiteSpace(docxPath)) entries.Add((Path.GetFileName(docxPath), docxPath!));
        if (!string.IsNullOrWhiteSpace(pdfPath)) entries.Add((Path.GetFileName(pdfPath), pdfPath!));
        if (!string.IsNullOrWhiteSpace(coverPdfPath)) entries.Add((Path.GetFileName(coverPdfPath), coverPdfPath));
        var zipPath = Path.Combine(dir, $"application-kit-{kitId}.zip");
        KitBuilder.Zip(entries, zipPath);

        using (var conn = _db.Open())
        {
            Db.Exec(conn,
                "INSERT INTO kit_emails (id, job_id, version_id, subject, body, cover_letter, to_address, apply_url, generated_by, created_at) " +
                "VALUES (@id,@j,@v,@s,@b,@c,@t,@a,@g,@n)",
                Db.P("@id", kitId), Db.P("@j", request.JobId), Db.P("@v", versionId), Db.P("@s", subject), Db.P("@b", body),
                Db.P("@c", coverLetter), Db.P("@t", toAddress), Db.P("@a", applyUrl), Db.P("@g", generatedBy), Db.P("@n", Now()));
        }

        var files = new KitFiles(emlPath, zipPath, emailTextPath, coverPdfPath, coverDocxPath);
        var kit = new KitDto
        {
            Id = kitId,
            JobId = request.JobId,
            JobTitle = job.Title,
            Company = job.Company,
            VersionId = versionId,
            VersionLabel = displayName,
            Subject = subject,
            Body = body,
            CoverLetter = coverLetter,
            ToAddress = toAddress,
            ApplyUrl = applyUrl,
            GeneratedBy = generatedBy,
            CreatedAt = Now(),
            WordCount = KitBuilder.WordCount(body),
            FactsUsed = facts.Take(6).Select(f => f.Text).ToList(),
            Attachments = entries.Skip(2).Select(e => e.Item1).ToList(),
            MailtoLink = new KitBuilder().MailtoLink(toAddress, subject, body),
            ReadyToSend = File.Exists(emlPath) && File.Exists(zipPath)
        };

        return (kit, files);
    }

    public (KitDto Kit, KitFiles Files) GetKit(string kitId, Configuration.AppConfig app)
    {
        using var conn = _db.Open();
        var row = Db.QueryOne(conn,
            "SELECT k.id, k.job_id, k.version_id, k.subject, k.body, k.cover_letter, k.to_address, k.apply_url, k.generated_by, k.created_at, " +
            "COALESCE(j.title,'') AS job_title, COALESCE(j.company,'') AS company, COALESCE(v.label,'') AS version_label " +
            "FROM kit_emails k LEFT JOIN jobs j ON j.id = k.job_id LEFT JOIN resume_versions v ON v.id = k.version_id WHERE k.id = @id",
            r => new
            {
                Id = Db.Str(r, "id"),
                JobId = Db.Str(r, "job_id"),
                VersionId = Db.Str(r, "version_id"),
                Subject = Db.Str(r, "subject"),
                Body = Db.Str(r, "body"),
                CoverLetter = Db.Str(r, "cover_letter"),
                ToAddress = Db.Str(r, "to_address"),
                ApplyUrl = Db.Str(r, "apply_url"),
                GeneratedBy = Db.Str(r, "generated_by"),
                CreatedAt = Db.Str(r, "created_at"),
                JobTitle = Db.Str(r, "job_title"),
                Company = Db.Str(r, "company"),
                VersionLabel = Db.Str(r, "version_label")
            }, Db.P("@id", kitId));

        if (row == default) throw new KeyNotFoundException($"Kit '{kitId}' does not exist.");

        var dir = Path.Combine(app.DataDir, "kits", row.JobId);
        var files = new KitFiles(
            Exists(Path.Combine(dir, $"application-{row.Id}.eml")),
            Exists(Path.Combine(dir, $"application-kit-{row.Id}.zip")),
            Exists(Path.Combine(dir, $"email-{row.Id}.txt")),
            Exists(Path.Combine(dir, $"cover-letter-{row.Id}.pdf")),
            Exists(Path.Combine(dir, $"cover-letter-{row.Id}.docx")));

        var attachments = new List<string>();
        if (files.EmailText is not null) attachments.Add(Path.GetFileName(files.EmailText));
        if (files.Eml is not null) attachments.Add(Path.GetFileName(files.Eml));
        if (files.CoverPdf is not null) attachments.Add(Path.GetFileName(files.CoverPdf));

        var kit = new KitDto
        {
            Id = row.Id,
            JobId = row.JobId,
            JobTitle = row.JobTitle,
            Company = row.Company,
            VersionId = row.VersionId,
            VersionLabel = row.VersionLabel,
            Subject = row.Subject,
            Body = row.Body,
            CoverLetter = row.CoverLetter,
            ToAddress = row.ToAddress,
            ApplyUrl = row.ApplyUrl,
            GeneratedBy = row.GeneratedBy,
            CreatedAt = row.CreatedAt,
            WordCount = KitBuilder.WordCount(row.Body),
            Attachments = attachments,
            MailtoLink = new KitBuilder().MailtoLink(row.ToAddress, row.Subject, row.Body),
            ReadyToSend = files.Eml is not null && files.Zip is not null
        };

        return (kit, files);
    }

    private async Task<string> BuildCoverLetterAsync(JobDto job, string resumeText, string fullName, CancellationToken ct)
    {
        var system = ModelRouter.SystemPrompt("apply_email", _profile.BuildReference());
        var user =
            $"Write a short cover letter for this role, 200 words maximum. Plain text, no placeholders, every claim from my resume.\n\n" +
            $"ROLE: {job.Title}\nCOMPANY: {job.Company}\n\nMY RESUME:\n<<<{Trim(resumeText, 6000)}>>>";
        var routed = await _router.RunAsync("apply_email", system, user, ct).ConfigureAwait(false);
        if (routed.UsedModel && !string.IsNullOrWhiteSpace(routed.Text) && !ContainsPlaceholder(routed.Text))
            return routed.Text.Trim();

        var company = string.IsNullOrWhiteSpace(job.Company) ? "" : $" at {job.Company}";
        return $"Dear Hiring Team,\n\nI am applying for the {job.Title}{company} role.\n\n" +
               $"My resume is attached. I would welcome a conversation about how my experience fits what you need.\n\nKind regards,\n{fullName}\n";
    }

    // ---- Tracker -----------------------------------------------------------

    private const string TrackerSelect =
        "SELECT a.id, a.job_id, COALESCE(j.title,'') AS job_title, COALESCE(j.company,'') AS company, a.kit_id, " +
        "a.version_id, COALESCE(v.label,'') AS version_label, a.status, a.notes, a.created_at, a.updated_at " +
        "FROM applications a LEFT JOIN jobs j ON j.id = a.job_id LEFT JOIN resume_versions v ON v.id = a.version_id";

    private static TrackerDto MapTracker(SqliteDataReader r) => new(
        Db.Str(r, "id"), Db.Str(r, "job_id"), Db.Str(r, "job_title"), Db.Str(r, "company"),
        Db.Str(r, "kit_id"), Db.Str(r, "version_id"), Db.Str(r, "version_label"), Db.Str(r, "status"),
        Db.Str(r, "notes"), Db.Str(r, "created_at"), Db.Str(r, "updated_at"));

    public Paged<TrackerDto> ListTracker(int page, int size, string? status)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size <= 0 ? 25 : size, 1, 100);
        var where = string.IsNullOrWhiteSpace(status) ? "" : " WHERE a.status = @s";
        var parameters = string.IsNullOrWhiteSpace(status)
            ? Array.Empty<SqliteParameter>()
            : new[] { Db.P("@s", status) };

        using var conn = _db.Open();
        var total = Convert.ToInt32(Db.Scalar(conn, "SELECT COUNT(*) FROM applications a" + where, parameters) ?? 0);
        var pageParameters = new List<SqliteParameter>(parameters) { Db.P("@l", size), Db.P("@o", (page - 1) * size) };
        var items = Db.Query(conn, TrackerSelect + where + " ORDER BY a.updated_at DESC LIMIT @l OFFSET @o", MapTracker, pageParameters.ToArray());
        return new Paged<TrackerDto>(items, page, size, total);
    }

    public TrackerDto AddTrackerEntry(CreateTrackerRequest request)
    {
        var job = _jobs.GetJob(request.JobId) ?? throw new KeyNotFoundException($"Job '{request.JobId}' does not exist.");
        var versionId = request.ResumeVersionId ?? job.BestVersionId ?? "";
        var id = ProfileService.NewId("app");
        var now = Now();

        using var conn = _db.Open();
        Db.Exec(conn,
            "INSERT INTO applications (id, job_id, kit_id, version_id, status, notes, created_at, updated_at) VALUES (@id,@j,@k,@v,@s,@n,@c,@c)",
            Db.P("@id", id), Db.P("@j", request.JobId), Db.P("@k", request.KitId ?? ""), Db.P("@v", versionId),
            Db.P("@s", NormalizeStatus(request.Status)), Db.P("@n", request.Notes ?? ""), Db.P("@c", now));

        return Db.QueryOne(conn, TrackerSelect + " WHERE a.id = @id", MapTracker, Db.P("@id", id))!;
    }

    public TrackerDto PatchTracker(string id, PatchTrackerRequest request)
    {
        using var conn = _db.Open();
        var current = Db.QueryOne(conn, "SELECT status, notes, kit_id, version_id FROM applications WHERE id = @id", r => new
        {
            Status = Db.Str(r, "status"),
            Notes = Db.Str(r, "notes"),
            KitId = Db.Str(r, "kit_id"),
            VersionId = Db.Str(r, "version_id")
        }, Db.P("@id", id)) ?? throw new KeyNotFoundException($"Tracker entry '{id}' does not exist.");

        Db.Exec(conn, "UPDATE applications SET status=@s, notes=@n, kit_id=@k, version_id=@v, updated_at=@t WHERE id=@id",
            Db.P("@s", request.Status is null ? current.Status : NormalizeStatus(request.Status)),
            Db.P("@n", request.Notes ?? current.Notes),
            Db.P("@k", request.KitId ?? current.KitId),
            Db.P("@v", request.ResumeVersionId ?? current.VersionId),
            Db.P("@t", Now()), Db.P("@id", id));

        return Db.QueryOne(conn, TrackerSelect + " WHERE a.id = @id", MapTracker, Db.P("@id", id))!;
    }

    public bool DeleteTrackerEntry(string id)
    {
        using var conn = _db.Open();
        return Db.Execute(conn, "DELETE FROM applications WHERE id = @id", Db.P("@id", id)) > 0;
    }

    // ---- Helpers -----------------------------------------------------------

    private string? BestFitVersion()
    {
        using var conn = _db.Open();
        return Db.QueryOne(conn, "SELECT id FROM resume_versions WHERE best_fit = 1 ORDER BY created_at DESC LIMIT 1", r => Db.Str(r, "id"));
    }

    private static string NameFor(ProfileDto profile)
    {
        if (profile.Contact.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name)) return name;
        if (profile.Contact.TryGetValue("full_name", out var full) && !string.IsNullOrWhiteSpace(full)) return full;
        return "Your name";
    }

    /// <summary>
/// Email pattern used to pre-fill the To field. The TLD part deliberately excludes dots so a
/// trailing full stop in a sentence ("send it to careers@example.org.") is not captured.
/// </summary>
private static readonly Regex EmailRegex = new(@"\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+\b", RegexOptions.Compiled);

    private static string ExtractEmail(string description)
    {
        var match = EmailRegex.Match(description ?? "");
        return match.Success ? match.Value.TrimEnd('.', ',', ';') : "";
    }

    private static readonly string[] Placeholders =
    {
        "[company]", "[your name]", "{company}", "[role]", "[job title]", "[insert", "lorem ipsum", "<company>"
    };

    private static bool ContainsPlaceholder(string text)
    {
        var lower = text.ToLowerInvariant();
        return Placeholders.Any(p => lower.Contains(p));
    }

    private static string StripSubjectLine(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 0 && lines[0].TrimStart().StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
            return string.Join("\n", lines.Skip(1)).Trim();
        return text.Trim();
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "\n[truncated]";

    private static string NormalizeStatus(string? status)
    {
        var value = (status ?? "prepared").Trim().ToLowerInvariant().Replace('-', '_');
        return value switch
        {
            "prepared" => "prepared",
            "sent" or "sent_by_me" => "sent",
            "replied" => "replied",
            "interview" or "interviewed" => "interview",
            "rejected" => "rejected",
            _ => "prepared"
        };
    }

    private static string? Exists(string path) => File.Exists(path) ? path : null;
}