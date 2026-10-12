using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// The grounding pass. Two things must hold: with no model configured it still finds unverified
/// figures, and nothing it finds is ever silently removed — it goes to the same approval queue every
/// other piece of new information passes through.
/// </summary>
public class FactSupportTests
{
    private static FactSupportService Facts(TestHost host) => new(host.Router, host.Profile, host.Db);

    /// <summary>A resume with two claims the profile backs and one it does not.</summary>
    private const string MixedResume = """
        JANE DOE
        jane.doe@example.com | +254700000000 | Nairobi, Kenya

        SUMMARY
        Data analyst with 6 years of experience in financial reporting.

        EXPERIENCE
        Senior Data Analyst | Kenya Commercial Bank | Mar 2021 - Present
        - Reduced reconciliation errors by 38%.
        - Managed a team of 15 engineers across three sites.
        - Cut reporting time from 9 days to 2.

        EDUCATION
        BSc Statistics, University of Nairobi, 2014 - 2018
        """;

    /// <summary>
    /// Puts a fact through the same approval gate a user would, rather than writing it straight in.
    /// Using a shortcut here would let the tests pass against a profile state the app can never be in.
    /// </summary>
    private static void Approve(TestHost host, string text)
    {
        var (_, pending, _) = host.Profile.SubmitFact(
            new Models.AddFactRequest { Kind = "achievement", Text = text }, "test");
        host.Profile.ApproveFact(Assert.IsType<Models.PendingFactDto>(pending).Id);
    }

    [Fact]
    public async Task A_claim_backed_by_a_verified_fact_is_grounded()
    {
        using var host = new TestHost();
        Approve(host, "Reduced reconciliation errors by 38%.");

        var report = await Facts(host).CheckAsync("ver_1", MixedResume);

        var claim = Assert.Single(report.Claims, c => c.Text.Contains("38%", StringComparison.Ordinal));
        Assert.True(claim.Grounded);
    }

    [Fact]
    public async Task A_claim_no_fact_supports_is_flagged()
    {
        using var host = new TestHost();
        Approve(host, "Reduced reconciliation errors by 38%.");

        var report = await Facts(host).CheckAsync("ver_1", MixedResume);

        var claim = Assert.Single(report.Claims, c => c.Text.Contains("15 engineers", StringComparison.Ordinal));
        Assert.False(claim.Grounded);
        Assert.Contains("15", claim.Note);
    }

    [Fact]
    public async Task Finding_an_unsupported_claim_never_deletes_it()
    {
        using var host = new TestHost();
        var (_, versionId) = host.Store.Create("Test", MixedResume, "paste");

        await Facts(host).CheckAsync(versionId, MixedResume);

        // The resume is untouched. The claim is surfaced, never removed.
        Assert.Equal(MixedResume, host.Store.GetText(versionId));
    }

    [Fact]
    public async Task An_unsupported_claim_goes_to_the_approval_queue()
    {
        using var host = new TestHost();
        Approve(host, "Reduced reconciliation errors by 38%.");

        var report = await Facts(host).CheckAsync("ver_1", MixedResume);

        // The resume makes four claims and one of them is now backed by a fact. The other three go
        // to the queue, each with its own text.
        Assert.Equal(4, report.Checked);
        Assert.Equal(3, report.Ungrounded);
        Assert.Equal(3, host.Profile.PendingCount());
        Assert.Contains(report.Queued, p => p.Text.Contains("15 engineers", StringComparison.Ordinal));

        // Queued for approval, never verified directly.
        Assert.DoesNotContain(host.Profile.ListFacts(), f => f.Text.Contains("15 engineers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Approving_a_queued_claim_makes_it_a_verified_fact()
    {
        using var host = new TestHost();
        var report = await Facts(host).CheckAsync("ver_1", MixedResume);

        host.Profile.ApproveFact(Assert.Single(report.Queued, p => p.Text.Contains("15 engineers")).Id);

        Assert.Contains(host.Profile.ListFacts(), f => f.Text.Contains("15 engineers", StringComparison.Ordinal));
        Assert.Equal(report.Queued.Count - 1, host.Profile.PendingCount());
    }

    [Fact]
    public async Task A_resume_entirely_backed_by_the_profile_reports_nothing_flagged()
    {
        using var host = new TestHost();
        Approve(host, "Reduced reconciliation errors by 38%.");
        Approve(host, "Managed a team of 15 engineers across three sites.");
        Approve(host, "Cut reporting time from 9 days to 2.");
        Approve(host, "Data analyst with 6 years of experience in financial reporting.");

        var report = await Facts(host).CheckAsync("ver_1", MixedResume);

        Assert.Equal(0, report.Ungrounded);
        Assert.Empty(report.Queued);
    }

    [Fact]
    public async Task The_check_runs_with_no_model_configured()
    {
        using var host = new TestHost();
        Assert.False(host.Router.AnyConfigured(FactSupportService.Workflow));

        var report = await Facts(host).CheckAsync("ver_1", MixedResume);

        Assert.False(report.UsedModel);
        Assert.Equal("deterministic", report.Method);
        Assert.Equal(4, report.Checked);
        Assert.Equal(4, report.Ungrounded);
    }

    [Fact]
    public async Task Employment_dates_are_not_treated_as_unverified_claims()
    {
        using var host = new TestHost();
        Approve(host, "Reduced reconciliation errors by 38%.");

        var report = await Facts(host).CheckAsync("ver_1", MixedResume);

        // 2021, 2014 and 2018 belong to the employment and education history. Flagging them would
        // train the user to ignore the report.
        Assert.DoesNotContain(report.Queued, p => p.Text.Contains("2021", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Queued, p => p.Text.Contains("2018", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_figure_the_resumes_own_scaffolding_uses_is_not_flagged()
    {
        using var host = new TestHost();
        // "14" also appears on the role line, which is not a claim, so a bullet using it is
        // traceable inside the resume without the profile having to repeat it.
        const string text = """
            EXPERIENCE
            Senior Analyst, 14 reports | Acme | Mar 2021 - Present
            - Rebuilt 14 monthly reports.
            - Reduced errors by 38%.
            """;
        Approve(host, "Reduced errors by 38%.");

        var report = await Facts(host).CheckAsync("ver_1", text);

        // "14" is traceable inside the resume and "38%" is in the profile, so neither is flagged.
        Assert.Equal(0, report.Ungrounded);
        Assert.DoesNotContain(report.Queued, p => p.Text.Contains("14", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Queued, p => p.Text.Contains("2021", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Headings_and_contact_lines_are_not_claims()
    {
        using var host = new TestHost();
        var report = await Facts(host).CheckAsync("ver_1", "SUMMARY\nEXPERIENCE\nSKILLS\n- Replaced a system.");

        // Only the bullet is a claim. "EXPERIENCE" is a heading.
        Assert.Single(report.Claims);
        Assert.Contains("Replaced a system.", report.Claims[0].Text);
    }

    [Fact]
    public async Task An_empty_profile_flags_every_figure_rather_than_passing_everything()
    {
        using var host = new TestHost();
        var report = await Facts(host).CheckAsync("ver_1", MixedResume);

        // With nothing verified there is nothing that could support a figure, and pretending
        // otherwise would be the exact failure this feature exists to catch.
        Assert.Equal(4, report.Ungrounded);
        Assert.Equal(4, report.Queued.Count);
    }

    [Fact]
    public async Task The_report_is_kept_so_it_can_be_compared_later()
    {
        using var host = new TestHost();
        await Facts(host).CheckAsync("ver_9", MixedResume);

        var saved = Assert.Single(Facts(host).Recent());

        Assert.Equal("ver_9", saved.VersionId);
        Assert.Equal("deterministic", saved.Method);
        Assert.Equal(4, saved.Checked);
        Assert.Equal(4, saved.Ungrounded);
    }

    [Fact]
    public void A_fact_id_is_present_in_the_prompt_so_a_claim_can_cite_one()
    {
        using var host = new TestHost();
        Approve(host, "Reduced reconciliation errors by 38%.");

        var fact = Assert.Single(host.Profile.ListFacts());
        var reference = host.Profile.BuildReference();

        Assert.Contains(fact.Id, reference);
        Assert.Contains("cite the id", reference, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_reference_can_omit_ids_for_prompts_that_do_not_need_them()
    {
        using var host = new TestHost();
        Approve(host, "Reduced reconciliation errors by 38%.");

        var plain = host.Profile.BuildReference(withIds: false);

        Assert.Contains("Reduced reconciliation errors", plain);
        Assert.DoesNotContain("fact_", plain);
    }
}