using ResuClean.Models;
using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Verification is the difference between "we applied to this" and "we applied to this, and it was
/// open when we checked".
///
/// Applied to 27 positions on 12 Oct 2026, more than half turned out not to be what they appeared to
/// be: four Jumia roles 404ing, a posting whose page had been saying "expired" for months, and twelve
/// that could not be verified at all. Four roles were carried as live across three sessions purely
/// because nothing ever asked the employer's system again.
///
/// So the property that matters here is the uncomfortable one: a check that could not confirm anything
/// must not be rounded up to "open".
/// </summary>
public class JobVerificationTests
{
    /// <summary>
    /// Saves a posting through the real save path and returns it. The id is generated on save, so it
    /// cannot be hardcoded — a test that assumes "job_1" is testing nothing but the exception.
    /// </summary>
    private static JobDto Save(TestHost host) => host.Jobs.SaveManualJob(new SaveJobRequest
    {
        Title = "Data Engineer",
        Company = "Acme",
        Location = "Nairobi",
        Url = "https://example.org/1",
        Text = "Build data pipelines."
    });

    /// <summary>A posting that exists only in memory, for the one case that needs no database.</summary>
    private static JobDto Unsaved() =>
        new("job_1", "Data Engineer", "Acme", "Nairobi", "https://example.org", "2026-10-10",
            "Build data pipelines.", "src", "manual", 0, false, true, null, "2026-10-10T00:00:00Z");

    [Fact]
    public void A_posting_saved_but_never_checked_reads_as_unknown()
    {
        using var host = new TestHost();

        // Not "open". We have never asked, and unknown is the honest word for that.
        Assert.Equal("unknown", host.Jobs.EffectiveStatus(Unsaved()));
    }

    [Fact]
    public void A_confirmed_open_posting_reads_as_open()
    {
        using var host = new TestHost();
        var job = host.Jobs.UpdateVerification(Save(host).Id, "2026-10-12T09:00:00Z", null, "open");

        Assert.Equal("open", job.EffectiveStatus);
        Assert.Equal("2026-10-12T09:00:00Z", job.LastVerifiedAt);
    }

    [Fact]
    public void An_unverifiable_posting_is_not_counted_as_open()
    {
        using var host = new TestHost();

        // The whole point. A 403 or a wrong-company redirect confirmed nothing, and a tracker that
        // calls those live is manufacturing confidence.
        var job = host.Jobs.UpdateVerification(Save(host).Id, null, null, "unverifiable");

        Assert.Equal("unverifiable", job.EffectiveStatus);
        Assert.Null(job.LastVerifiedAt);
    }

    [Fact]
    public void A_failed_check_does_not_advance_the_verified_timestamp()
    {
        using var host = new TestHost();
        var id = Save(host).Id;
        host.Jobs.UpdateVerification(id, "2026-10-12T09:00:00Z", null, "open");

        // We checked again and learned nothing. The timestamp must not move, because "we checked
        // and could not tell" is not "we checked and it was there".
        var job = host.Jobs.UpdateVerification(id, null, null, "unverifiable");

        Assert.Equal("2026-10-12T09:00:00Z", job.LastVerifiedAt);
    }

    [Fact]
    public void A_closed_posting_reads_as_closed()
    {
        using var host = new TestHost();
        var job = host.Jobs.UpdateVerification(Save(host).Id, null, null, "closed");

        Assert.Equal("closed", job.EffectiveStatus);
    }

    [Fact]
    public void A_posting_whose_deadline_has_passed_reads_as_expired_even_when_confirmed_open()
    {
        using var host = new TestHost();
        var past = DateTime.UtcNow.AddDays(-2).ToString("O");

        // Confirmed open this morning and closed tonight is a real sequence. The closing date is
        // what a user acts on, so it wins over a check that was true when it was made.
        var job = host.Jobs.UpdateVerification(Save(host).Id, DateTime.UtcNow.ToString("O"), past, "open");

        Assert.Equal("expired", job.EffectiveStatus);
    }

    [Fact]
    public void A_future_deadline_does_not_make_a_posting_expired()
    {
        using var host = new TestHost();
        var future = DateTime.UtcNow.AddDays(20).ToString("O");
        var job = host.Jobs.UpdateVerification(Save(host).Id, DateTime.UtcNow.ToString("O"), future, "open");

        Assert.Equal("open", job.EffectiveStatus);
    }

    [Fact]
    public void Verifying_an_unknown_job_says_so_rather_than_silently_doing_nothing()
    {
        using var host = new TestHost();

        Assert.Throws<KeyNotFoundException>(() =>
            host.Jobs.UpdateVerification("job_missing", null, null, "open"));
    }

    [Fact]
    public void The_verified_date_survives_a_restart_because_it_is_stored()
    {
        using var host = new TestHost();
        var at = "2026-10-12T09:00:00Z";
        var id = Save(host).Id;
        host.Jobs.UpdateVerification(id, at, null, "open");

        // Read back through a fresh query, not the object we just wrote.
        Assert.Equal(at, host.Jobs.GetJob(id)?.LastVerifiedAt);
    }

    [Fact]
    public void The_verified_date_reaches_the_job_list_not_just_the_detail_read()
    {
        using var host = new TestHost();
        var id = Save(host).Id;
        host.Jobs.UpdateVerification(id, "2026-10-12T09:00:00Z", null, "open");

        // The list is what gets rendered. If the column is missing from this query the badge silently
        // vanishes from the one screen that matters, which is exactly the drift item 4.3 warns about.
        var row = host.Jobs.ListJobs(1, 25, null, null, null, null).Items.Single(j => j.Id == id);

        Assert.Equal("2026-10-12T09:00:00Z", row.LastVerifiedAt);
        Assert.Equal("open", row.EffectiveStatus);
    }
}