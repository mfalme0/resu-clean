using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// The rewrite gate is the rule that makes model output safe to accept, so both halves of it are
/// pinned: a rewrite is only offered when it measurably helps, and a rejected rewrite is handed
/// back rather than thrown away.
///
/// These call the scorer directly rather than a model, which is the point — the gate has to be
/// decidable with no provider configured at all.
/// </summary>
public class RewriteGateTests
{
    /// <summary>The shared sample, with every measurable figure removed.</summary>
    private const string NoNumbers = """
        JANE DOE
        jane.doe@example.com | +254700000000 | Nairobi, Kenya
        linkedin.com/in/janedoe

        SUMMARY
        Data analyst with 6 years of experience in financial reporting and reporting automation.

        EXPERIENCE
        Senior Data Analyst | Kenya Commercial Bank | Mar 2021 - Present
        - Led the migration of the monthly reports to Power BI.
        - Reduced reconciliation errors by introducing automated validation on the transactions.
        - Managed a team of analysts and the reporting calendar for the business units.

        Data Analyst | Finlay Analytics | Jun 2018 - Feb 2021
        - Built SQL ETL pipelines over the transaction data.
        - Automated the regulatory reporting pack.
        - Partnered with finance on variance analysis.

        EDUCATION
        BSc Statistics, University of Nairobi, 2014 - 2018

        SKILLS
        SQL, Power BI, Excel, Python, dbt, Airflow, stakeholder management
        """;

    private static int ScoreOf(string text, string? jd = null) => AtsScorer.Score("v", text, jd).Score;

    [Fact]
    public void A_rewrite_that_loses_points_is_refused()
    {
        // The candidate reads better but drops every measurable result, which is 18 points.
        var decision = RewriteGate.Evaluate(TestHost.SampleResume, NoNumbers, null);

        Assert.False(decision.Accepted);
        Assert.True(decision.CandidateScore < decision.BaselineScore);
        Assert.Contains("scored", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("points lower", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_refused_rewrite_is_returned_not_discarded()
    {
        var decision = RewriteGate.Evaluate(TestHost.SampleResume, NoNumbers, null);

        // The whole point of refusing is that the user can still read it and overrule the gate.
        Assert.Equal(NoNumbers, decision.CandidateText);
    }

    [Fact]
    public void A_rewrite_that_gains_points_is_accepted()
    {
        var decision = RewriteGate.Evaluate(NoNumbers, TestHost.SampleResume, null);

        Assert.True(decision.Accepted);
        Assert.True(decision.CandidateScore > decision.BaselineScore);
        Assert.Contains("higher", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Identical_text_is_accepted_on_wording()
    {
        var decision = RewriteGate.Evaluate(TestHost.SampleResume, TestHost.SampleResume, null);

        Assert.True(decision.Accepted);
        Assert.Equal(decision.BaselineScore, decision.CandidateScore);
    }

    [Fact]
    public void A_rewrite_that_only_rewords_is_accepted()
    {
        // Same facts, same figures, tighter sentences. This is the case the gate exists to allow.
        var tightened = TestHost.SampleResume
            .Replace("Led the migration of", "Migrated")
            .Replace("Partnered with finance on", "Worked with finance on");

        var decision = RewriteGate.Evaluate(TestHost.SampleResume, tightened, null);

        Assert.True(decision.Accepted);
        Assert.Equal(decision.BaselineScore, decision.CandidateScore);
    }

    [Fact]
    public void Losing_job_description_keywords_is_refused_even_when_the_score_holds()
    {
        const string jd = "We need Airflow, Terraform and Ansible experience.";
        var baseline = TestHost.SampleResume + "\nAirflow, Terraform, Ansible";
        var candidate = TestHost.SampleResume + "\nExcel, Word, PowerPoint";

        var decision = RewriteGate.Evaluate(baseline, candidate, jd);

        // Assert the variable is isolated: keyword coverage carries zero weight, so the two texts
        // score identically and the keyword rule is the only thing that can decide this.
        Assert.Equal(decision.BaselineScore, decision.CandidateScore);
        Assert.True(decision.CandidateMissing > decision.BaselineMissing);
        Assert.False(decision.Accepted);
        Assert.Contains("keyword", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gaining_job_description_keywords_is_accepted_at_the_same_score()
    {
        const string jd = "We need Airflow, Terraform and Ansible experience.";
        var baseline = TestHost.SampleResume + "\nExcel, Word, PowerPoint";
        var candidate = TestHost.SampleResume + "\nAirflow, Terraform, Ansible";

        var decision = RewriteGate.Evaluate(baseline, candidate, jd);

        Assert.Equal(decision.BaselineScore, decision.CandidateScore);
        Assert.True(decision.CandidateMissing < decision.BaselineMissing);
        Assert.True(decision.Accepted);
    }

    [Fact]
    public void The_keyword_rule_is_skipped_when_there_is_no_posting()
    {
        // With no job description there is no keyword check at all, so comparing the two missing
        // counts would be comparing two meaningless zeros.
        var decision = RewriteGate.Evaluate(TestHost.SampleResume, TestHost.SampleResume, null);

        Assert.Equal(0, decision.BaselineMissing);
        Assert.Equal(0, decision.CandidateMissing);
        Assert.True(decision.Accepted);
    }

    [Fact]
    public void A_lower_score_is_refused_even_when_keywords_improve()
    {
        // Both conditions are required, not either. A rewrite can stuff keywords while wrecking the
        // measurable results that carry the most weight, and that is still a worse resume.
        const string jd = "We need Airflow, Terraform and Ansible experience.";
        var baseline = TestHost.SampleResume + "\nExcel, Word, PowerPoint";
        var candidate = NoNumbers + "\nAirflow, Terraform, Ansible";

        var decision = RewriteGate.Evaluate(baseline, candidate, jd);

        Assert.True(decision.CandidateMissing < decision.BaselineMissing);
        Assert.True(decision.CandidateScore < decision.BaselineScore);
        Assert.False(decision.Accepted);
        Assert.Contains("points lower", decision.Reason);
        Assert.Equal(candidate, decision.CandidateText);
    }

    [Fact]
    public void A_better_score_with_better_keywords_is_accepted()
    {
        const string jd = "We need Airflow, Terraform and Ansible experience.";
        var decision = RewriteGate.Evaluate(NoNumbers + "\nExcel, Word, PowerPoint",
            TestHost.SampleResume + "\nAirflow, Terraform, Ansible", jd);

        Assert.True(decision.CandidateScore > decision.BaselineScore);
        Assert.True(decision.CandidateMissing < decision.BaselineMissing);
        Assert.True(decision.Accepted);
    }

    [Fact]
    public void The_ledger_records_an_acceptance_with_its_numbers()
    {
        using var host = new TestHost();
        var routed = new ModelRouter.RoutedResult("rewritten", true, "ollama", "llama3.2", Array.Empty<string>());

        host.Ledger.Record("clean", routed, "ver_1",
            RewriteGate.Evaluate(NoNumbers, TestHost.SampleResume, null));

        var row = Assert.Single(host.Ledger.Summary(), r => r.Workflow == "clean");
        Assert.Equal(1, row.Total);
        Assert.Equal(1, row.Accepted);

        var recent = Assert.Single(host.Ledger.Recent());
        Assert.True(recent.Accepted);
        Assert.True(recent.CandidateScore > recent.BaselineScore);
        Assert.Equal("llama3.2", recent.Model);
    }

    [Fact]
    public void The_ledger_records_a_refusal_and_the_reason_for_it()
    {
        using var host = new TestHost();
        var routed = new ModelRouter.RoutedResult("rewritten", true, "groq", "llama", Array.Empty<string>());

        host.Ledger.Record("update_resume", routed, "ver_2",
            RewriteGate.Evaluate(TestHost.SampleResume, NoNumbers, null));

        var row = Assert.Single(host.Ledger.Summary(), r => r.Workflow == "update_resume");
        Assert.Equal(0, row.Accepted);
        Assert.True(row.AvgDelta < 0);

        var recent = Assert.Single(host.Ledger.Recent());
        Assert.False(recent.Accepted);
        Assert.Contains("points lower", recent.Reason);
    }

    [Fact]
    public void The_ledger_groups_by_workflow_and_model_so_a_losing_route_is_visible()
    {
        using var host = new TestHost();
        var good = new ModelRouter.RoutedResult("t", true, "p", "strong-model", Array.Empty<string>());
        var bad = new ModelRouter.RoutedResult("t", true, "p", "weak-model", Array.Empty<string>());

        host.Ledger.Record("clean", good, "v1", RewriteGate.Evaluate(NoNumbers, TestHost.SampleResume, null));
        host.Ledger.Record("clean", bad, "v2", RewriteGate.Evaluate(TestHost.SampleResume, NoNumbers, null));
        host.Ledger.Record("clean", bad, "v3", RewriteGate.Evaluate(TestHost.SampleResume, NoNumbers, null));

        var rows = host.Ledger.Summary();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Single(r => r.Model == "strong-model").Accepted);
        Assert.Equal(0, rows.Single(r => r.Model == "weak-model").Accepted);
        Assert.Equal(2, rows.Single(r => r.Model == "weak-model").Total);
    }

    [Fact]
    public void An_empty_rewrite_is_refused_rather_than_blanking_the_resume()
    {
        var decision = RewriteGate.Evaluate(TestHost.SampleResume, "   ", null);

        Assert.False(decision.Accepted);
    }

    [Fact]
    public void The_scorer_is_the_only_thing_the_gate_needs()
    {
        // No provider, no route, no database: the verdict is computable offline. That is what makes
        // it possible to enforce the rule without depending on a model being available.
        Assert.True(ScoreOf(TestHost.SampleResume) > ScoreOf(NoNumbers));
    }
}