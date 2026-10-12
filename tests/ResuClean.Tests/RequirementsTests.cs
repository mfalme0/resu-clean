using ResuClean.Models;
using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Requirement classification. The claim being tested is narrow and testable: a posting says what it
/// demands, and the split between a blocker and a nice-to-have decides whether a role is winnable
/// this week. The deterministic pass has to get that right with no model configured.
/// </summary>
public class RequirementsTests
{
    private static RequirementsService Service(TestHost host) => new(host.Router, host.Profile, host.Db);

    /// <summary>A posting written the way real postings are, with the four classes represented.</summary>
    private const string Posting = """
        Senior Data Engineer

        What you'll bring
        - 7 years of experience building data pipelines in production.
        - Strong SQL and Python.
        - Experience with Airflow or a similar scheduler.

        Nice to have
        - Familiarity with Terraform.
        - Kubernetes would be a plus.

        Responsibilities
        - Own the batch and streaming platform.
        - Partner with analysts on data models.
        """;

    private static List<RequirementsService.Requirement> Classify(TestHost host, string posting) =>
        Service(host).DeterministicRequirements(posting);

    [Fact]
    public void A_years_requirement_is_a_blocker_not_a_keyword()
    {
        using var host = new TestHost();
        var requirement = Assert.Single(Classify(host, Posting), r => r.Text.Contains("7 years", StringComparison.Ordinal));

        // Nobody closes seven years in a weekend, so it can never be filed as merely required.
        Assert.Equal(RequirementsService.Blocker, requirement.Class);
        Assert.Equal("7 years", requirement.Evidence);
    }

    [Fact]
    public void A_nice_to_have_is_preferred_whatever_section_it_sits_in()
    {
        using var host = new TestHost();
        var requirements = Classify(host, Posting);

        Assert.Equal(RequirementsService.Preferred,
            Assert.Single(requirements, r => r.Text.Contains("Terraform", StringComparison.Ordinal)).Class);
        Assert.Equal(RequirementsService.Preferred,
            Assert.Single(requirements, r => r.Text.Contains("Kubernetes", StringComparison.Ordinal)).Class);
    }

    [Fact]
    public void Lines_under_what_you_will_bring_are_required()
    {
        using var host = new TestHost();
        var requirements = Classify(host, Posting);

        Assert.Equal(RequirementsService.Required,
            Assert.Single(requirements, r => r.Text.Contains("SQL and Python", StringComparison.Ordinal)).Class);
    }

    [Fact]
    public void Duties_are_implicit_rather_than_demands()
    {
        using var host = new TestHost();
        var requirements = Classify(host, Posting);

        // "Own the batch and streaming platform" is what the job does, not a skill to evidence.
        Assert.Equal(RequirementsService.Implicit,
            Assert.Single(requirements, r => r.Text.Contains("batch and streaming", StringComparison.Ordinal)).Class);
    }

    [Fact]
    public void A_year_requirement_survives_being_inside_a_required_section()
    {
        using var host = new TestHost();
        const string posting = """
            Requirements
            - At least 7 years of professional experience.
            - Terraform.
            """;

        var years = Assert.Single(Classify(host, posting), r => r.Text.Contains("years", StringComparison.Ordinal));
        Assert.Equal(RequirementsService.Blocker, years.Class);
    }

    [Fact]
    public void A_must_have_phrase_beats_a_preferred_heading_only_when_it_is_in_the_line()
    {
        using var host = new TestHost();
        const string posting = """
            Nice to have
            - Must have a professional certification in data engineering.
            """;

        var requirement = Assert.Single(Classify(host, posting));
        Assert.Equal(RequirementsService.Blocker, requirement.Class);
    }

    [Fact]
    public void Headings_are_not_themselves_requirements()
    {
        using var host = new TestHost();
        var requirements = Classify(host, Posting);

        Assert.DoesNotContain(requirements, r => r.Text.Equals("Nice to have", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requirements, r => r.Text.Equals("Responsibilities", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(requirements, r => r.Text.Equals("What you'll bring", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_requirement_is_met_when_your_own_material_covers_it()
    {
        using var host = new TestHost();
        const string resume = "Built SQL and Python pipelines.";

        Assert.True(RequirementsService.IsMet("Strong SQL and Python", resume));
    }

    [Fact]
    public void A_requirement_is_not_met_when_it_is_absent()
    {
        using var host = new TestHost();
        const string resume = "Built pipelines in Excel.";

        Assert.False(RequirementsService.IsMet("Strong SQL and Python", resume));
    }

    [Fact]
    public void A_short_requirement_word_does_not_match_inside_a_longer_word()
    {
        using var host = new TestHost();
        // "api" is a real requirement word, and plain substring matching found it inside "capital".
        // A requirement reported met on the strength of a finance role's employer name is worse
        // than one reported unmet, because you only find out at the interview.
        const string resume = "Reported on capital markets for a regional bank.";

        Assert.False(RequirementsService.IsMet("Strong API design", resume));
        Assert.Contains("api", RequirementsService.MissingParts("Strong API design", resume));
    }

    [Fact]
    public void A_word_is_still_matched_when_it_stands_alone()
    {
        using var host = new TestHost();
        // "design" is required and "Designed" is not it, so the resume says both plainly. The
        // boundary rule has to be usable, not merely strict.
        const string resume = "Set API design standards and wrote the design doc.";

        Assert.True(RequirementsService.IsMet("Strong API design", resume));
    }

    [Fact]
    public void An_approved_fact_can_close_a_gap_that_the_resume_does_not()
    {
        using var host = new TestHost();
        var (_, pending, _) = host.Profile.SubmitFact(
            new AddFactRequest { Kind = "skill", Text = "Terraform, provisioned with modules" }, "test");
        host.Profile.ApproveFact(Assert.IsType<PendingFactDto>(pending).Id);

        var report = Service(host).AnalyseAsync("job_1", Posting, "Built SQL pipelines.").GetAwaiter().GetResult();
        var terraform = Assert.Single(report.Requirements, r => r.Text.Contains("Terraform", StringComparison.Ordinal));

        Assert.True(terraform.Met);
    }

    [Fact]
    public void The_missing_parts_are_named_rather_than_just_counted()
    {
        using var host = new TestHost();
        var report = Service(host).AnalyseAsync("job_1", Posting, "Built pipelines.").GetAwaiter().GetResult();
        var gap = Assert.Single(report.Requirements, r => r.Text.Contains("batch and streaming", StringComparison.Ordinal));

        Assert.False(gap.Met);
        Assert.Contains(gap.Missing, m => m.Contains("batch", StringComparison.Ordinal) || m.Contains("streaming", StringComparison.Ordinal));
    }

    [Fact]
    public void The_analysis_runs_with_no_model_configured()
    {
        using var host = new TestHost();
        Assert.False(host.Router.AnyConfigured(RequirementsService.Workflow));

        var report = Service(host).AnalyseAsync("job_1", Posting, "SQL, Python, Airflow, Terraform, Kubernetes.").GetAwaiter().GetResult();

        Assert.False(report.UsedModel);
        Assert.Equal("deterministic", report.Method);
        Assert.NotEmpty(report.Requirements);
    }

    [Fact]
    public void An_empty_posting_produces_no_requirements_rather_than_nonsense()
    {
        using var host = new TestHost();
        var report = Service(host).AnalyseAsync("job_1", "", "anything").GetAwaiter().GetResult();

        Assert.Empty(report.Requirements);
        Assert.Equal(0, report.UnmetBlockers);
    }

    [Fact]
    public void The_result_is_stored_so_the_job_list_can_rank_on_it()
    {
        using var host = new TestHost();
        var job = host.Jobs.SaveManualJob(new SaveJobRequest
        {
            Title = "Data Engineer", Company = "Acme", Text = Posting
        });

        Service(host).AnalyseAsync(job.Id, Posting, "SQL and Python.").GetAwaiter().GetResult();

        var stored = Service(host).ForJob(job.Id);
        Assert.NotEmpty(stored);
        Assert.Contains(stored, r => r.Class == RequirementsService.Blocker && !r.Met);
    }

    [Fact]
    public void An_unchecked_job_is_not_treated_as_clear()
    {
        using var host = new TestHost();
        var checkedJob = host.Jobs.SaveManualJob(new SaveJobRequest
        {
            Title = "Blocked", Company = "A", Text = Posting
        });
        host.Jobs.SaveManualJob(new SaveJobRequest
        {
            Title = "Unknown", Company = "B", Text = "A posting about nothing in particular, with enough words to be a posting at all, honestly."
        });

        Service(host).AnalyseAsync(checkedJob.Id, Posting, "nothing relevant here").GetAwaiter().GetResult();

        var jobs = host.Jobs.ListJobs(1, 50, null, null, null, null).Items;
        var unknown = Assert.Single(jobs, j => j.Title == "Unknown");
        var blocked = Assert.Single(jobs, j => j.Title == "Blocked");

        Assert.True(blocked.RequirementsChecked);
        Assert.Equal(0, unknown.UnmetBlockers);
        Assert.False(unknown.RequirementsChecked);

        // Unchecked sits between "clear" and "has a blocker", never above a genuinely clear role.
        Assert.Equal(1, blocked.UnmetBlockers);
    }

    [Fact]
    public void Closing_a_blocker_lifts_a_job_above_one_that_still_has_it()
    {
        using var host = new TestHost();
        var closed = host.Jobs.SaveManualJob(new SaveJobRequest { Title = "Closed", Company = "A", Text = Posting });
        var open = host.Jobs.SaveManualJob(new SaveJobRequest { Title = "Open", Company = "B", Text = Posting });

        // Everything the "closed" posting asks for is present; the other is missing all of it.
        const string everything = "7 years of experience building data pipelines in production. "
            + "Strong SQL and Python. Experience with Airflow. Familiarity with Terraform. Kubernetes. "
            + "Own the batch and streaming platform. Partner with analysts on data models.";
        Service(host).AnalyseAsync(closed.Id, Posting, everything)
            .GetAwaiter().GetResult();
        Service(host).AnalyseAsync(open.Id, Posting, "Unrelated background.")
            .GetAwaiter().GetResult();

        var jobs = host.Jobs.ListJobs(1, 50, null, null, null, null).Items;
        var closedRow = Assert.Single(jobs, j => j.Title == "Closed");
        var openRow = Assert.Single(jobs, j => j.Title == "Open");

        Assert.Equal(0, closedRow.UnmetBlockers);
        Assert.True(openRow.UnmetBlockers > 0);
        var closedIndex = jobs.ToList().FindIndex(j => j.Title == "Closed");
        var openIndex = jobs.ToList().FindIndex(j => j.Title == "Open");
        Assert.True(closedIndex < openIndex,
            "the job with no unmet blocker should rank above the one that still has it");
    }

    [Fact]
    public void Reclassifying_replaces_the_previous_reading_rather_than_stacking()
    {
        using var host = new TestHost();
        var job = host.Jobs.SaveManualJob(new SaveJobRequest { Title = "X", Company = "A", Text = Posting });
        var service = Service(host);

        var first = service.AnalyseAsync(job.Id, Posting, "SQL.").GetAwaiter().GetResult();
        var second = service.AnalyseAsync(job.Id, Posting, "SQL.").GetAwaiter().GetResult();

        Assert.Equal(first.Requirements.Count, second.Requirements.Count);
        Assert.Equal(first.Requirements.Count, service.ForJob(job.Id).Count);
    }

    [Fact]
    public void Only_postings_with_enough_text_are_worth_classifying()
    {
        using var host = new TestHost();
        host.Jobs.SaveManualJob(new SaveJobRequest { Title = "Bare", Company = "A", Text = "Short." });
        host.Jobs.SaveManualJob(new SaveJobRequest { Title = "Full", Company = "B", Text = Posting });

        var candidates = Service(host).Classifiable();
        Assert.Single(candidates);
    }
}