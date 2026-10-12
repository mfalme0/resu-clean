using ResuClean.Services;
using ResuClean.Services.Sources;
using Xunit;

namespace ResuClean.Tests;

/// <summary>
/// Pins the rules that decide whether a seeded source is safe to ship, plus the ones that were
/// previously wrong: robots.txt precedence, and the {key} placeholder.
///
/// Every test here is offline except <see cref="Every_seeded_host_still_resolves"/>, which is gated
/// behind RESUCLEAN_LIVE_SOURCE_CHECK=1 so the default suite stays network-free.
/// </summary>
public sealed class SeedLivenessTests
{
    // ---- robots.txt precedence ---------------------------------------------

    [Fact]
    public void A_broad_allow_does_not_unlock_a_specific_disallow()
    {
        // The regression this guards. "Allow: /public" next to "Disallow: /public/secret" used to
        // mark /public/secret/x fetchable, because a single matching Allow short-circuited the
        // Disallow list entirely. The longer, more specific Disallow must win.
        var rules = SourceHttp.Evaluate(
            allowed: new[] { "/public" },
            disallowed: new[] { "/public/secret" },
            path: "/public/secret/keys");

        Assert.False(rules.Allowed);
        Assert.Contains("/public/secret", rules.Reason);
    }

    [Fact]
    public void A_more_specific_allow_beats_a_broader_disallow()
    {
        var rules = SourceHttp.Evaluate(
            allowed: new[] { "/jobs" },
            disallowed: new[] { "/" },
            path: "/jobs");

        Assert.True(rules.Allowed);
    }

    [Fact]
    public void The_longest_matching_allow_still_wins_over_a_short_disallow()
    {
        // Length, not kind, decides: /jobs (5) is more specific than / (1).
        var rules = SourceHttp.Evaluate(
            allowed: new[] { "/jobs" },
            disallowed: new[] { "/" },
            path: "/jobs/123");

        Assert.True(rules.Allowed);
    }

    [Fact]
    public void A_specific_disallow_beats_a_broader_allow()
    {
        var rules = SourceHttp.Evaluate(
            allowed: new[] { "/" },
            disallowed: new[] { "/admin" },
            path: "/admin/keys");

        Assert.False(rules.Allowed);
    }

    [Fact]
    public void Allow_wins_a_tie_on_equal_length()
    {
        var rules = SourceHttp.Evaluate(
            allowed: new[] { "/x" },
            disallowed: new[] { "/y" },
            path: "/x");

        Assert.True(rules.Allowed);
    }

    [Fact]
    public void A_disallowed_path_is_refused_even_with_no_allow_rules_at_all()
    {
        var rules = SourceHttp.Evaluate(
            allowed: Array.Empty<string>(),
            disallowed: new[] { "/jobs" },
            path: "/jobs/search");

        Assert.False(rules.Allowed);
    }

    [Fact]
    public void An_empty_rule_set_allows_everything()
    {
        var rules = SourceHttp.Evaluate(
            allowed: Array.Empty<string>(),
            disallowed: Array.Empty<string>(),
            path: "/anything");

        Assert.True(rules.Allowed);
    }

    [Fact]
    public void Wildcards_and_end_anchors_are_honoured()
    {
        // BrighterMonday's real robots.txt uses exactly this shape: Disallow: /*q=*
        var search = SourceHttp.Evaluate(
            allowed: Array.Empty<string>(),
            disallowed: new[] { "/*q=*" },
            path: "/jobs?q=developer");
        Assert.False(search.Allowed);

        // ...and a trailing '$' anchors the end rather than matching a prefix.
        var exact = SourceHttp.Evaluate(
            allowed: Array.Empty<string>(),
            disallowed: new[] { "/private$" },
            path: "/private");
        Assert.False(exact.Allowed);

        var notExact = SourceHttp.Evaluate(
            allowed: Array.Empty<string>(),
            disallowed: new[] { "/private$" },
            path: "/private/keys");
        Assert.True(notExact.Allowed);
    }

    // ---- {key} substitution -------------------------------------------------

    [Fact]
    public void The_key_placeholder_is_filled_from_the_sources_own_variable()
    {
        const string Var = "RESUCLEAN_TEST_JOOBLE_KEY";
        Environment.SetEnvironmentVariable(Var, "abc123");
        try
        {
            var composed = SourceDetector.Compose(
                "https://jooble.org/api/{key}", "dev", null, Var);

            Assert.Equal("https://jooble.org/api/abc123", composed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, null);
        }
    }

    [Fact]
    public void The_key_placeholder_is_left_visible_when_no_key_is_set()
    {
        // A literal "{key}" path segment would produce a confusing 404; leaving it visible lets the
        // user see exactly what is missing.
        const string Var = "RESUCLEAN_TEST_JOOBLE_MISSING";
        Environment.SetEnvironmentVariable(Var, null);

        var composed = SourceDetector.Compose(
            "https://jooble.org/api/{key}", null, null, Var);

        Assert.Contains("{key}", composed);
    }

    [Fact]
    public void Url_composition_does_not_touch_placeholders_it_was_not_given()
    {
        Assert.Equal(
            "https://x.example/api/{key}",
            SourceDetector.Compose("https://x.example/api/{key}", null, null));
    }

    // ---- seed pack shape ---------------------------------------------------

    [Fact]
    public void Every_seed_url_is_absolute_https()
    {
        foreach (var pack in SeedPacks.All)
        {
            foreach (var entry in pack.Entries)
            {
                foreach (var candidate in new[] { entry.Url, entry.UrlTemplate })
                {
                    if (string.IsNullOrWhiteSpace(candidate)) continue;
                    Assert.True(
                        Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == "https",
                        $"{pack.Id}/{entry.Key} has a non-https or relative URL: {candidate}");
                }
            }
        }
    }

    [Fact]
    public void The_domains_that_were_dead_are_gone_from_the_seeds()
    {
        // These three failed DNS entirely when checked. They are pinned here so nobody re-adds them.
        var dead = new[] { "jobweb.co.ke", "cpoint.co.ke", "careerpoint.co.ke" };
        var everything = SeedPacks.All
            .SelectMany(p => p.Entries)
            .Select(e => e.Url ?? "")
            .Concat(SeedPacks.All.SelectMany(p => p.Entries).Select(e => e.UrlTemplate ?? ""));

        foreach (var domain in dead)
        {
            Assert.DoesNotContain(everything, u => u.Contains(domain, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Indeed_ships_the_global_host_because_the_kenya_subdomain_does_not_resolve()
    {
        var indeed = SeedPacks.All
            .SelectMany(p => p.Entries)
            .Single(e => e.Key == "indeed-ke");

        Assert.StartsWith("https://www.indeed.com/", indeed.UrlTemplate);
        Assert.DoesNotContain("ke.indeed.com", indeed.UrlTemplate);
        Assert.Equal("link", indeed.Type);
    }

    [Fact]
    public void Restricted_boards_stay_link_typed_and_disabled()
    {
        // The promise the README makes: these are never requested.
        foreach (var key in new[] { "indeed-ke", "linkedin-jobs-ke" })
        {
            var entry = SeedPacks.All.SelectMany(p => p.Entries).Single(e => e.Key == key);
            Assert.Equal("link", entry.Type);
            Assert.False(entry.EnabledByDefault);
        }
    }

    [Fact]
    public void Fetchable_sources_are_json_with_a_root_and_a_politeness_floor()
    {
        var fetchable = SeedPacks.All
            .SelectMany(p => p.Entries)
            .Where(e => e.Type is "json" or "rss")
            .ToList();

        Assert.NotEmpty(fetchable);
        foreach (var entry in fetchable)
        {
            Assert.True(entry.RateLimitMs >= 2000,
                $"{entry.Key} would hammer a source: {entry.RateLimitMs} ms");
            Assert.False(string.IsNullOrWhiteSpace(entry.Url), $"{entry.Key} is fetchable but has no URL");
        }
    }

    [Fact]
    public void The_greenhouse_pack_points_at_the_public_board_api_with_a_real_slug()
    {
        var pack = SeedPacks.All.Single(p => p.Id == "greenhouse-boards");
        Assert.NotEmpty(pack.Entries);

        foreach (var entry in pack.Entries)
        {
            Assert.StartsWith("https://boards-api.greenhouse.io/v1/boards/", entry.Url);
            var slug = entry.Url![("/v1/boards/".Length)..^("/jobs".Length)];
            Assert.False(string.IsNullOrWhiteSpace(slug), $"{entry.Key} has no company slug");
            Assert.DoesNotContain("{", slug);
        }
    }

    [Fact]
    public void Unverified_templates_say_so()
    {
        // Anything whose search path could not be confirmed must be labelled, not passed off as good.
        foreach (var key in new[] { "jobweb-ke", "careerjet-ke", "unjobs" })
        {
            var entry = SeedPacks.All.SelectMany(p => p.Entries).Single(e => e.Key == key);
            Assert.Contains("UNVERIFIED", entry.Notes, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- live check --------------------------------------------------------

    /// <summary>
    /// Resolves every seeded host over DNS. Host-level only: this never requests a page, so it is
    /// safe even for the link sources whose robots.txt forbids crawling.
    ///
    /// Opt in with RESUCLEAN_LIVE_SOURCE_CHECK=1, e.g. before publishing a release.
    /// </summary>
    [Fact]
    public async Task Every_seeded_host_still_resolves()
    {
        if (Environment.GetEnvironmentVariable("RESUCLEAN_LIVE_SOURCE_CHECK") != "1") return;

        var hosts = SeedPacks.All
            .SelectMany(p => p.Entries)
            .Select(e => e.Url ?? e.UrlTemplate)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            // Strip the {query}/{location}/{key} placeholders before parsing.
            .Select(u => u.Replace("{query}", "x").Replace("{location}", "x").Replace("{key}", "x"))
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : null)
            .Where(h => h is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(hosts);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var dead = new List<string>();

        foreach (var host in hosts)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var reply = await client.GetAsync($"https://{host}/robots.txt", cts.Token);
                // A 2xx/4xx both prove DNS resolved and something answered. Only 5xx is inconclusive.
                if ((int)reply.StatusCode >= 500)
                    dead.Add($"{host} (robots.txt returned {(int)reply.StatusCode})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                dead.Add($"{host} ({ex.GetType().Name})");
            }
        }

        Assert.True(dead.Count == 0,
            "These seeded hosts no longer answer. Fix or remove them in SeedPacks.cs:\n  - "
            + string.Join("\n  - ", dead));
    }
}