using ResuClean.Models;
using ResuClean.Services.Sources;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Provenance answers one question: can this host tell me the posting is gone?
///
/// It matters because of a specific failure. Four Jumia roles were carried as live across three
/// sessions while the Greenhouse slug 404d — that board never held engineering roles. Nothing
/// noticed because nothing ever asked a system capable of saying no.
///
/// An aggregator is a convenience and not evidence: it republishes someone else's listing and may
/// serve it for weeks after the employer's board dropped it. So a 200 from one proves nothing, and a
/// tool that cannot name that difference is handing the user false confidence.
/// </summary>
public class SourceProvenanceTests
{
    [Theory]
    // Greenhouse, in the host forms it actually serves from. The Jumia case lives here.
    [InlineData("https://boards.greenhouse.io/jumia/jobs/12345", SourceProvenance.EmployerAts)]
    [InlineData("https://boards-api.greenhouse.io/v1/boards/stripe/jobs", SourceProvenance.EmployerAts)]
    [InlineData("https://job-boards.greenhouse.io/datadog", SourceProvenance.EmployerAts)]
    [InlineData("https://jobs.lever.co/netflix", SourceProvenance.EmployerAts)]
    [InlineData("https://jobs.ashbyhq.com/openai", SourceProvenance.EmployerAts)]
    [InlineData("https://myworkdayjobs.com/en-US/Acme_Career", SourceProvenance.EmployerAts)]
    [InlineData("https://apply.workable.com/acme/", SourceProvenance.EmployerAts)]
    public void An_employers_own_ats_is_authoritative(string url, string expected)
    {
        Assert.Equal(expected, SourceProvenance.For(url));
    }

    [Theory]
    [InlineData("https://remotive.com/api/remote-jobs?limit=100", SourceProvenance.Aggregator)]
    [InlineData("https://remoteok.com/api", SourceProvenance.Aggregator)]
    [InlineData("https://jooble.org/api/KEY", SourceProvenance.Aggregator)]
    [InlineData("https://www.arbeitnow.com/api/job-board-api", SourceProvenance.Aggregator)]
    [InlineData("https://www.indeed.com/jobs?q=data", SourceProvenance.Aggregator)]
    public void A_third_party_index_is_not_authoritative(string url, string expected)
    {
        Assert.Equal(expected, SourceProvenance.For(url));
    }

    [Theory]
    [InlineData("https://weworkremotely.com/remote-jobs", SourceProvenance.Board)]
    [InlineData("https://wellfound.com/jobs", SourceProvenance.Board)]
    public void A_general_listings_site_is_neither(string url, string expected)
    {
        Assert.Equal(expected, SourceProvenance.For(url));
    }

    [Theory]
    [InlineData("https://www.jumia.com/kenya/careers/", SourceProvenance.Unknown)]
    [InlineData("https://some-company.wixsite.com/careers", SourceProvenance.Unknown)]
    [InlineData("", SourceProvenance.Unknown)]
    [InlineData("not a url at all", SourceProvenance.Unknown)]
    public void An_unrecognised_host_is_unknown_rather_than_guessed(string url, string expected)
    {
        // The conservative default is the whole point. A host we cannot place is not the employer's
        // own system, and defaulting it to authoritative is what produced the original problem.
        Assert.Equal(expected, SourceProvenance.For(url));
    }

    [Theory]
    [InlineData("not-greenhouse.io", SourceProvenance.Unknown)]
    [InlineData("greenhouse.io.evil.example", SourceProvenance.Unknown)]
    [InlineData("mygreenhouse.io", SourceProvenance.Unknown)]
    public void A_host_that_merely_contains_a_known_name_is_not_matched(string host, string expected)
    {
        // Suffix matching has to respect the dot. "evil-greenhouse.io" is not Greenhouse, and
        // treating it as the employer's own ATS would be the exact error this guards against.
        Assert.Equal(expected, SourceProvenance.ForHost(host));
    }

    [Fact]
    public void A_real_subdomain_does_match()
    {
        // The other half of the pair above, so the boundary cannot be "never match a subdomain".
        Assert.Equal(SourceProvenance.EmployerAts, SourceProvenance.ForHost("boards.greenhouse.io"));
        Assert.Equal(SourceProvenance.EmployerAts, SourceProvenance.ForHost("www.myworkdayjobs.com"));
    }

    [Fact]
    public void Www_and_bare_forms_are_the_same_source()
    {
        Assert.Equal(SourceProvenance.Aggregator, SourceProvenance.For("https://www.remotive.com/api"));
        Assert.Equal(SourceProvenance.Aggregator, SourceProvenance.For("https://remotive.com/api"));
    }

    [Fact]
    public void Every_recognised_value_can_be_described_for_the_screen()
    {
        // The UI shows this sentence, so an unknown value must produce something rather than a blank.
        foreach (var value in new[]
                 {
                     SourceProvenance.EmployerAts, SourceProvenance.Aggregator,
                     SourceProvenance.Board, SourceProvenance.Unknown, "something-else"
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(SourceProvenance.Describe(value)));
        }
    }

    [Fact]
    public void The_aggregator_description_says_why_it_is_not_evidence()
    {
        var text = SourceProvenance.Describe(SourceProvenance.Aggregator);

        Assert.Contains("not evidence", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_source_with_no_url_is_unknown()
    {
        // A search-URL source has nothing to classify by host, and guessing would be worse.
        Assert.Equal(SourceProvenance.Unknown, SourceProvenance.For(null));
    }
}

/// <summary>
/// Provenance as it reaches a saved source.
///
/// The classifier tests cover the rule. These cover the wiring, which is where it silently rots: a
/// seed entry that gains a new host, or a save path that forgets the column, and every source
/// quietly reverts to "unknown" — which is the same as saying nothing while looking like an answer.
/// </summary>
public class SeededSourceProvenanceTests
{
    [Fact]
    public void The_greenhouse_pack_is_the_employers_own_ats()
    {
        // The high-leverage pack. Every entry is a per-company board, so a 404 from one is the
        // authoritative answer about that employer's role. This is the case that matters most.
        var pack = SeedPacks.All.Single(p => p.Id == "greenhouse-boards");

        Assert.NotEmpty(pack.Entries);
        foreach (var entry in pack.Entries)
        {
            Assert.Equal(SourceProvenance.EmployerAts, entry.Provenance);
        }
    }

    [Fact]
    public void Every_keyless_feed_pack_entry_is_marked_as_something_other_than_an_employer_ats()
    {
        // Arbeitnow, Jobicy, Remotive and RemoteOK all republish. Treating one as an employer's own
        // system would let a dead posting look confirmed, which is the Jumia failure again.
        var packs = new[] { "open-feeds", "global-remote" };

        foreach (var id in packs)
        {
            var entries = SeedPacks.All.Single(p => p.Id == id).Entries;
            Assert.NotEmpty(entries);

            foreach (var entry in entries.Where(e => e.Type is "json" or "rss"))
            {
                Assert.NotEqual(SourceProvenance.EmployerAts, entry.Provenance);
                Assert.NotEqual(SourceProvenance.Unknown, entry.Provenance);
            }
        }
    }

    [Fact]
    public void No_seeded_entry_that_points_at_an_ats_is_left_unknown()
    {
        // The drift guard: any seeded URL whose host the classifier recognises must carry the
        // matching verdict. Catches a new pack entry added without a provenance, and a new ATS host
        // added to the list that the seed packs then fail to reflect.
        foreach (var entry in SeedPacks.All.SelectMany(p => p.Entries))
        {
            var expected = SourceProvenance.For(entry.Url ?? entry.UrlTemplate);
            if (expected == SourceProvenance.Unknown) continue;

            Assert.True(expected == entry.Provenance,
                $"{entry.Key} points at {entry.Url ?? entry.UrlTemplate}, which is {expected}, " +
                $"but the entry says {entry.Provenance}");
        }
    }

    [Fact]
    public void An_installed_pack_carries_provenance_into_the_database()
    {
        // Through the real install path, not the classifier. This is the assertion that a save which
        // forgot the column would fail.
        using var host = new TestHost();
        host.Registry.InstallPack("greenhouse-boards");

        var sources = host.Registry.List();
        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            Assert.NotEqual(SourceProvenance.Unknown, source.Provenance);
        }
    }

    [Fact]
    public void A_source_saved_with_an_unrecognised_provenance_is_stored_as_unknown()
    {
        using var host = new TestHost();
        var saved = host.Registry.Save(new UpsertSourceRequest
        {
            Name = "Odd board",
            Type = "rss",
            Url = "https://example.org/feed",
            Provenance = "definitely-trustworthy"
        });

        Assert.Equal(SourceProvenance.Unknown, saved.Provenance);
    }
}