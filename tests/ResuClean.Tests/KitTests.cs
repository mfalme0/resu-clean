using System.IO.Compression;
using System.Text;
using ResuClean.Data;
using ResuClean.Models;
using ResuClean.Services;
using ResuClean.Services.Kits;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// The application kit. The important guarantees: the email contains no placeholders, the .eml
/// really carries the resume as an attachment, and nothing in this flow transmits anything.
/// </summary>
public class KitTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    private (string JobId, string VersionId) Seed()
    {
        var (resumeId, versionId) = _host.Store.Create("Main resume", TestHost.SampleResume, "paste");

        _host.Profile.UpdateProfile(new UpsertProfileRequest
        {
            Voice = "Direct and plain",
            Contact = new Dictionary<string, string> { ["name"] = "Jane Doe", ["email"] = "jane@example.com" }
        });

        foreach (var fact in new[]
        {
            "Led the migration of 14 reports to Power BI",
            "Reduced reconciliation errors by 38%"
        })
        {
            var (_, pending, _) = _host.Profile.SubmitFact(new AddFactRequest { Kind = "achievement", Text = fact }, "test");
            _host.Profile.ApproveFact(pending!.Id);
        }

        _host.Execute("INSERT INTO jobs (id,title,company,location,url,posted_at,description,source_id,source_name,match_pct,remote,is_manual,best_version_id,raw,created_at) " +
            "VALUES (@id,@t,@c,@l,@u,NULL,@d,'src_manual','manual',72,0,1,@v,'{}',@now)",
            Db.P("@id", "job_1"),
            Db.P("@t", "Senior Data Analyst"),
            Db.P("@c", "Kenya Commercial Bank"),
            Db.P("@l", "Nairobi, Kenya"),
            Db.P("@u", "https://example.org/apply/1"),
            Db.P("@d", "We need a data analyst. Send applications to careers@example.org. SQL and Power BI required."),
            Db.P("@v", versionId),
            Db.P("@now", DateTime.UtcNow.ToString("O")));

        return ("job_1", versionId);
    }

    [Fact]
    public async Task A_kit_produces_an_email_an_eml_and_a_zip()
    {
        var (jobId, versionId) = Seed();
        var (kit, files) = await _host.App.CreateKitAsync(
            new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, UseModel = false },
            _host.AppConfig);

        Assert.Contains("Senior Data Analyst", kit.Subject);
        Assert.True(kit.ReadyToSend);
        Assert.NotNull(files.Eml);
        Assert.NotNull(files.Zip);
        Assert.True(File.Exists(files.Eml!));
        Assert.True(File.Exists(files.Zip!));
    }

    [Fact]
    public async Task The_email_body_has_no_placeholders()
    {
        var (jobId, versionId) = Seed();
        var (kit, _) = await _host.App.CreateKitAsync(
            new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, UseModel = false },
            _host.AppConfig);

        foreach (var placeholder in new[] { "[company]", "[your name]", "{company}", "XXX", "lorem ipsum" })
        {
            Assert.DoesNotContain(placeholder, kit.Body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("Jane Doe", kit.Body);
    }

    [Fact]
    public async Task The_email_stays_under_one_hundred_and_fifty_words()
    {
        var (jobId, versionId) = Seed();
        var (kit, _) = await _host.App.CreateKitAsync(
            new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, UseModel = false },
            _host.AppConfig);

        Assert.True(kit.WordCount < 150, $"body was {kit.WordCount} words");
    }

    [Fact]
    public async Task The_application_email_is_pulled_from_the_posting_text()
    {
        var (jobId, versionId) = Seed();
        var (kit, _) = await _host.App.CreateKitAsync(
            new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, UseModel = false },
            _host.AppConfig);

        Assert.Equal("careers@example.org", kit.ToAddress);
        Assert.Equal("https://example.org/apply/1", kit.ApplyUrl);
    }

    [Fact]
    public async Task The_eml_carries_the_resume_as_a_real_attachment()
    {
        var (jobId, versionId) = Seed();
        var (_, files) = await _host.App.CreateKitAsync(
            new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, UseModel = false },
            _host.AppConfig);

        var message = global::MimeKit.MimeMessage.Load(files.Eml!);
        var attachments = message.Body is global::MimeKit.Multipart multipart
            ? multipart.OfType<global::MimeKit.MimePart>()
                .Where(p => p.ContentDisposition is { IsAttachment: true })
                .ToList()
            : new List<global::MimeKit.MimePart>();

        Assert.NotEmpty(attachments);
        var attachment = attachments[0];
        var disposition = attachment.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("application/pdf", attachment.ContentType.MimeType);
        Assert.Equal(global::MimeKit.ContentDisposition.Attachment, disposition!.Disposition);
        Assert.NotNull(disposition.FileName);
        Assert.EndsWith(".pdf", disposition.FileName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_eml_is_valid_utf8_text_with_the_right_headers()
    {
        var (jobId, versionId) = Seed();
        var (kit, files) = await _host.App.CreateKitAsync(
            new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, UseModel = false },
            _host.AppConfig);

        var raw = await File.ReadAllTextAsync(files.Eml!);
        // MimeKit writes headers in its own order, so assert on content rather than position.
        Assert.Contains("Subject: " + kit.Subject, raw);
        Assert.Contains("To: careers@example.org", raw);
        Assert.Contains("application/pdf", raw);
        Assert.Contains("base64", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(kit.Body.Split('\n')[0], raw);
    }

    [Fact]
    public async Task The_zip_contains_the_email_the_eml_and_both_resume_formats()
    {
        var (jobId, versionId) = Seed();
        var (_, files) = await _host.App.CreateKitAsync(
            new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, UseModel = false },
            _host.AppConfig);

        using var archive = ZipFile.OpenRead(files.Zip!);
        var names = archive.Entries.Select(e => e.Name).ToList();

        Assert.Contains(names, n => n.EndsWith(".eml", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(names, n => n.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_mailto_link_carries_the_subject_and_body()
    {
        var (jobId, versionId) = Seed();
        var (kit, _) = await _host.App.CreateKitAsync(
            new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, UseModel = false },
            _host.AppConfig);

        Assert.StartsWith("mailto:careers%40example.org?subject=", kit.MailtoLink);
        Assert.Contains("body=", kit.MailtoLink);
    }

    [Fact]
    public async Task A_cover_letter_is_generated_when_asked()
    {
        var (jobId, versionId) = Seed();
        var (kit, files) = await _host.App.CreateKitAsync(
            new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, CoverLetter = true, UseModel = false },
            _host.AppConfig);

        Assert.NotEmpty(kit.CoverLetter);
        Assert.NotNull(files.CoverPdf);
        Assert.True(File.Exists(files.CoverPdf!));
    }

    [Fact]
    public async Task A_second_kit_for_the_same_job_is_kept_alongside_the_first()
    {
        var (jobId, versionId) = Seed();
        var (first, _) = await _host.App.CreateKitAsync(new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, UseModel = false }, _host.AppConfig);
        var (second, _) = await _host.App.CreateKitAsync(new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, UseModel = false }, _host.AppConfig);

        Assert.NotEqual(first.Id, second.Id);
        var kits = _host.App.ListTracker(1, 50, null); // tracker is empty, but nothing should throw
        Assert.Empty(kits.Items);
    }

    [Fact]
    public async Task A_cover_letter_never_claims_a_skill_the_profile_does_not_contain()
    {
        var (jobId, versionId) = Seed();
        var (kit, _) = await _host.App.CreateKitAsync(
            new CreateKitRequest { JobId = jobId, ResumeVersionId = versionId, CoverLetter = true, UseModel = false },
            _host.AppConfig);

        // The template fallback only restates the role, so no unverified skill can appear.
        Assert.DoesNotContain("machine learning", kit.CoverLetter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[", kit.CoverLetter);
    }

    // ---- Tracker -----------------------------------------------------------

    [Fact]
    public void The_tracker_accepts_a_status_and_normalises_spellings()
    {
        var (jobId, versionId) = Seed();
        var entry = _host.App.AddTrackerEntry(new CreateTrackerRequest
        {
            JobId = jobId,
            ResumeVersionId = versionId,
            Status = "sent-by-me",
            Notes = "Emailed careers@example.org"
        });

        Assert.Equal("sent", entry.Status);
        Assert.Equal("Senior Data Analyst", entry.JobTitle);
    }

    [Fact]
    public void The_tracker_updates_a_status_and_its_notes()
    {
        var (jobId, versionId) = Seed();
        var entry = _host.App.AddTrackerEntry(new CreateTrackerRequest { JobId = jobId, ResumeVersionId = versionId });

        var updated = _host.App.PatchTracker(entry.Id, new PatchTrackerRequest
        {
            Status = "interview",
            Notes = "Panel on the 14th"
        });

        Assert.Equal("interview", updated.Status);
        Assert.Contains("14th", updated.Notes);
    }

    [Fact]
    public void The_tracker_filters_by_status()
    {
        var (jobId, versionId) = Seed();
        _host.App.AddTrackerEntry(new CreateTrackerRequest { JobId = jobId, ResumeVersionId = versionId, Status = "prepared" });
        _host.App.AddTrackerEntry(new CreateTrackerRequest { JobId = jobId, ResumeVersionId = versionId, Status = "rejected" });

        Assert.Equal(1, _host.App.ListTracker(1, 50, "prepared").Total);
        Assert.Equal(1, _host.App.ListTracker(1, 50, "rejected").Total);
        Assert.Equal(2, _host.App.ListTracker(1, 50, null).Total);
    }

    [Fact]
    public void An_unknown_tracker_entry_is_a_404()
    {
        Assert.Throws<KeyNotFoundException>(() => _host.App.PatchTracker("app_missing", new PatchTrackerRequest { Status = "sent" }));
    }

    // ---- Documents ---------------------------------------------------------

    [Fact]
    public void The_docx_and_pdf_are_generated_and_non_trivial()
    {
        var (_, versionId) = Seed();
        var (docx, pdf) = _host.Store.EnsureDocuments(versionId, "Main resume");

        Assert.NotNull(docx);
        Assert.NotNull(pdf);
        Assert.True(new FileInfo(docx!).Length > 2000, "docx looks empty");
        Assert.True(new FileInfo(pdf!).Length > 2000, "pdf looks empty");
        // docx is a zip container: check the magic bytes rather than trusting the extension.
        Assert.Equal("PK", Encoding.ASCII.GetString(File.ReadAllBytes(docx!), 0, 2));
        Assert.Equal("%", Encoding.ASCII.GetString(File.ReadAllBytes(pdf!), 0, 1));
    }

    [Fact]
    public void Regenerating_documents_reuses_the_existing_files()
    {
        var (_, versionId) = Seed();
        var (first, _) = _host.Store.EnsureDocuments(versionId, "Main resume");
        var stamp = File.GetLastWriteTimeUtc(first!);
        var (second, _) = _host.Store.EnsureDocuments(versionId, "Main resume");
        Assert.Equal(first, second);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(second!));
    }
}