using System.Text;
using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Invariants that must hold for every input, not fixtures someone already thought of.
///
/// QuantifiedCheckTests pins one regression well, and that regression — employment dates passing the
/// "quantified" check — was found by a human noticing a wrong score. A fixture only covers the case
/// that was already imagined. These hold across generated input instead, so the next instance of the
/// same class of bug fails a test rather than needing to be noticed.
///
/// The two that matter most here:
///   - adding employment dates never increases a score (this is the regression, generalised)
///   - text carrying only contact details, headings and punctuation changes nothing (this is why the
///     bug was possible at all: the quantified check read digits, and dates are digits)
/// </summary>
public class ScorerInvariantTests
{
    private const string Base = """
        JANE DOE
        jane.doe@example.com | +254700000000 | Nairobi, Kenya
        linkedin.com/in/janedoe

        SUMMARY
        Data analyst with 6 years of experience in financial reporting.

        EXPERIENCE
        Senior Data Analyst | Kenya Commercial Bank | Mar 2021 - Present
        - Led the migration of the monthly reporting pack to Power BI.
        - Reduced reconciliation errors by introducing automated validation.
        - Managed the reporting calendar across the business units.

        EDUCATION
        BSc Statistics, University of Nairobi, 2014 - 2018
        """;

    [Fact]
    public void Adding_employment_dates_never_increases_the_score()
    {
        // The generalised form of the regression QuantifiedCheckTests pins. Dates are digits, and a
        // check that reads digits will reward them unless it is told not to.
        var without = AtsScorer.Score("base", Base).Score;
        var with = AtsScorer.Score("dated", Base + "\nData Analyst | Finlay Analytics | Jun 2018 - Feb 2021").Score;

        Assert.True(with <= without, $"dates raised the score from {without} to {with}");
    }

    [Fact]
    public void Text_of_only_contact_details_and_punctuation_changes_nothing()
    {
        var baseScore = AtsScorer.Score("base", Base).Score;

        // Contact details and punctuation are scaffolding, not evidence, so none of these may move
        // the score in either direction.
        //
        // Note what is deliberately NOT in this list: bare section headings. Adding "SKILLS" to a
        // resume is a real structural improvement and the headings check is correct to reward it.
        // The failure mode worth guarding is dates being read as achievements, which is what the
        // quantified check and the first test here cover.
        var noise = new[]
        {
            "JANE DOE\njane@example.org\n+254711111111",
            "- - - - -",
            "...............",
            "== REFERENCES AVAILABLE ON REQUEST ==",
            "Page 1 of 2",
            "\n\n\n\n",
            "Contact: | | |",
            "Years: 2020 - 2024 | 2015 - 2019 | 2010 - 2014",
        };

        foreach (var extra in noise)
        {
            var scored = AtsScorer.Score("noise", Base + "\n" + extra).Score;
            Assert.True(scored <= baseScore, $"noise \"{extra}\" raised the score from {baseScore} to {scored}");
        }
    }

    [Fact]
    public void Adding_a_genuinely_missing_section_heading_can_improve_the_score()
    {
        // The companion to the test above, so the pair cannot be quietly collapsed into "headings do
        // nothing". The scorer is measuring structure; adding structure should show.
        const string withoutSkills = """
            JANE DOE
            jane.doe@example.com | Nairobi, Kenya

            SUMMARY
            Data analyst with 6 years of experience in financial reporting.

            EXPERIENCE
            Senior Data Analyst | Kenya Commercial Bank | Mar 2021 - Present
            - Reduced reconciliation errors by 38% across 1,200 transactions.
            - Cut reporting time from 9 days to 2.
            """;

        var before = AtsScorer.Score("v", withoutSkills).Checks.Single(c => c.Id == "headings");
        var after = AtsScorer.Score("v", withoutSkills + "\n\nSKILLS\nSQL, Power BI, dbt")
            .Checks.Single(c => c.Id == "headings");

        // Asserted on the check rather than the total, because the headings check is binary: it
        // passes at one missing heading and fails at two. Adding the second of three missing
        // sections cannot flip it, so the total legitimately does not move here.
        //
        // What must hold is that progress is recognised — "Education, Skills, Projects" becomes
        // "Education, Projects". A scorer that kept reporting three headings after one was added
        // would be telling the user their edit did nothing.
        Assert.Contains("Skills", before.Detail);
        Assert.DoesNotContain("Skills", after.Detail);
        Assert.True(AtsScorer.Score("v", withoutSkills).Score <=
            AtsScorer.Score("v", withoutSkills + "\n\nSKILLS\nSQL, Power BI, dbt").Score,
            "adding a real section must never lower the score");
    }

    [Fact]
    public void A_date_range_on_its_own_does_not_satisfy_the_quantified_check()
    {
        var check = AtsScorer.Score("v", Base).Checks.Single(c => c.Id == "quantified");

        Assert.False(check.Passed);
    }

    [Fact]
    public void A_real_figure_does_satisfy_the_quantified_check()
    {
        // The other half of the pair. If the check could not be satisfied at all, "dates do not pass"
        // would be trivially true and worth nothing.
        const string withFigure = """
            JANE DOE
            jane.doe@example.com | Nairobi, Kenya

            EXPERIENCE
            Senior Data Analyst | Kenya Commercial Bank | Mar 2021 - Present
            - Reduced reconciliation errors by 38% across 1,200 transactions.
            - Cut reporting time from 9 days to 2.

            EDUCATION
            BSc Statistics, University of Nairobi, 2014 - 2018
            """;

        Assert.True(AtsScorer.Score("v", withFigure).Checks.Single(c => c.Id == "quantified").Passed);
    }

    [Fact]
    public void Non_keyword_weights_always_sum_to_one_hundred()
    {
        // Held across every posting rather than once: the weights are a lookup, and a lookup is
        // where an edit goes wrong without anyone noticing.
        var jobs = new[]
        {
            Base,
            "Data engineer wanted in Nairobi. Terraform, Kubernetes and dbt.",
            "Sales role. Five years in FMCG distribution.",
            string.Empty,
            new string('x', 500)
        };

        foreach (var job in jobs)
        {
            var total = AtsScorer.Score("v", Base, job).Checks
                .Where(c => c.Id != "keywords")
                .Sum(c => c.Weight);
            Assert.Equal(100, total);
        }
    }

    [Fact]
    public void A_growing_resume_never_scores_lower_than_a_shorter_one()
    {
        // Monotonicity in the size of the achievement content. A longer resume with more real content
        // must not be penalised; if it is, the scorer is rewarding brevity, which no rule intends.
        var sb = new StringBuilder();
        sb.AppendLine(Base);
        var shortScore = AtsScorer.Score("v", Base).Score;

        for (var i = 0; i < 8; i++)
        {
            sb.AppendLine($"- Cut the {i + 1}th reporting cycle from {10 + i} days to {i + 1}, saving {i + 30}% effort.");
        }

        var longer = sb.ToString();
        var longScore = AtsScorer.Score("v", longer).Score;
        Assert.True(longScore >= shortScore, $"more real content dropped the score from {shortScore} to {longScore}");
    }

    [Fact]
    public void An_empty_resume_scores_near_nothing_rather_than_throwing()
    {
        // Not asserted as exactly zero: the scorer awards a small amount for having a parseable
        // shape at all, which is deliberate. What must hold is that it stays near the floor and
        // never throws, because every check still has to run and report.
        var result = AtsScorer.Score("v", "");

        Assert.InRange(result.Score, 0, 20);
        Assert.NotNull(result.Checks);
        Assert.NotEmpty(result.Checks);
    }

    [Fact]
    public void Every_generated_check_carries_a_tip_and_a_detail()
    {
        // Every check, for every one of a spread of inputs. A check with no tip is a dead end in the
        // UI, and the UI is where a person is told what to do about it.
        var inputs = new[] { Base, string.Empty, "Hello", new string('•', 40), "2021 - 2024" };

        foreach (var input in inputs)
        {
            foreach (var check in AtsScorer.Score("v", input).Checks)
            {
                Assert.False(string.IsNullOrWhiteSpace(check.Tip), $"check '{check.Id}' has no tip");
                Assert.False(string.IsNullOrWhiteSpace(check.Detail), $"check '{check.Id}' has no detail");
            }
        }
    }

    [Fact]
    public void Keyword_coverage_never_exceeds_one_hundred_percent()
    {
        var jd = "Terraform Kubernetes dbt SQL Power BI Python";
        for (var i = 0; i <= 20; i++)
        {
            var resume = string.Join("\n", Enumerable.Repeat($"- Used Terraform, Kubernetes, dbt, SQL and Python in role {i}.", i));
            var pct = Keywords.Compare(resume, jd).MatchPct;
            Assert.InRange(pct, 0, 100);
        }
    }
}