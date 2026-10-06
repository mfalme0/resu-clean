using System.Text.RegularExpressions;
using ResuClean.Models;

namespace ResuClean.Services.Sources;

/// <summary>
/// "Add source by URL": probes a URL and works out whether it is an RSS/Atom feed, a JSON API,
/// an HTML page needing selectors, or a page we must not fetch (so the user gets a search link instead).
/// </summary>
public sealed partial class SourceDetector
{
    public sealed record Detection(string Url, string SuggestedType, string Confidence, string? FeedUrl,
        IReadOnlyList<string> Candidates, IReadOnlyList<SourcePreviewItem> Preview, IReadOnlyList<string> Notes);

    private static readonly string[] CommonFeedPaths =
    {
        "/feed", "/feed/", "/rss", "/rss.xml", "/feed.xml", "/atom.xml", "/index.xml",
        "/jobs/feed", "/careers/feed", "/career/feed", "/vacancies/feed", "/vacancies.rss",
        "/jobs.rss", "/careers.rss", "/blog/feed", "/news/feed", "/rss-feed"
    };

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex LinkTags();

    [GeneratedRegex(@"rel\s*=\s*[""']([^""']+)[""'][^>]*href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex RelThenHref();

    [GeneratedRegex(@"href\s*=\s*[""']([^""']+)[""'][^>]*rel\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex HrefThenRel();

    [GeneratedRegex(@"<title[^>]*>([\s\S]{0,200}?)</title>", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex HtmlTitle();

    [GeneratedRegex(@"(?i)\b(cloudflare|captcha|access denied|are you a robot|unusual traffic|enable javascript and cookies)", RegexOptions.None, 2000)]
    private static partial Regex BotWall();

    public async Task<Detection> DetectAsync(SourceHttp http, string url, string? query, string? location, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var candidates = new List<string>();
        var normalized = url.Trim();

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            // Not fetchable: hand back a search link so the workflow still works.
            return new Detection(normalized, "link", "low", null, candidates,
                Array.Empty<SourcePreviewItem>(),
                new[] { "That is not an http(s) URL, so it cannot be fetched automatically. Save it as a 'link' source: resu-clean will build the search URL and you open it in your browser." });
        }

        var robots = await http.IsAllowedAsync(uri, ct).ConfigureAwait(false);
        if (!robots.Allowed)
        {
            notes.Add($"robots.txt disallows this path ({robots.Reason}). Saving as a 'link' source is the right choice.");
            return new Detection(normalized, "link", "high", null, candidates, Array.Empty<SourcePreviewItem>(), notes);
        }

        // Probe the URL itself.
        var result = await http.GetAsync(normalized, null, false, ct).ConfigureAwait(false);
        candidates.Add(normalized);
        var body = result.Body;
        var contentType = GuessContentType(body);

        // 1. Feed?
        if (contentType == "feed")
        {
            notes.Add("This URL returns a valid RSS/Atom feed.");
            var preview = await PreviewAsync(http, normalized, "rss", new Dictionary<string, string>(), new Dictionary<string, string>(), query, location, ct);
            return new Detection(normalized, "rss", "high", normalized, candidates, preview, notes);
        }

        // 2. JSON?
        if (contentType == "json")
        {
            var looksLikeJobs = body.Contains("\"title\"", StringComparison.OrdinalIgnoreCase) ||
                                body.Contains("\"job", StringComparison.OrdinalIgnoreCase) ||
                                body.Contains("\"position", StringComparison.OrdinalIgnoreCase);
            var type = looksLikeJobs ? "json" : "link";
            notes.Add(looksLikeJobs
                ? "This URL returns JSON that looks like job listings. Confirm the field mapping before saving."
                : "This URL returns JSON but it does not look like a job listing API. Saved as a link source.");
            var preview = looksLikeJobs
                ? await PreviewAsync(http, normalized, "json", DefaultJsonMap, new Dictionary<string, string>(), query, location, ct)
                : Array.Empty<SourcePreviewItem>();
            return new Detection(normalized, type, looksLikeJobs ? "medium" : "low", null, candidates, preview, notes);
        }

        // 3. HTML: look for advertised feeds.
        if (contentType == "html")
        {
            if (BotWall().IsMatch(body))
            {
                notes.Add("This page looks like bot protection, not a job list. Save it as a 'link' source.");
                return new Detection(normalized, "link", "medium", null, candidates, Array.Empty<SourcePreviewItem>(), notes);
            }

            foreach (var feed in FindFeedLinks(body, uri))
            {
                if (!candidates.Contains(feed)) candidates.Add(feed);
            }

            // Try each advertised feed.
            foreach (var feed in candidates.Where(c => c != normalized).Take(4))
            {
                var feedResult = await http.GetAsync(feed, null, false, ct).ConfigureAwait(false);
                if (GuessContentType(feedResult.Body) != "feed") continue;
                notes.Add($"Found a working feed advertised by the page: {feed}");
                var preview = await PreviewAsync(http, feed, "rss", new Dictionary<string, string>(), new Dictionary<string, string>(), query, location, ct);
                return new Detection(feed, "rss", "high", feed, candidates, preview, notes);
            }

            // Try well-known feed paths on the same host.
            foreach (var path in CommonFeedPaths)
            {
                var candidate = new Uri(new Uri(uri.GetLeftPart(UriPartial.Authority)), path.TrimStart('/')).ToString();
                if (!candidates.Contains(candidate)) candidates.Add(candidate);
                var probe = await http.GetAsync(candidate, null, false, ct).ConfigureAwait(false);
                if (GuessContentType(probe.Body) != "feed") continue;
                notes.Add($"No feed link was advertised, but {candidate} returns a valid feed. This is almost certainly the site's job feed.");
                var preview = await PreviewAsync(http, candidate, "rss", new Dictionary<string, string>(), new Dictionary<string, string>(), query, location, ct);
                return new Detection(candidate, "rss", "medium", candidate, candidates, preview, notes);
            }

            notes.Add("This is an HTML page with no advertised feed. Provide CSS selectors for the listing to parse it, or save it as a 'link' source if the site disallows automated access.");
            var linkTitle = HtmlTitle().Match(body) is { Success: true } m ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : normalized;
            return new Detection(normalized, "html", "low", null, candidates,
                new[] { new SourcePreviewItem(linkTitle, "", "", normalized, "", "", 0) }, notes);
        }

        notes.Add("The response was empty or not HTML/XML/JSON. Save it as a 'link' source.");
        return new Detection(normalized, "link", "low", null, candidates, Array.Empty<SourcePreviewItem>(), notes);
    }

    /// <summary>Finds &lt;link rel="alternate" type="application/rss+xml"&gt; hrefs.</summary>
    public static List<string> FindFeedLinks(string html, Uri pageUri)
    {
        var found = new List<string>();
        foreach (Match tag in LinkTags().Matches(html))
        {
            var markup = tag.Value;
            string? rel = null;
            string? href = null;

            var relHref = RelThenHref().Match(markup);
            if (relHref.Success) { rel = relHref.Groups[1].Value; href = relHref.Groups[2].Value; }
            else
            {
                var hrefRel = HrefThenRel().Match(markup);
                if (hrefRel.Success) { href = hrefRel.Groups[1].Value; rel = hrefRel.Groups[2].Value; }
            }

            if (string.IsNullOrWhiteSpace(href)) continue;
            var relValue = (rel ?? string.Empty).ToLowerInvariant();
            var isFeedRel = relValue.Contains("alternate") || relValue.Contains("feed");
            var mentionsFeedType = markup.Contains("rss", StringComparison.OrdinalIgnoreCase) ||
                                   markup.Contains("atom", StringComparison.OrdinalIgnoreCase) ||
                                   markup.Contains("xml", StringComparison.OrdinalIgnoreCase);
            if (!isFeedRel && !mentionsFeedType) continue;

            var absolute = SourceParser.Absolute(href, pageUri.ToString());
            if (absolute.Length > 0 && !found.Contains(absolute)) found.Add(absolute);
        }
        return found;
    }

    public static string GuessContentType(string body)
    {
        var head = body.Length > 600 ? body[..600] : body;
        var trimmed = head.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return "feed";
        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal)) return "json";
        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            if (trimmed.Contains("<rss", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("<feed", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("<rdf:RDF", StringComparison.OrdinalIgnoreCase)) return "feed";
            return "html";
        }
        return "unknown";
    }

    private static readonly Dictionary<string, string> DefaultJsonMap = new()
    {
        ["title"] = "title",
        ["company"] = "company",
        ["location"] = "location",
        ["url"] = "url",
        ["description"] = "description",
        ["posted"] = "date",
        ["remote"] = "remote"
    };

    private static async Task<IReadOnlyList<SourcePreviewItem>> PreviewAsync(SourceHttp http, string url, string type,
        Dictionary<string, string> fieldMap, Dictionary<string, string> selectors, string? query, string? location, CancellationToken ct)
    {
        try
        {
            var probeSource = new SourceDto("", "", type, url, null, Array.Empty<string>(), false, 0, fieldMap, selectors, "", "", "");
            var result = await http.GetAsync(Compose(url, query, location), null, false, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.Body)) return Array.Empty<SourcePreviewItem>();
            var jobs = SourceParser.ParseAsync(probeSource, result.Body, url, ct);
            return jobs.Take(10)
                .Select(j => new SourcePreviewItem(j.Title, j.Company, j.Location, j.Url, j.PostedAt ?? "", Truncate(j.Description, 240), 0))
                .ToList();
        }
        catch
        {
            return Array.Empty<SourcePreviewItem>();
        }
    }

    public static string Compose(string urlOrTemplate, string? query, string? location) =>
        urlOrTemplate
            .Replace("{query}", Uri.EscapeDataString(query ?? string.Empty))
            .Replace("{location}", Uri.EscapeDataString(location ?? string.Empty));

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd() + "...";
}