using ResuClean.Services;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Cross-source dedupe: the same posting arrives from several boards with different titles,
/// tracking parameters and blank fields.
/// </summary>
public class DedupeTests
{
    [Theory]
    [InlineData("Senior Data Analyst", "data analyst")]
    [InlineData("Sr Data Analyst (Nairobi)", "sr data analyst")]
    [InlineData("Data Analyst - Acme", "data analyst acme")]
    [InlineData("DATA ANALYST", "data analyst")]
    public void Normalisation_strips_seniority_and_separators(string input, string expected)
    {
        Assert.Equal(expected, Dedupe.Normalize(input));
    }

    [Fact]
    public void Tracking_parameters_do_not_affect_url_identity()
    {
        Assert.True(Dedupe.SameUrl(
            "https://example.org/jobs/analyst?utm_source=rss&utm_medium=feed&id=42",
            "https://example.org/jobs/analyst?id=42"));
    }

    [Fact]
    public void Different_paths_are_different_urls()
    {
        Assert.False(Dedupe.SameUrl("https://example.org/jobs/a", "https://example.org/jobs/b"));
    }

    [Fact]
    public void Identical_titles_and_companies_are_the_same_posting()
    {
        Assert.True(Dedupe.Same("Data Analyst", "Acme", "Data Analyst", "Acme"));
    }

    [Fact]
    public void Seniority_differences_are_treated_as_the_same_posting()
    {
        Assert.True(Dedupe.Same(
            "Senior Data Analyst",
            "Acme Analytics",
            "Data Analyst",
            "Acme Analytics"));
    }

    [Fact]
    public void A_blank_company_on_one_side_still_matches()
    {
        Assert.True(Dedupe.Same("Data Analyst", "", "Data Analyst", "Acme"));
        Assert.True(Dedupe.Same("Data Analyst", "Acme", "Data Analyst", ""));
    }

    [Fact]
    public void Different_companies_are_different_postings()
    {
        Assert.False(Dedupe.Same("Data Analyst", "Acme Analytics", "Data Analyst", "Coastal Cloud"));
    }

    [Fact]
    public void Different_roles_are_different_postings()
    {
        Assert.False(Dedupe.Same("Data Analyst", "Acme", "Marketing Manager", "Acme"));
    }

    [Fact]
    public void Slightly_different_titles_at_the_same_company_are_different()
    {
        Assert.False(Dedupe.Same("Data Analyst", "Acme", "Senior Data Scientist", "Acme"));
    }

    [Fact]
    public void Near_identical_titles_with_fuzzy_companies_still_match()
    {
        Assert.True(Dedupe.Same("Data Analyst", "Acme Analytics Ltd", "Data Analyst", "Acme Analytics Limited"));
    }

    [Fact]
    public void Similarity_is_bounded_and_symmetric()
    {
        var a = Dedupe.Similarity("data analyst nairobi", "data analyst");
        var b = Dedupe.Similarity("data analyst", "data analyst nairobi");
        Assert.InRange(a, 0, 1);
        Assert.Equal(a, b, 5);
    }

    [Fact]
    public void Similarity_of_identical_strings_is_one()
    {
        Assert.Equal(1.0, Dedupe.Similarity("data analyst", "data analyst"), 5);
    }

    [Fact]
    public void Cross_source_scenarios_resolve_as_expected()
    {
        // Board A lists it with a seniority prefix and tracking parameters; Board B does not.
        var boardA = ("Senior Data Analyst", "Acme Analytics", "https://example.org/jobs/1?utm_source=a");
        var boardB = ("Data Analyst", "Acme Analytics", "https://example.org/jobs/1");

        Assert.True(Dedupe.SameUrl(boardA.Item3, boardB.Item3));
        Assert.True(Dedupe.Same(boardA.Item1, boardA.Item2, boardB.Item1, boardB.Item2));

        // A genuinely different role at the same company must survive.
        var boardC = ("Finance Intern", "Acme Analytics", "https://example.org/jobs/2");
        Assert.False(Dedupe.Same(boardA.Item1, boardA.Item2, boardC.Item1, boardC.Item2));
    }
}