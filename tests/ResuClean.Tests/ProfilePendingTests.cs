using ResuClean.Models;
using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// The NEW INFO RULE: nothing new enters profile_facts without an explicit approval decision,
/// and the user is never asked about the same fact twice.
/// </summary>
public class ProfilePendingTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Submitting_a_fact_queues_it_instead_of_verifying_it()
    {
        var (fact, pending, duplicate) = _host.Profile.SubmitFact(
            new AddFactRequest { Kind = "achievement", Text = "Cut reporting time from 9 days to 2" },
            "profile_editor");

        Assert.Null(fact);
        Assert.False(duplicate);
        Assert.NotNull(pending);
        Assert.Empty(_host.Profile.ListFacts());
        Assert.Single(_host.Profile.ListPending("pending"));
    }

    [Fact]
    public void Approving_moves_the_fact_into_the_profile()
    {
        var (_, pending, _) = _host.Profile.SubmitFact(
            new AddFactRequest { Kind = "skill", Text = "Advanced dbt modelling" }, "profile_editor");
        Assert.NotNull(pending);

        var fact = _host.Profile.ApproveFact(pending!.Id);
        Assert.Equal("skill", fact.Kind);
        Assert.Single(_host.Profile.ListFacts());
        Assert.Empty(_host.Profile.ListPending("pending"));
    }

    [Fact]
    public void Rejecting_keeps_it_out_of_the_profile()
    {
        var (_, pending, _) = _host.Profile.SubmitFact(
            new AddFactRequest { Kind = "other", Text = "Speaks nine languages" }, "profile_editor");

        _host.Profile.RejectFact(pending!.Id);

        Assert.Empty(_host.Profile.ListFacts());
        Assert.Empty(_host.Profile.ListPending("pending"));
        Assert.Single(_host.Profile.ListPending("rejected"));
    }

    [Fact]
    public void Approving_the_same_item_twice_throws()
    {
        var (_, pending, _) = _host.Profile.SubmitFact(
            new AddFactRequest { Kind = "other", Text = "Wrote a book in 2021" }, "profile_editor");
        _host.Profile.ApproveFact(pending!.Id);

        var error = Assert.Throws<InvalidOperationException>(() => _host.Profile.ApproveFact(pending.Id));
        Assert.Contains("approved", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_exact_duplicate_is_not_queued_twice()
    {
        _host.Profile.AddFactToPending("achievement", "Reduced errors by 38%", "resume_update");
        var second = _host.Profile.AddFactToPending("achievement", "Reduced errors by 38%", "resume_update");

        Assert.Single(second); // returns the existing row rather than creating a new one
        Assert.Single(_host.Profile.ListPending("pending"));
    }

    [Fact]
    public void A_duplicate_ignoring_punctuation_is_still_a_duplicate()
    {
        _host.Profile.AddFactToPending("achievement", "Reduced errors by 38%", "resume_update");
        _host.Profile.AddFactToPending("achievement", "reduced  errors  by  38 %", "resume_update");
        Assert.Single(_host.Profile.ListPending("pending"));
    }

    [Fact]
    public void Something_already_verified_is_never_queued()
    {
        var (_, pending, _) = _host.Profile.SubmitFact(
            new AddFactRequest { Kind = "role", Text = "Senior Data Analyst" }, "profile_editor");
        _host.Profile.ApproveFact(pending!.Id);

        var queued = _host.Profile.AddFactToPending("role", "Senior Data Analyst", "resume_update");
        Assert.Empty(queued);
        Assert.Empty(_host.Profile.ListPending("pending"));
    }

    [Fact]
    public void A_near_duplicate_is_flagged_for_review()
    {
        _host.Profile.AddFactToPending("achievement", "Led the payments team of eight", "resume_update");
        var second = _host.Profile.AddFactToPending("achievement", "Led payments team of eight analysts", "resume_update");

        Assert.Single(second);
        Assert.Contains("fuzzy_similar_to", second[0].Meta.Keys);
    }

    [Fact]
    public void Pending_count_is_reported_for_the_nav_badge()
    {
        Assert.Equal(0, _host.Profile.PendingCount());
        _host.Profile.AddFactToPending("skill", "Airflow", "resume_update");
        _host.Profile.AddFactToPending("skill", "Terraform", "resume_update");
        Assert.Equal(2, _host.Profile.PendingCount());
    }

    [Fact]
    public void Empty_facts_are_ignored()
    {
        Assert.Empty(_host.Profile.AddFactToPending("skill", "   ", "resume_update"));
        Assert.Equal(0, _host.Profile.PendingCount());
    }

    [Theory]
    [InlineData("Led the payments team", "Led payments team", true)]
    [InlineData("Led the payments team of eight", "Led payments team of eight analysts", true)]
    [InlineData("Reduced errors by 38%", "Cut errors by 12%", false)]
    [InlineData("Same text", "Same  text!", true)]
    public void Similarity_separates_near_duplicates_from_different_facts(string a, string b, bool expected)
    {
        Assert.Equal(expected, ProfileService.FuzzyMatch(a, b));
    }

    [Fact]
    public void The_reference_block_lists_verified_facts_only()
    {
        var (_, pending, _) = _host.Profile.SubmitFact(
            new AddFactRequest { Kind = "achievement", Text = "Cut reporting time by 78%" }, "profile_editor");

        var before = _host.Profile.BuildReference();
        Assert.DoesNotContain("Cut reporting time", before);

        _host.Profile.ApproveFact(pending!.Id);

        var after = _host.Profile.BuildReference();
        Assert.Contains("Cut reporting time by 78%", after);
        Assert.Contains("VERIFIED FACTS", after);
    }

    [Fact]
    public void Voice_and_contact_are_persisted()
    {
        _host.Profile.UpdateProfile(new UpsertProfileRequest
        {
            Voice = "Direct, plain, no buzzwords",
            Contact = new Dictionary<string, string> { ["email"] = "jane@example.com" }
        });

        var profile = _host.Profile.GetProfile();
        Assert.Equal("Direct, plain, no buzzwords", profile.Voice);
        Assert.Equal("jane@example.com", profile.Contact["email"]);
    }

    [Fact]
    public void Approving_an_unknown_id_is_a_404()
    {
        Assert.Throws<KeyNotFoundException>(() => _host.Profile.ApproveFact("pend_missing"));
    }
}