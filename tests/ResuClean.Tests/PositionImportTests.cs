using System.Text.Json;
using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Importing a position file somebody else kept.
///
/// The shape is the one this tool exports, but the content is not its own: the keys are positions
/// rather than companies, and the notes carry the only copy of the posting URL, the title and the
/// location. Getting that wrong does not merely lose data, it invents it — an empty "applied" date
/// read back as today writes an application into your history that never happened.
///
/// The six tests below are the failures the first version of this importer had.
/// </summary>
public class PositionImportTests
{
    private static PositionImport For(TestHost host) => new(host.Db, host.Jobs);

    /// <summary>A slice of a real tracker file, with the shapes that actually appear in it.</summary>
    private const string RealFile = """
        {
          "jumia_nodejs": {
            "stage": "closed",
            "note": "Jumia - Senior NodeJS Software Engineer (Nairobi). Node/Express/NestJS, OOP, SQL+NoSQL. https://job-boards.eu.greenhouse.io/jumia/jobs/4343820101",
            "applied": "",
            "updated": "2026-10-12"
          },
          "google_search": {
            "stage": "closed",
            "note": "Google - Senior Software Engineer, Search (Nairobi). Deadline 31 Oct 2026. https://jobwebkenya.com/jobs/x-3/",
            "applied": "",
            "updated": "2026-10-12"
          },
          "canonical_data_infra": {
            "stage": "todo",
            "note": "12 Oct verification: OPEN, EMEA, posted 2 Oct. Apply base role.",
            "applied": "",
            "updated": ""
          },
          "tatu_net": {
            "stage": "applied",
            "note": "Applied 11 Oct. ALSO apply via Workable AD02B6FE71.",
            "applied": "2026-10-11",
            "updated": "2026-10-11"
          },
          "mkopa": { "stage": "applied", "note": "", "applied": "", "updated": "2026-10-12" }
        }
        """;

    private static ResuClean.Models.JobDto Job(TestHost host, string titleContains) =>
        host.Jobs.ListJobs(1, 50, titleContains, null, null, null).Items.First(j => j.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Four_roles_at_one_company_stay_four_roles()
    {
        using var host = new TestHost();
        var file = """
            {
              "jumia_nodejs": { "stage": "closed", "note": "NodeJS role.", "applied": "", "updated": "2026-10-12" },
              "jumia_java":   { "stage": "closed", "note": "Java role.",   "applied": "", "updated": "2026-10-12" },
              "jumia_sre":    { "stage": "closed", "note": "SRE role.",    "applied": "", "updated": "2026-10-12" },
              "jumia_frontend": { "stage": "todo", "note": "Frontend role.", "applied": "", "updated": "" }
            }
            """;

        For(host).Import(file, overwrite: false);

        // The existing per-company import keys on the company and would have kept exactly one.
        var jobs = host.Jobs.ListJobs(1, 50, null, null, null, null).Total;
        Assert.Equal(4, jobs);
    }

    [Fact]
    public void A_closed_posting_is_not_recorded_as_you_being_rejected()
    {
        using var host = new TestHost();
        For(host).Import(RealFile, overwrite: false);

        // "closed" means the posting is gone. Collapsing it into "rejected" claims the employer
        // turned you down, which is a different and much more annoying thing to have on record.
        var stages = host.App.ListTracker(1, 50, null).Items.Select(t => t.Status).ToList();

        Assert.Contains("closed", stages);
        Assert.DoesNotContain("rejected", stages);
    }

    [Fact]
    public void An_empty_applied_date_is_never_filled_in_with_today()
    {
        using var host = new TestHost();
        For(host).Import(RealFile, overwrite: false);
        var transfer = new TrackerTransfer(host.Db, host.Jobs);

        var exported = JsonDocument.Parse(transfer.ExportJson()).RootElement;

        // These entries say they were never applied to. The file had no applied date, so the export
        // must have none either — writing today's date would invent an application.
        Assert.Equal("", exported.GetProperty("jumia_nodejs").GetProperty("applied").GetString());
        Assert.Equal("", exported.GetProperty("google_search").GetProperty("applied").GetString());
        Assert.Equal("", exported.GetProperty("mkopa").GetProperty("applied").GetString());

        // And the one that genuinely has a date keeps it.
        Assert.Equal("2026-10-11", exported.GetProperty("tatu_net").GetProperty("applied").GetString());
    }

    [Fact]
    public void The_posting_url_in_a_note_is_kept_rather_than_replaced_by_a_placeholder()
    {
        using var host = new TestHost();
        For(host).Import(RealFile, overwrite: false);

        var job = Job(host, "NodeJS");

        // The URL is the whole point: without it the role cannot be re-verified, which is the
        // failure this project exists to prevent.
        Assert.Equal("https://job-boards.eu.greenhouse.io/jumia/jobs/4343820101", job.Url);
        Assert.DoesNotContain("import-", job.Url);
    }

    [Fact]
    public void A_title_company_and_location_are_read_out_of_the_note()
    {
        using var host = new TestHost();
        For(host).Import(RealFile, overwrite: false);

        var job = Job(host, "NodeJS");

        Assert.Equal("Senior NodeJS Software Engineer", job.Title);
        Assert.Equal("Jumia", job.Company);
        Assert.Equal("Nairobi", job.Location);
    }

    [Fact]
    public void A_deadline_stated_in_a_note_is_carried_over()
    {
        using var host = new TestHost();
        For(host).Import(RealFile, overwrite: false);

        var job = Job(host, "Search");

        Assert.NotNull(job.DeadlineAt);
        Assert.Contains("2026-10-31", job.DeadlineAt);
    }

    [Fact]
    public void A_verification_written_into_a_note_is_recorded()
    {
        using var host = new TestHost();
        For(host).Import(RealFile, overwrite: false);

        var job = Job(host, "Canonical Data Infra");

        Assert.Equal("open", job.Status);
        Assert.Equal("open", job.EffectiveStatus);
    }

    [Fact]
    public void A_note_with_nothing_to_extract_still_imports_and_says_what_is_missing()
    {
        using var host = new TestHost();

        // "mkopa" carries only a stage. It should still arrive — with the key as its label —
        // and be reported as missing a title and a URL rather than silently guessed at.
        var preview = For(host).Parse(RealFile);
        var mkopa = preview.Positions.Single(p => p.Key == "mkopa");

        Assert.Contains("title", mkopa.Missing);
        Assert.Contains("posting URL", mkopa.Missing);
        Assert.Null(mkopa.Title);

        For(host).Import(RealFile, overwrite: false);
        Assert.Equal("Mkopa", Job(host, "Mkopa").Title);
    }

    [Theory]
    [InlineData("canonical_sre", "Canonical SRE")]
    [InlineData("moniepoint_principal", "Moniepoint Principal")]
    [InlineData("unio_platform", "Unio Platform")]
    public void A_key_used_as_a_label_keeps_its_acronyms(string key, string expected)
    {
        using var host = new TestHost();

        // "Canonical Sre" reads as a typo. The key is the user's own label, so it should come back
        // looking like something they wrote rather than something we mangled.
        For(host).Import($$"""{ "{{key}}": { "stage": "todo", "note": "", "applied": "", "updated": "" } }""", false);

        Assert.Equal(expected, host.Jobs.ListJobs(1, 50, null, null, null, null).Items.Single().Title);
    }

    [Fact]
    public void Importing_twice_updates_rather_than_duplicating()
    {
        using var host = new TestHost();
        var importer = For(host);

        importer.Import(RealFile, overwrite: false);
        var second = importer.Import(RealFile, overwrite: false);

        Assert.Equal(0, second.Created);
        Assert.Equal(5, second.Skipped.Count);
        Assert.Equal(5, host.Jobs.ListJobs(1, 50, null, null, null, null).Total);
    }

    [Fact]
    public void Re_importing_with_overwrite_refreshes_a_position()
    {
        using var host = new TestHost();
        var importer = For(host);
        importer.Import(RealFile, overwrite: false);

        var changed = RealFile.Replace("\"stage\": \"closed\"", "\"stage\": \"todo\"");
        var result = importer.Import(changed, overwrite: true);

        Assert.Equal(5, result.Updated);
        Assert.Equal(5, host.Jobs.ListJobs(1, 50, null, null, null, null).Total);
    }

    [Fact]
    public void A_re_import_does_not_erase_a_field_the_note_never_carried()
    {
        using var host = new TestHost();
        var importer = For(host);
        importer.Import(RealFile, overwrite: false);

        // Add a URL by hand, then re-import a file whose note has none. The hand-added URL is a
        // correction the user made; a stale file must not undo it.
        var job = Job(host, "NodeJS");
        using (var conn = host.Db.Open())
        {
            ResuClean.Data.Db.Exec(conn, "UPDATE jobs SET url='https://correct.example/role' WHERE id=@id",
                ResuClean.Data.Db.P("@id", job.Id));
        }

        importer.Import(RealFile.Replace("https://job-boards.eu.greenhouse.io/jumia/jobs/4343820101", ""), overwrite: true);

        Assert.Equal("https://correct.example/role", host.Jobs.GetJob(job.Id)?.Url);
    }

    [Fact]
    public void The_note_is_preserved_verbatim()
    {
        using var host = new TestHost();
        For(host).Import(RealFile, overwrite: false);

        var entry = host.App.ListTracker(1, 50, null).Items.First(t => t.JobTitle.Contains("NodeJS"));

        // Extraction adds structure; it must not lose the sentence the person actually wrote.
        Assert.Contains("Workable", host.App.ListTracker(1, 50, null).Items.First(t => t.JobTitle.Contains("Tatu")).Notes);
        Assert.Contains("Node/Express/NestJS", entry.Notes);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    public void A_file_that_is_not_the_expected_shape_says_so_rather_than_importing_nothing(string json)
    {
        using var host = new TestHost();

        var error = Assert.Throws<ArgumentException>(() => For(host).Parse(json));
        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public void An_unknown_stage_falls_back_to_todo_rather_than_being_invented()
    {
        using var host = new TestHost();
        For(host).Import("""{ "acme": { "stage": "quantum-superposition", "note": "", "applied": "", "updated": "" } }""", false);

        Assert.Equal("prepared", host.App.ListTracker(1, 50, null).Items.Single().Status);
    }
}