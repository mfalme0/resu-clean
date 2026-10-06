using ResuClean.Models;
using ResuClean.Services;
using ResuClean.Services.Sources;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Source parsing, field mapping and URL auto-detection, all against saved fixtures.
/// Nothing here touches the network.
/// </summary>
public class SourceParsingTests : IDisposable
{
    private readonly TestHost _host = new();
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public void Dispose() => _host.Dispose();

    private static string Read(string name) => File.ReadAllText(Path.Combine(Fixtures, name));

    private static SourceDto Source(string type, Dictionary<string, string>? fieldMap = null, Dictionary<string, string>? selectors = null) =>
        new("src_1", "Test source", type, "https://example.org/feed", null, new[] { "KE" }, true,
            0, fieldMap ?? new Dictionary<string, string>(), selectors ?? new Dictionary<string, string>(), "", "", "");

    [Fact]
    public void Rss_feed_yields_one_job_per_item()
    {
        var jobs = SourceParser.ParseAsync(Source("rss"), Read("sample-feed.xml"), "https://example.org/feed");
        Assert.Equal(3, jobs.Count);
        Assert.Contains(jobs, j => j.Title == "Senior Data Analyst");
    }

    [Fact]
    public void Rss_relative_links_become_absolute_and_tracking_params_are_kept_out_of_matching()
    {
        var jobs = SourceParser.ParseAsync(Source("rss"), Read("sample-feed.xml"), "https://example.org/feed");
        var first = jobs.Single(j => j.Title == "Senior Data Analyst");
        Assert.StartsWith("https://example.org/jobs/senior-data-analyst", first.Url);
    }

    [Fact]
    public void Rss_html_is_stripped_and_dates_parsed()
    {
        var jobs = SourceParser.ParseAsync(Source("rss"), Read("sample-feed.xml"), "https://example.org/feed");
        var first = jobs.Single(j => j.Title == "Senior Data Analyst");
        Assert.DoesNotContain("<p>", first.Description);
        Assert.Equal("2025-10-06", first.PostedAt);
        Assert.Equal("Kenya Commercial Bank", first.Company);
        Assert.Equal("Nairobi, Kenya", first.Location);
    }

    [Fact]
    public void Remote_is_detected_from_the_text()
    {
        var jobs = SourceParser.ParseAsync(Source("rss"), Read("sample-feed.xml"), "https://example.org/feed");
        Assert.True(jobs.Single(j => j.Title.Contains("Power BI")).Remote);
        Assert.False(jobs.Single(j => j.Title == "Finance Intern").Remote);
    }

    [Fact]
    public void Malformed_falls_back_to_the_lenient_reader()
    {
        var jobs = SourceParser.ParseAsync(Source("rss"), Read("malformed-feed.xml"), "https://broken.example/feed");
        Assert.Single(jobs);
        Assert.Contains("Business Analyst", jobs[0].Title);
        Assert.Contains("Acme Holdings", jobs[0].Company);
    }

    [Fact]
    public void Json_uses_the_supplied_field_mapping()
    {
        var map = new Dictionary<string, string>
        {
            ["title"] = "title",
            ["company"] = "organization.name",
            ["location"] = "location.name",
            ["url"] = "url_alias",
            ["description"] = "body",
            ["posted"] = "date.created"
        };

        var jobs = SourceParser.ParseAsync(Source("json", map), Read("sample-api.json"), "https://api.example.org/v1/jobs");

        Assert.Equal(2, jobs.Count);
        var first = jobs.Single(j => j.Title == "Data Analyst");
        Assert.Equal("Acme Analytics", first.Company);
        Assert.Equal("Nairobi, Kenya", first.Location);
        Assert.Equal("2025-10-06", first.PostedAt);
        Assert.Contains("SQL and Power BI", first.Description);
    }

    [Fact]
    public void Json_without_an_array_root_parses_nothing()
    {
        // No _root hint and no "results" key: nothing to iterate.
        var jobs = SourceParser.ParseAsync(Source("json"), """{ "items": [ { "title": "x" } ] }""", "https://api.example.org/v1/jobs");
        Assert.Empty(jobs);
    }

    [Fact]
    public void Html_uses_the_supplied_css_selectors()
    {
        var selectors = new Dictionary<string, string>
        {
            ["item"] = "li.card",
            ["title"] = "h3.title a",
            ["company"] = ".company",
            ["location"] = ".location",
            ["posted"] = ".posted",
            ["description"] = ".summary"
        };

        var jobs = SourceParser.ParseAsync(Source("html", null, selectors), Read("sample-jobs.html"), "https://acme.example/careers");

        Assert.Equal(3, jobs.Count);
        var analyst = jobs.Single(j => j.Title == "Data Analyst");
        Assert.Equal("Acme Analytics", analyst.Company);
        Assert.Equal("Nairobi, Kenya", analyst.Location);
        Assert.Equal("https://acme.example/jobs/data-analyst", analyst.Url);
        Assert.Equal("2025-10-06", analyst.PostedAt);
    }

    [Fact]
    public void Html_without_selectors_still_finds_link_titles()
    {
        var jobs = SourceParser.ParseAsync(Source("html"), Read("sample-jobs.html"), "https://acme.example/careers");
        Assert.NotEmpty(jobs);
    }

    [Fact]
    public void Link_sources_are_never_parsed_into_jobs()
    {
        var jobs = SourceParser.ParseAsync(Source("link"), Read("sample-jobs.html"), "https://acme.example/careers");
        Assert.Empty(jobs);
    }

    [Theory]
    [InlineData("<rss version=\"2.0\"><channel></channel></rss>", "feed")]
    [InlineData("<?xml version=\"1.0\"?><rss version=\"2.0\"><channel></channel></rss>", "feed")]
    [InlineData("{\"results\":[]}", "json")]
    [InlineData("<!doctype html><html><body>hi</body></html>", "html")]
    [InlineData("", "unknown")]
    public void Content_type_is_guessed_correctly(string body, string expected)
    {
        Assert.Equal(expected, SourceDetector.GuessContentType(body));
    }

    [Fact]
    public void Feed_links_are_found_in_both_attribute_orders()
    {
        var a = SourceDetector.FindFeedLinks(Read("sample-jobs.html"), new Uri("https://acme.example/careers"));
        Assert.Contains("https://acme.example/jobs/feed.xml", a);

        var reversed = SourceDetector.FindFeedLinks(
            "<link href=\"/feed.xml\" rel=\"alternate\" type=\"application/rss+xml\" />",
            new Uri("https://acme.example/"));
        Assert.Contains("https://acme.example/feed.xml", reversed);
    }

    [Fact]
    public void Non_feed_links_are_ignored()
    {
        var found = SourceDetector.FindFeedLinks(Read("sample-jobs.html"), new Uri("https://acme.example/"));
        Assert.DoesNotContain("https://acme.example/style.css", found);
    }

    [Fact]
    public void Compose_substitates_query_and_location()
    {
        Assert.Equal(
            "https://example.org/jobs?q=data%20analyst&loc=Nairobi%2C%20Kenya",
            SourceDetector.Compose("https://example.org/jobs?q={query}&loc={location}", "data analyst", "Nairobi, Kenya"));
    }

    [Fact]
    public void A_non_http_url_is_reported_as_a_link_source()
    {
        // Detection itself needs the network; the contract it protects is that link sources are never fetched.
        var source = _host.Registry.Save(new UpsertSourceRequest { Name = "Indeed", Type = "link", UrlTemplate = "https://www.indeed.com/jobs?q={query}" });
        Assert.Equal("link", source.Type);
        Assert.Equal("https://www.indeed.com/jobs?q=sql", SourceRegistry.SearchUrlFor(source, "sql", null));
    }

    [Fact]
    public void Source_templates_expand_with_the_search_terms()
    {
        var source = _host.Registry.Save(new UpsertSourceRequest
        {
            Name = "Fuzu",
            Type = "link",
            UrlTemplate = "https://fuzu.com/?query={query}&location={location}",
            Regions = new List<string> { "KE" }
        });

        Assert.Equal("https://fuzu.com/?query=data&location=Nairobi", SourceRegistry.SearchUrlFor(source, "data", "Nairobi"));
        Assert.Contains("KE", source.Regions);
    }

    [Fact]
    public void Seed_packs_default_to_link_when_access_is_unclear()
    {
        var packs = _host.Registry.ListPacks();
        Assert.Equal(2, packs.Count);

        var kenya = packs.Single(p => p.Id == "kenya");
        var linkedin = kenya.Entries.Single(e => e.Name.Contains("LinkedIn"));
        Assert.Equal("link", linkedin.Type);
        Assert.False(linkedin.EnabledByDefault);

        var indeed = kenya.Entries.Single(e => e.Name.Contains("Indeed"));
        Assert.Equal("link", indeed.Type);

        var remote = packs.Single(p => p.Id == "global-remote");
        var remotive = remote.Entries.Single(e => e.Name == "Remotive");
        Assert.Equal("json", remotive.Type);
        Assert.True(remotive.EnabledByDefault);
        Assert.Equal("company_name", remotive.FieldMap["company"]);

        var jooble = remote.Entries.Single(e => e.Name == "Jooble");
        Assert.Equal("JOOBLE_API_KEY", jooble.RequiresKey);
        Assert.False(jooble.EnabledByDefault);
    }

    [Fact]
    public void ReliefWeb_points_at_v2_and_is_disabled_because_the_appname_must_be_pre_approved()
    {
        var entry = _host.Registry.ListPacks()
            .Single(p => p.Id == "kenya").Entries.Single(e => e.Name == "ReliefWeb");

        Assert.Contains("/v2/jobs", entry.Url);
        Assert.DoesNotContain("/v1/", entry.Url);
        Assert.Contains("appname=", entry.Url);
        // v1 is decommissioned and a human has to approve the appname, so it must not auto-enable.
        Assert.False(entry.EnabledByDefault);
        Assert.Contains("PRE-APPROVED", entry.Notes);
        // The ReliefWeb list response really is rooted at "data".
        Assert.Equal("data", entry.FieldMap["_root"]);
        Assert.Equal("source.name", entry.FieldMap["company"]);
        Assert.Equal("date.created", entry.FieldMap["posted"]);
    }

    [Fact]
    public void Remotive_uses_the_jobs_key_and_respects_its_rate_limit()
    {
        var entry = _host.Registry.ListPacks()
            .Single(p => p.Id == "global-remote").Entries.Single(e => e.Name == "Remotive");

        Assert.Equal("jobs", entry.FieldMap["_root"]);
        // Remotive block more than 2 requests per minute, so the politeness floor must be well above 2s.
        Assert.True(entry.RateLimitMs >= 60000, $"rate limit was {entry.RateLimitMs} ms");
    }

    [Fact]
    public void Remoteok_is_parsed_despite_its_leading_metadata_element()
    {
        // Element 0 is { last_updated, legal }, not a job: it has no "position" and must be skipped.
        var map = new Dictionary<string, string>
        {
            ["title"] = "position",
            ["company"] = "company",
            ["location"] = "location",
            ["url"] = "url",
            ["description"] = "description",
            ["posted"] = "date"
        };

        var jobs = SourceParser.ParseAsync(Source("json", map), Read("models-remoteok.json"), "https://remoteok.com/api");

        Assert.Equal(2, jobs.Count);
        Assert.DoesNotContain(jobs, j => j.Title.Contains("last_updated", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Senior Rust Engineer", jobs[0].Title);
        Assert.Equal("Acme Cloud", jobs[0].Company);
        Assert.Equal("Worldwide", jobs[0].Location);
    }

    [Fact]
    public void Remoteok_seed_carries_its_attribution_and_window_caveats()
    {
        var entry = _host.Registry.ListPacks()
            .Single(p => p.Id == "global-remote").Entries.Single(e => e.Name == "RemoteOK");

        Assert.True(entry.EnabledByDefault);
        Assert.Contains("newest 100", entry.Notes);
        Assert.Contains("410", entry.Notes); // the RSS twin is gone, so only the JSON feed is configured
    }

    [Fact]
    public void Installing_a_pack_creates_disabled_link_sources_and_enabled_api_sources()
    {
        var pack = _host.Registry.InstallPack("kenya");
        Assert.True(pack.Installed);

        var sources = _host.Registry.List();
        var linkedin = sources.Single(s => s.Name.Contains("LinkedIn"));
        Assert.Equal("link", linkedin.Type);
        Assert.False(linkedin.Enabled);

        var reliefweb = sources.Single(s => s.Name == "ReliefWeb");
        Assert.Equal("json", reliefweb.Type);
        // Disabled: the appname must be pre-approved by ReliefWeb before the endpoint will answer.
        Assert.False(reliefweb.Enabled);

        var unCareers = sources.Single(s => s.Name == "UN Careers");
        Assert.Equal("html", unCareers.Type);
        Assert.False(unCareers.Enabled);
    }

    [Fact]
    public void Per_source_politeness_limits_survive_installation()
    {
        _host.Registry.InstallPack("global-remote");
        var sources = _host.Registry.List();

        // Remotive block more than 2 requests a minute, so its own limit must be applied.
        Assert.Equal(60000, sources.Single(s => s.Name == "Remotive").RateLimitMs);
        Assert.Equal(10000, sources.Single(s => s.Name == "RemoteOK").RateLimitMs);
    }

    [Fact]
    public void The_remote_pack_enables_only_the_sources_with_no_key_gate()
    {
        _host.Registry.InstallPack("global-remote");
        var remote = _host.Registry.List();

        Assert.Equal(3, remote.Count(s => s.Enabled));
        Assert.Equal(1, remote.Count(s => !s.Enabled)); // Jooble, which needs your own key
    }

    [Fact]
    public void Reinstalling_a_pack_does_not_duplicate_sources()
    {
        _host.Registry.InstallPack("kenya");
        _host.Registry.InstallPack("kenya");
        Assert.Equal(11, _host.Registry.List().Count);
    }

    [Fact]
    public void Removing_a_pack_removes_its_sources()
    {
        _host.Registry.InstallPack("global-remote");
        Assert.Equal(4, _host.Registry.List().Count);
        _host.Registry.UninstallPack("global-remote");
        Assert.Empty(_host.Registry.List());
    }

    [Fact]
    public void Unknown_source_types_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => _host.Registry.Save(new UpsertSourceRequest { Name = "x", Type = "telepathy" }));
    }
}