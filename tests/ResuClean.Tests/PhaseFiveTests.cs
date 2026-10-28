using ResuClean.Models;
using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// The skill dictionary is scoring-critical, so the property that matters is that nothing reaches it
/// without approval, and that an approved term changes matching in both directions.
/// </summary>
public class SkillDictionaryTests
{
    private const string ResumeWithTools = """
        EXPERIENCE
        - Ran Synology and QNAP at home for three years.
        - Backed everything onto a Synology cluster and a QNAP share.
        - Migrated the Synology host and rebuilt the QNAP pool.
        """;

    [Fact]
    public void An_empty_dictionary_extra_set_changes_nothing()
    {
        using var host = new TestHost();
        Assert.Empty(Keywords.ExtraSkills);
    }

    [Fact]
    public void A_term_is_not_proposed_until_it_recurs()
    {
        using var host = new TestHost();
        host.Store.Create("Test", "- Used Proxmox once.", "paste");

        // One mention is not a pattern worth adding to a scoring list.
        var queued = host.Dictionary.ProposeAsync();
        Assert.DoesNotContain(queued, q => q.Text.Contains("Proxmox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_recurring_unknown_tool_is_proposed_for_approval()
    {
        using var host = new TestHost();
        host.Store.Create("Test", ResumeWithTools, "paste");

        var queued = host.Dictionary.ProposeAsync();

        Assert.Contains(queued, q => q.Text.Contains("Synology", StringComparison.OrdinalIgnoreCase));
        // Proposed, not applied: the dictionary is still untouched.
        Assert.Empty(host.Dictionary.List());
        Assert.Empty(Keywords.ExtraSkills);
    }

    [Fact]
    public void Approving_a_proposal_adds_it_to_the_dictionary_and_the_profile()
    {
        using var host = new TestHost();
        host.Store.Create("Test", ResumeWithTools, "paste");
        host.Dictionary.ProposeAsync();

        var pending = Assert.Single(host.Profile.ListPending(), p => p.Text.Contains("Synology", StringComparison.OrdinalIgnoreCase));

        // This is the endpoint's coordination rule: a dictionary approval does both jobs.
        var fact = host.Profile.ApproveFact(pending.Id);
        host.Dictionary.Add(pending.Text, origin: "dictionary");

        Assert.Contains(host.Dictionary.List(), t => t.Term.Contains("Synology", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Keywords.ExtraSkills, t => t.Contains("synology", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(fact);
    }

    [Fact]
    public void An_approved_term_changes_keyword_coverage()
    {
        using var host = new TestHost();
        const string jd = "We run Synology and QNAP for virtualisation.";

        var before = Keywords.Compare("- Some unrelated experience.", jd);

        host.Dictionary.Add("Synology");
        host.Dictionary.Add("QNAP");

        var after = Keywords.Compare("- Ran Synology and QNAP in a home lab.", jd);

        Assert.Contains("synology", before.Missing);
        Assert.Contains("synology", after.Matched);
        Assert.True(after.MatchPct > before.MatchPct);
    }

    [Fact]
    public void A_term_shorter_than_three_characters_is_refused()
    {
        using var host = new TestHost();
        var error = Assert.Throws<ArgumentException>(() => host.Dictionary.Add("R"));
        Assert.Contains("three characters", error.Message);
    }

    [Fact]
    public void Removing_a_term_takes_it_out_of_the_live_dictionary()
    {
        using var host = new TestHost();
        var term = host.Dictionary.Add("Synology");
        Assert.Contains("synology", Keywords.ExtraSkills);

        host.Dictionary.Remove(term.Id);
        Assert.DoesNotContain("synology", Keywords.ExtraSkills);
    }

    [Fact]
    public void A_two_letter_skill_no_longer_matches_inside_a_longer_word()
    {
        using var host = new TestHost();
        // "ui" is a real dictionary entry, but it used to match "build" and "quality" by substring.
        var match = Keywords.Compare("I can build quality systems.", "We need UI expertise.");
        Assert.Contains("ui", match.Missing);
    }
}

/// <summary>
/// Job quality flags. Staleness is the one that must be exactly right, because it is arithmetic and
/// has no excuse for being wrong. The judgement flags must stay silent without a quote.
/// </summary>
public class JobQualityTests
{
    private static JobQualityService Service(TestHost host) => new(host.Router, host.Profile);

    private static JobDto Job(
        string title = "Data Engineer",
        string description = "Build data pipelines.",
        string? postedAt = null,
        bool remote = false,
        string location = "") =>
        new("job_1", title, "Acme", location, "https://example.org", postedAt, description,
            "src", "manual", 0, remote, true, null, DateTime.UtcNow.ToString("O"));

    [Fact]
    public void A_fresh_posting_is_not_flagged_as_stale()
    {
        using var host = new TestHost();
        var report = Service(host).Analyse("job_1", Job(postedAt: DateTime.UtcNow.AddDays(-3).ToString("O"), description: ""));
        Assert.DoesNotContain(report.Flags, f => f.Kind == "stale");
    }

    [Fact]
    public void A_17_month_old_posting_is_flagged_without_a_model()
    {
        using var host = new TestHost();
        var report = Service(host).Analyse("job_1", Job(postedAt: DateTime.UtcNow.AddDays(-520).ToString("O"), description: ""));

        var stale = Assert.Single(report.Flags, f => f.Kind == "stale");
        Assert.Equal("warn", stale.Severity);
        Assert.Contains("520", stale.Detail);
        Assert.False(report.UsedModel);
    }

    [Fact]
    public void A_posting_at_the_boundary_is_not_yet_stale()
    {
        using var host = new TestHost();
        var report = Service(host).Analyse("job_1", Job(postedAt: DateTime.UtcNow.AddDays(-44).ToString("O"), description: ""));
        Assert.DoesNotContain(report.Flags, f => f.Kind == "stale");
    }

    [Fact]
    public void A_service_desk_job_wearing_an_engineering_title_is_flagged()
    {
        using var host = new TestHost();
        const string description =
            "You will work the service desk and own the ticket queue. You will meet SLAs and handle "
            + "incident escalation. This is a shift based role on a rota, with level 1 and l2 support duties, "
            + "including password reset and onboarding users.";
        var report = Service(host).Analyse("job_1", Job(title: "System Administrator", description: description));

        var mismatch = Assert.Single(report.Flags, f => f.Kind == "title-mismatch");
        Assert.Equal("warn", mismatch.Severity);
    }

    [Fact]
    public void A_genuine_engineering_role_is_not_flagged_as_a_mismatch()
    {
        using var host = new TestHost();
        const string description =
            "You will design and build our data platform. You will architect the pipeline, own the "
            + "infrastructure as code, deploy with Kubernetes and maintain the API and database layer.";
        var report = Service(host).Analyse("job_1", Job(title: "Data Engineer", description: description));
        Assert.DoesNotContain(report.Flags, f => f.Kind == "title-mismatch");
    }

    [Fact]
    public void A_work_authorisation_requirement_is_flagged_and_quoted()
    {
        using var host = new TestHost();
        const string description = "Candidates must be authorised to work in Kenya for this role. We cannot sponsor visas.";
        var report = Service(host).Analyse("job_1", Job(description: description));

        var eligibility = Assert.Single(report.Flags, f => f.Kind == "eligibility");
        Assert.NotNull(eligibility.Quote);
        Assert.Contains("authorised to work", eligibility.Quote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_region_restriction_is_flagged_because_remote_is_not_a_promise()
    {
        using var host = new TestHost();
        const string description = "This is a fully remote role. We hire in the EU only and operate on European time zones.";
        var report = Service(host).Analyse("job_1", Job(description: description, remote: true));

        var eligibility = Assert.Single(report.Flags, f => f.Kind == "eligibility");
        Assert.Equal("warn", eligibility.Severity);
    }

    [Fact]
    public void A_plain_posting_produces_no_flags()
    {
        using var host = new TestHost();
        var report = Service(host).Analyse("job_1", Job(description: "We are hiring a data engineer in Nairobi to build pipelines."));
        Assert.Empty(report.Flags);
        Assert.False(report.HasWarnings);
    }

    [Fact]
    public void The_quality_check_needs_no_model()
    {
        using var host = new TestHost();
        Assert.False(host.Router.AnyConfigured(JobQualityService.Workflow));
        var report = Service(host).Analyse("job_1", Job());
        Assert.Equal("deterministic", report.Method);
    }

    // ---- Closure ------------------------------------------------------------
    // The Google case: a two-day-old posting whose deadline had passed, carried for several rounds
    // of planning while its own page said "expired". Staleness cannot see that, because it only
    // looks at the posted date.

    [Fact]
    public void A_posting_whose_own_deadline_passed_is_flagged_closed()
    {
        using var host = new TestHost();
        var past = DateTime.UtcNow.AddDays(-3).ToString("O");
        var report = Service(host).Analyse("job_1", Job(description: "", postedAt: DateTime.UtcNow.AddDays(-2).ToString("O")));

        var job = Job(description: "", postedAt: DateTime.UtcNow.AddDays(-2).ToString("O")) with { DeadlineAt = past };
        var withDeadline = Service(host).Analyse("job_1", job);

        Assert.DoesNotContain(report.Flags, f => f.Kind == "closed");
        var closed = Assert.Single(withDeadline.Flags, f => f.Kind == "closed");
        Assert.Equal("warn", closed.Severity);
        Assert.Contains("closing date", closed.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_posting_that_says_it_is_closed_is_flagged_even_with_no_dates()
    {
        using var host = new TestHost();
        var report = Service(host).Analyse("job_1", Job(description: "This job listing has expired."));

        var closed = Assert.Single(report.Flags, f => f.Kind == "closed");
        Assert.Equal("warn", closed.Severity);
    }

    [Fact]
    public void A_deadline_still_in_the_future_is_information_not_a_verdict()
    {
        using var host = new TestHost();
        // Three days left is worth knowing, but it is not evidence the role is winnable.
        var job = Job(description: "", postedAt: DateTime.UtcNow.AddDays(-1).ToString("O"))
            with { DeadlineAt = DateTime.UtcNow.AddDays(3).ToString("O") };

        var report = Service(host).Analyse("job_1", job);

        Assert.DoesNotContain(report.Flags, f => f.Kind == "closed");
        var deadline = Assert.Single(report.Flags, f => f.Kind == "deadline");
        Assert.Equal("info", deadline.Severity);
    }

    [Fact]
    public void A_posting_with_no_deadline_raises_no_closure_flag()
    {
        using var host = new TestHost();
        var report = Service(host).Analyse("job_1", Job(description: "We are hiring a data engineer in Nairobi."));

        Assert.DoesNotContain(report.Flags, f => f.Kind is "closed" or "deadline");
    }
}

/// <summary>
/// Gap ramps. The safety property is the citation: a suggestion with no verified fact behind it must
/// never reach the user, because "what should I learn" is the easiest prompt to answer with fiction.
/// </summary>
public class GapRampTests
{
    private static GapRampService Service(TestHost host) => new(host.Router, host.Profile);

    private static void Approve(TestHost host, string text)
    {
        var (_, pending, _) = host.Profile.SubmitFact(new AddFactRequest { Kind = "skill", Text = text }, "test");
        host.Profile.ApproveFact(Assert.IsType<PendingFactDto>(pending).Id);
    }

    [Fact]
    public void With_no_model_and_no_fact_the_service_suggests_nothing()
    {
        using var host = new TestHost();
        var report = Service(host).SuggestAsync("Experience with Terraform", "required", new List<string> { "terraform" })
            .GetAwaiter().GetResult();

        Assert.Empty(report.Suggestions);
        Assert.Null(report.NearestFact);
        Assert.False(report.UsedModel);
    }

    [Fact]
    public void The_nearest_verified_fact_is_reported_even_with_no_model()
    {
        using var host = new TestHost();
        Approve(host, "Ran KVM virtualisation through Proxmox for three years.");

        var report = Service(host).SuggestAsync("Experience with VMware ESXi", "required", new List<string> { "vmware" })
            .GetAwaiter().GetResult();

        Assert.NotNull(report.NearestFact);
        Assert.Contains("Proxmox", report.NearestFact!);
    }

    [Fact]
    public void A_years_requirement_is_reported_as_not_closable()
    {
        using var host = new TestHost();
        var service = Service(host);

        Assert.False(service.IsClosable(RequirementsService.Blocker, new List<string> { "7", "years" }));
        Assert.True(service.IsClosable(RequirementsService.Blocker, new List<string> { "certification" }));
        Assert.True(service.IsClosable(RequirementsService.Required, new List<string> { "terraform" }));
    }

    [Fact]
    public void A_skill_gap_is_closable()
    {
        using var host = new TestHost();
        Assert.True(Service(host).IsClosable(RequirementsService.Required, new List<string> { "terraform" }));
    }
}

/// <summary>
/// Interview prep. The link that matters: a question that probes an unsupported claim is the same
/// finding the grounding pass produces, read from the other direction.
/// </summary>
public class InterviewTests
{
    private static InterviewService Service(TestHost host) => new(host.Router, host.Profile);

    [Fact]
    public void With_no_model_the_unsupported_claims_are_still_reported()
    {
        using var host = new TestHost();
        var claims = new List<FactSupportService.Claim>
        {
            new("Reduced costs 20% on Azure.", "achievement", Array.Empty<string>(), false, "no fact"),
            new("Built pipelines in SQL.", "achievement", new[] { "fact_1" }, true, "")
        };

        var report = Service(host).RisksOnly("job_1", "ver_1", claims);

        Assert.Equal("deterministic", report.Method);
        Assert.Empty(report.Questions);
        Assert.Single(report.Notes);
        Assert.Contains("Azure", report.Notes[0]);
    }

    [Fact]
    public void Every_reported_risk_is_a_claim_the_profile_does_not_support()
    {
        using var host = new TestHost();
        var claims = new List<FactSupportService.Claim>
        {
            new("Managed a team of 15 engineers.", "achievement", Array.Empty<string>(), false, "no fact"),
            new("Wrote SQL.", "skill", new[] { "fact_2" }, true, "")
        };

        var report = Service(host).RisksOnly("job_1", "ver_1", claims);
        Assert.DoesNotContain(report.Notes, n => n.Contains("Wrote SQL", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("15 engineers", StringComparison.Ordinal));
    }
}