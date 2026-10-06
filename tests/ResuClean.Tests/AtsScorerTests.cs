using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

public class AtsScorerTests
{
    private static ResuClean.Models.AtsReport Score(string text, string? jobDescription = null) =>
        AtsScorer.Score("ver_test", text, jobDescription);

    [Fact]
    public void Score_is_between_zero_and_one_hundred()
    {
        var report = Score(TestHost.SampleResume);
        Assert.InRange(report.Score, 0, 100);
        Assert.Contains(new[] { "Strong", "Good", "Needs work", "At risk" }, v => v == report.Verdict);
    }

    [Fact]
    public void Every_check_has_a_tip_and_a_detail()
    {
        var report = Score(TestHost.SampleResume);
        Assert.NotEmpty(report.Checks);
        foreach (var check in report.Checks)
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Tip), $"check {check.Id} has no tip");
            Assert.False(string.IsNullOrWhiteSpace(check.Detail), $"check {check.Id} has no detail");
            Assert.False(string.IsNullOrWhiteSpace(check.Label));
        }
    }

    [Fact]
    public void Non_keyword_weights_sum_to_one_hundred()
    {
        var report = Score(TestHost.SampleResume);
        var total = report.Checks.Where(c => c.Id != "keywords").Sum(c => c.Weight);
        Assert.Equal(100, total);
    }

    [Fact]
    public void Strong_resume_scores_well()
    {
        var report = Score(TestHost.SampleResume);
        Assert.True(report.Score >= 70, $"expected a good score, got {report.Score}");
    }

    [Fact]
    public void Missing_contact_details_fail_the_contact_check()
    {
        var report = Score("EXPERIENCE\n- Led something");
        var contact = report.Checks.Single(c => c.Id == "contact");
        Assert.False(contact.Passed);
        Assert.Contains("email", contact.Tip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Icons_fail_the_no_icons_check()
    {
        var report = Score("Jane Doe \uF0E7 Nairobi\nEMAIL: jane@example.com\nPHONE: +254700000000");
        Assert.False(report.Checks.Single(c => c.Id == "no-icons").Passed);
    }

    [Fact]
    public void Tables_fail_the_no_tables_check()
    {
        var text = string.Join("\n", new[] { "a | b | c | d", "e | f | g | h", "i | j | k | l" });
        Assert.False(Score(text).Checks.Single(c => c.Id == "no-tables").Passed);
    }

    [Fact]
    public void Too_few_words_fails_the_length_check()
    {
        var report = Score("Jane Doe\nemail: jane@example.com\nEXPERIENCE\n- Led things");
        Assert.False(report.Checks.Single(c => c.Id == "length").Passed);
    }

    [Fact]
    public void Bullet_heavy_resume_passes_the_bullets_check()
    {
        var report = Score(TestHost.SampleResume);
        Assert.True(report.Checks.Single(c => c.Id == "bullets").Passed);
    }

    [Fact]
    public void Job_description_produces_a_keyword_match()
    {
        var report = Score(TestHost.SampleResume, "Senior data analyst. SQL, Power BI, dbt and Airflow.");
        Assert.NotNull(report.Keywords);
        Assert.Contains("keywords", report.Checks.Select(c => c.Id));
        Assert.NotEmpty(report.Keywords!.Matched);
        // The keyword check must not inflate the 0-100 score.
        Assert.Equal(0, report.Checks.Single(c => c.Id == "keywords").Weight);
    }

    [Fact]
    public void No_job_description_means_no_keyword_check()
    {
        var report = Score(TestHost.SampleResume);
        Assert.Null(report.Keywords);
        Assert.DoesNotContain(report.Checks, c => c.Id == "keywords");
    }

    [Fact]
    public void Reports_word_and_line_counts()
    {
        var report = Score(TestHost.SampleResume);
        Assert.True(report.WordCount > 50);
        Assert.True(report.LineCount > 10);
    }
}

public class ResumeWorkflowValidationTests
{
    /// <summary>
    /// The no-fabrication guard: output that invents numbers or dates must be rejected outright,
    /// and output that only rewords must pass.
    /// </summary>
    [Fact]
    public void Validation_rejects_invented_metrics()
    {
        using var host = new TestHost();
        var (_, versionId) = host.Store.Create("Test", "Reduced errors by 38%.", "paste");
        var workflows = new ResumeWorkflows(host.Db, host.Profile, host.Router);

        var validated = workflows.Validate("Reduced errors by 92% and saved 500,000 KES.", "Reduced errors by 38%.");
        Assert.Equal(string.Empty, validated.Text);
        Assert.Contains(validated.Notes, n => n.Contains("introduced numbers or dates", StringComparison.OrdinalIgnoreCase));
        Assert.NotEqual(versionId, string.Empty);
    }

    [Fact]
    public void Validation_rejects_invented_dates()
    {
        using var host = new TestHost();
        host.Store.Create("Test", "Worked at Acme from 2018 to 2021.", "paste");
        var workflows = new ResumeWorkflows(host.Db, host.Profile, host.Router);

        var validated = workflows.Validate("Worked at Acme from 2012 to 2025.", "Worked at Acme from 2018 to 2021.");
        Assert.Equal(string.Empty, validated.Text);
        Assert.NotEmpty(validated.Notes);
    }

    [Fact]
    public void Validation_accepts_pure_rephrasing()
    {
        using var host = new TestHost();
        var workflows = new ResumeWorkflows(host.Db, host.Profile, host.Router);

        var original = "Reduced reconciliation errors by 38% across 1,200 transactions.";
        var reworded = "Cut reconciliation errors by 38% across 1,200 transactions.";
        var validated = workflows.Validate(reworded, original);

        Assert.Equal(reworded, validated.Text);
        Assert.Empty(validated.Notes);
    }

    [Fact]
    public void Validation_flags_unknown_names_without_discarding_the_output()
    {
        using var host = new TestHost();
        var workflows = new ResumeWorkflows(host.Db, host.Profile, host.Router);

        var validated = workflows.Validate("Worked at Globex on the reporting stack.", "Worked at Initech on the reporting stack.");
        Assert.NotEqual(string.Empty, validated.Text);
        Assert.Contains(validated.Notes, n => n.Contains("Globex", StringComparison.OrdinalIgnoreCase));
    }
}