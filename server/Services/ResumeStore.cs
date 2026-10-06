using Microsoft.Data.Sqlite;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>Resume and resume-version storage, plus streamed access to generated files.</summary>
public sealed class ResumeStore
{
    private readonly Db _db;
    private readonly Configuration.AppConfig _app;

    public ResumeStore(Db db, Configuration.AppConfig app)
    {
        _db = db;
        _app = app;
    }

    private static string Now() => DateTime.UtcNow.ToString("O");

    public string DocumentsDir => Path.Combine(_app.DataDir, "documents");
    public string UploadsDir => Path.Combine(_app.DataDir, "uploads");

    public Paged<ResumeSummary> List(int page, int size)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size <= 0 ? 25 : size, 1, 100);
        using var conn = _db.Open();
        var total = Convert.ToInt32(Db.Scalar(conn, "SELECT COUNT(*) FROM resumes") ?? 0);
        var items = Db.Query(conn,
            "SELECT r.id, r.label, r.created_at, r.updated_at, r.current_version_id, " +
            "(SELECT COUNT(*) FROM resume_versions v WHERE v.resume_id = r.id) AS version_count, " +
            "(SELECT v.id FROM resume_versions v WHERE v.resume_id = r.id AND v.best_fit = 1 LIMIT 1) AS best_fit " +
            "FROM resumes r ORDER BY r.updated_at DESC LIMIT @l OFFSET @o",
            r => new ResumeSummary(Db.Str(r, "id"), Db.Str(r, "label"), Db.Str(r, "created_at"), Db.Str(r, "updated_at"),
                Db.StrOrNull(r, "current_version_id"), Db.Int(r, "version_count"), Db.StrOrNull(r, "best_fit")),
            Db.P("@l", size), Db.P("@o", (page - 1) * size));
        return new Paged<ResumeSummary>(items, page, size, total);
    }

    public ResumeDetail? Get(string id)
    {
        using var conn = _db.Open();
        var summary = Db.QueryOne(conn,
            "SELECT r.id, r.label, r.created_at, r.updated_at, r.current_version_id, " +
            "(SELECT COUNT(*) FROM resume_versions v WHERE v.resume_id = r.id) AS version_count, " +
            "(SELECT v.id FROM resume_versions v WHERE v.resume_id = r.id AND v.best_fit = 1 LIMIT 1) AS best_fit " +
            "FROM resumes r WHERE r.id = @id",
            r => new ResumeSummary(Db.Str(r, "id"), Db.Str(r, "label"), Db.Str(r, "created_at"), Db.Str(r, "updated_at"),
                Db.StrOrNull(r, "current_version_id"), Db.Int(r, "version_count"), Db.StrOrNull(r, "best_fit")),
            Db.P("@id", id));
        if (summary is null) return null;

        var versions = Db.Query(conn,
            "SELECT id, resume_id, label, source_kind, parent_id, best_fit, created_at, LENGTH(text) AS chars, docx_path, pdf_path " +
            "FROM resume_versions WHERE resume_id = @id ORDER BY created_at DESC",
            r => new ResumeVersionDto(Db.Str(r, "id"), Db.Str(r, "resume_id"), Db.Str(r, "label"), Db.Str(r, "source_kind"),
                Db.StrOrNull(r, "parent_id"), Db.Bool(r, "best_fit"), Db.Str(r, "created_at"), Db.Int(r, "chars"),
                !string.IsNullOrEmpty(Db.StrOrNull(r, "docx_path")), !string.IsNullOrEmpty(Db.StrOrNull(r, "pdf_path"))),
            Db.P("@id", id));

        return new ResumeDetail(summary, versions);
    }

    public (string ResumeId, string VersionId) Create(string label, string text, string sourceKind)
    {
        var resumeId = ProfileService.NewId("res");
        var versionId = ProfileService.NewId("ver");
        var now = Now();
        using var conn = _db.Open();
        var tx = conn.BeginTransaction();
        try
        {
            Db.Exec(conn, "INSERT INTO resumes (id, label, created_at, updated_at, current_version_id) VALUES (@id,@l,@c,@c,@v)",
                Db.P("@id", resumeId), Db.P("@l", label), Db.P("@c", now), Db.P("@v", versionId));
            Db.Exec(conn, "INSERT INTO resume_versions (id, resume_id, label, text, source_kind, parent_id, best_fit, created_at) VALUES (@id,@r,'Original',@t,@s,NULL,1,@n)",
                Db.P("@id", versionId), Db.P("@r", resumeId), Db.P("@t", text), Db.P("@s", sourceKind), Db.P("@n", now));
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return (resumeId, versionId);
    }

    public string AddVersion(string resumeId, string parentId, string label, string text, string sourceKind)
    {
        var id = ProfileService.NewId("ver");
        var now = Now();
        using var conn = _db.Open();
        var tx = conn.BeginTransaction();
        try
        {
            var exists = Db.Scalar(conn, "SELECT 1 FROM resumes WHERE id = @id", Db.P("@id", resumeId)) != null;
            if (!exists) throw new KeyNotFoundException($"Resume '{resumeId}' does not exist.");
            Db.Exec(conn, "INSERT INTO resume_versions (id, resume_id, label, text, source_kind, parent_id, best_fit, created_at) VALUES (@id,@r,@l,@t,@s,@p,0,@n)",
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

    public string? GetText(string versionId)
    {
        using var conn = _db.Open();
        return Db.QueryOne(conn, "SELECT text FROM resume_versions WHERE id = @id", r => Db.Str(r, "text"), Db.P("@id", versionId));
    }

    public bool DeleteResume(string id)
    {
        using var conn = _db.Open();
        return Db.Execute(conn, "DELETE FROM resumes WHERE id = @id", Db.P("@id", id)) > 0;
    }

    public bool SetBestFit(string versionId, bool bestFit)
    {
        using var conn = _db.Open();
        var resumeId = Db.QueryOne(conn, "SELECT resume_id FROM resume_versions WHERE id = @id", r => Db.Str(r, "resume_id"), Db.P("@id", versionId));
        if (resumeId is null) return false;
        var tx = conn.BeginTransaction();
        try
        {
            Db.Exec(conn, "UPDATE resume_versions SET best_fit = 0 WHERE resume_id = @r", Db.P("@r", resumeId));
            if (bestFit) Db.Exec(conn, "UPDATE resume_versions SET best_fit = 1 WHERE id = @id", Db.P("@id", versionId));
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return true;
    }

    public void RecordFiles(string versionId, string? docxPath, string? pdfPath)
    {
        using var conn = _db.Open();
        Db.Exec(conn, "UPDATE resume_versions SET docx_path = COALESCE(@d, docx_path), pdf_path = COALESCE(@p, pdf_path) WHERE id = @id",
            Db.P("@d", docxPath), Db.P("@p", pdfPath), Db.P("@id", versionId));
    }

    public (string? DocxPath, string? PdfPath)? GetFilePaths(string versionId)
    {
        using var conn = _db.Open();
        var row = Db.QueryOne(conn, "SELECT docx_path, pdf_path FROM resume_versions WHERE id = @id",
            r => Db.StrOrNull(r, "docx_path") + "\n" + Db.StrOrNull(r, "pdf_path"), Db.P("@id", versionId));
        if (row is null) return null;
        var parts = row.Split('\n', 2);
        return (parts[0].Length == 0 ? null : parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }

    public string? GetLabel(string versionId)
    {
        using var conn = _db.Open();
        return Db.QueryOne(conn, "SELECT label FROM resume_versions WHERE id = @id", r => Db.Str(r, "label"), Db.P("@id", versionId));
    }

    /// <summary>Renders .docx and .pdf for a version if they do not exist yet.</summary>
    public (string? DocxPath, string? PdfPath) EnsureDocuments(string versionId, string? displayName)
    {
        var text = GetText(versionId) ?? string.Empty;
        var existing = GetFilePaths(versionId);
        Directory.CreateDirectory(DocumentsDir);

        var safeName = SafeName(displayName ?? GetLabel(versionId) ?? "resume");
        var docxPath = existing?.DocxPath;
        var pdfPath = existing?.PdfPath;

        if (string.IsNullOrEmpty(docxPath) || !File.Exists(docxPath))
        {
            docxPath = Path.Combine(DocumentsDir, $"{versionId}-{safeName}.docx");
            Extractor.WriteDocx(docxPath, text, displayName);
        }
        if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath))
        {
            pdfPath = Path.Combine(DocumentsDir, $"{versionId}-{safeName}.pdf");
            Services.Kits.PdfWriter.Write(pdfPath, text, displayName ?? "resume");
        }

        RecordFiles(versionId, docxPath, pdfPath);
        return (docxPath, pdfPath);
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        cleaned = string.Join('-', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length > 40 ? cleaned[..40] : (cleaned.Length == 0 ? "resume" : cleaned);
    }
}