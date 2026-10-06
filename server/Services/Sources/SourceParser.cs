using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using ResuClean.Models;

namespace ResuClean.Services.Sources;

/// <summary>One job posting, normalised across every source type.</summary>
public sealed record RawJob(
    string Title,
    string Company,
    string Location,
    string Url,
    string? PostedAt,
    string Description,
    bool Remote);

/// <summary>
/// Turns a fetched document into raw jobs, based on the source's declared type and its
/// field mapping or CSS selectors. Nothing here is source-specific.
/// </summary>
public sealed partial class SourceParser
{
    private static readonly HtmlParser Parser = new();

    public static List<RawJob> ParseAsync(SourceDto source, string body, string baseUrl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return source.Type switch
        {
            "rss" => ParseFeed(body, baseUrl),
            "atom" => ParseFeed(body, baseUrl),
            "json" => ParseJson(source, body, baseUrl),
            "html" => ParseHtml(source, body, baseUrl).GetAwaiter().GetResult(),
            "link" => new List<RawJob>(),
            _ => new List<RawJob>()
        };
    }

    // ---- RSS / Atom --------------------------------------------------------

    public static List<RawJob> ParseFeed(string xml, string baseUrl)
    {
        var jobs = new List<RawJob>();
        if (string.IsNullOrWhiteSpace(xml)) return jobs;

        XDocument doc;
        try
        {
            // Feeds in the wild are frequently not well-formed; be forgiving.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true };
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, settings);
            doc = XDocument.Load(reader);
        }
        catch (Exception)
        {
            return ParseFeedLenient(xml, baseUrl);
        }

        XNamespace atom = "http://www.w3.org/2005/Atom";
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName is "item" or "entry"))
        {
            var title = Value(item, "title") ?? Value(item, "title", atom) ?? string.Empty;
            if (title.Length == 0) continue;

            var link = LinkOf(item, atom);
            var description = StripHtml(Value(item, "description") ?? Value(item, "summary") ?? Value(item, "content", atom) ?? string.Empty);
            var posted = ParseDate(Value(item, "pubDate") ?? Value(item, "published") ?? Value(item, "updated") ?? Value(item, "date", atom));

            var company = GuessCompany(description, title);
            var location = GuessLocation(description, company, title);

            jobs.Add(new RawJob(
                Clean(title), company, location, Absolute(link, baseUrl), posted, description, IsRemoteText(title + " " + location + " " + description)));
        }

        return jobs;
    }

    /// <summary>Regex fallback for feeds that are not valid XML (unescaped ampersands, stray tags).</summary>
    private static List<RawJob> ParseFeedLenient(string xml, string baseUrl)
    {
        var jobs = new List<RawJob>();
        foreach (Match block in Regex.Matches(xml, @"<(item|entry)\b[\s\S]*?</\1>", RegexOptions.IgnoreCase))
        {
            var inner = block.Value;
            var title = TagText(inner, "title");
            if (title.Length == 0) continue;
            var link = TagText(inner, "link");
            if (link.Length == 0)
            {
                var href = Regex.Match(inner, @"<link[^>]*href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (href.Success) link = href.Groups[1].Value;
            }
            var description = StripHtml(TagText(inner, "description") is { Length: > 0 } d ? d : TagText(inner, "summary") is { Length: > 0 } s ? s : TagText(inner, "content"));
            var posted = ParseDate(TagText(inner, "pubDate") is { Length: > 0 } p ? p : TagText(inner, "published"));
            var company = GuessCompany(description, title);
            jobs.Add(new RawJob(Clean(title), company, GuessLocation(description, company, title), Absolute(link, baseUrl), posted, description,
                IsRemoteText(title + " " + description)));
        }
        return jobs;
    }

    private static string TagText(string xml, string tag)
    {
        var m = Regex.Match(xml, $@"<{tag}\b[^>]*>([\s\S]*?)</{tag}>", RegexOptions.IgnoreCase);
        if (!m.Success) return string.Empty;
        return System.Net.WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, @"<[^>]+>", " ")).Trim();
    }

    private static string? Value(XElement element, string name, XNamespace? ns = null)
    {
        var match = ns is null
            ? element.Elements().FirstOrDefault(e => e.Name.LocalName == name)
            : element.Element(ns + name);
        var value = match?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string LinkOf(XElement item, XNamespace atom)
    {
        var plain = item.Elements().FirstOrDefault(e => e.Name.LocalName == "link")?.Value;
        if (!string.IsNullOrWhiteSpace(plain)) return plain.Trim();
        var atomLink = item.Elements(atom + "link").FirstOrDefault()?.Attribute("href")?.Value;
        return atomLink ?? string.Empty;
    }

    // ---- JSON with field mapping ------------------------------------------

/// <summary>
    /// Reads a dotted JSON path. The root may be an array, or an object whose array lives at
    /// fieldMap["_root"] (default "results"), which is what most job APIs look like.
    /// </summary>
    public static List<RawJob> ParseJson(SourceDto source, string body, string baseUrl)
    {
        var jobs = new List<RawJob>();
        if (string.IsNullOrWhiteSpace(body)) return jobs;

        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var map = source.FieldMap ?? new Dictionary<string, string>();

        JsonElement array;
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            var path = map.GetValueOrDefault("_root", "results");
            var found = root.TryGetProperty(path, out var candidate) ? candidate : default;
            if (found.ValueKind != JsonValueKind.Array) return jobs;
            array = found;
        }
        else
        {
            return jobs;
        }

        foreach (var element in array.EnumerateArray())
        {
            var title = ReadPath(element, map.GetValueOrDefault("title", "title"));
            if (string.IsNullOrWhiteSpace(title)) continue;

            var company = ReadPath(element, map.GetValueOrDefault("company", "company"));
            var location = ReadPath(element, map.GetValueOrDefault("location", "location"));
            var url = ReadPath(element, map.GetValueOrDefault("url", "url"));
            var description = StripHtml(ReadPath(element, map.GetValueOrDefault("description", "description")));
            var postedRaw = ReadPath(element, map.GetValueOrDefault("posted", "date"));
            var posted = ParseDate(postedRaw);
            var remoteField = ReadPath(element, map.GetValueOrDefault("remote", "remote"));
            var remote = ParseBool(remoteField) || IsRemoteText(title + " " + location + " " + description);

            if (string.IsNullOrWhiteSpace(company)) company = GuessCompany(description, title);
            if (string.IsNullOrWhiteSpace(location)) location = GuessLocation(description, company, title);

            jobs.Add(new RawJob(Clean(title), company, location, Absolute(url, baseUrl), posted, description, remote));
        }

        return jobs;
    }

    /// <summary>Reads a dotted JSON path like "result.data.title"; falls back to case-insensitive lookup.</summary>
    public static string ReadPath(JsonElement element, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var current = element;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object) return string.Empty;
            if (!current.TryGetProperty(segment, out var next))
            {
                var found = false;
                foreach (var property in current.EnumerateObject())
                {
                    if (!string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase)) continue;
                    next = property.Value;
                    found = true;
                    break;
                }
                if (!found) return string.Empty;
                current = next;
            }
            else
            {
                current = next;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? string.Empty,
            JsonValueKind.Number => current.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    // ---- HTML with CSS selectors ------------------------------------------

    public static async Task<List<RawJob>> ParseHtml(SourceDto source, string html, string baseUrl)
    {
        var jobs = new List<RawJob>();
        if (string.IsNullOrWhiteSpace(html)) return jobs;

        var doc = await Parser.ParseDocumentAsync(html).ConfigureAwait(false);
        var selectors = source.Selectors ?? new Dictionary<string, string>();
        var container = selectors.GetValueOrDefault("item", "a[href]");
        var nodes = doc.QuerySelectorAll(container).ToList();
        if (nodes.Count == 0) nodes = doc.QuerySelectorAll("a[href]").ToList();

        foreach (var node in nodes)
        {
            var element = node as IHtmlElement;
            var title = PickText(element, selectors.GetValueOrDefault("title", ""), node);
            if (string.IsNullOrWhiteSpace(title)) title = Clean(element?.TextContent ?? "");
            if (title.Length is < 3 or > 200) continue;

            var href = element?.GetAttribute("href") ?? string.Empty;
            // The container is usually a card, not the anchor itself: fall back to the first link inside it,
            // and then to the link inside the title element when one was configured.
            if (href.Length == 0 && element is not null)
            {
                var titleSelector = selectors.GetValueOrDefault("title", "");
                var titleNode = titleSelector.Length > 0 ? element.QuerySelector(titleSelector) : null;
                href = titleNode is IHtmlElement titleAnchor ? titleAnchor.GetAttribute("href") ?? string.Empty : string.Empty;
                if (href.Length == 0) href = element.QuerySelector("a[href]")?.GetAttribute("href") ?? string.Empty;
            }

            var url = Absolute(href, baseUrl);
            var company = PickText(element, selectors.GetValueOrDefault("company", ""), node);
            var location = PickText(element, selectors.GetValueOrDefault("location", ""), node);
            var description = StripHtml(PickText(element, selectors.GetValueOrDefault("description", ""), node));
            var postedRaw = PickText(element, selectors.GetValueOrDefault("posted", ""), node);
            var posted = ParseDate(postedRaw);
            var remoteText = PickText(element, selectors.GetValueOrDefault("remote", ""), node);

            if (string.IsNullOrWhiteSpace(company)) company = GuessCompany(description, title);
            if (string.IsNullOrWhiteSpace(location)) location = GuessLocation(description, company, title);
            var remote = ParseBool(remoteText) || IsRemoteText(title + " " + location + " " + description);

            jobs.Add(new RawJob(Clean(title), company, location, url, posted, description, remote));
        }

        return jobs;
    }

    private static string PickText(IHtmlElement? scope, string selector, IElement fallback)
    {
        if (string.IsNullOrWhiteSpace(selector) || scope is null) return string.Empty;
        var found = scope.QuerySelector(selector);
        return found is null ? string.Empty : Clean(found.TextContent);
    }

    // ---- Normalisation helpers --------------------------------------------

    public static string Clean(string text) =>
        Regex.Replace(WebUtility_HtmlDecode(text), @"\s+", " ").Trim();

    private static string WebUtility_HtmlDecode(string text) => System.Net.WebUtility.HtmlDecode(text);

    public static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        var withoutBlocks = Regex.Replace(html, @"<br\s*/?>|</p>|</div>|</li>", "\n", RegexOptions.IgnoreCase);
        var text = Regex.Replace(withoutBlocks, @"<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(decoded, @"[ \t]{2,}", " ").Trim();
    }

    public static string Absolute(string url, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        url = url.Trim();
        if (url.StartsWith("//")) url = "https:" + url;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) return url;
        return Uri.TryCreate(baseUri, url, out var combined) ? combined.ToString() : url;
    }

    [GeneratedRegex(@"(?i)\b(remote|work from home|wfh|anywhere|distributed|virtual)\b", RegexOptions.None, 2000)]
    private static partial Regex RemotePattern();

    [GeneratedRegex(@"(?i)\b(?:location|based in|located in)\s*[:\-]?\s*([^\n\.]{2,60})", RegexOptions.None, 2000)]
    private static partial Regex LocationLine();

    [GeneratedRegex(@"(?i)\b(?:company|employer|organization|organisation|client)\s*[:\-]\s*([^\n\.]{2,60})", RegexOptions.None, 2000)]
    private static partial Regex CompanyLine();

    public static bool IsRemoteText(string text) => !string.IsNullOrWhiteSpace(text) && RemotePattern().IsMatch(text);

    public static string GuessCompany(string description, string title)
    {
        var match = CompanyLine().Match(description);
        if (match.Success) return Clean(match.Groups[1].Value);
        // "Senior Data Analyst - Acme Ltd" style titles.
        var byDash = Regex.Match(title, @"\s[–—-]\s(?<c>[A-Z][\w&.'\- ]{2,40})$");
        if (byDash.Success) return Clean(byDash.Groups["c"].Value);
        return string.Empty;
    }

    public static string GuessLocation(string description, string company, string title)
    {
        var match = LocationLine().Match(description);
        if (match.Success) return Clean(match.Groups[1].Value);
        foreach (var candidate in new[] { description, title })
        {
            var city = Regex.Match(candidate, @"\b(Nairobi|Mombasa|Kisumu|Nakuru|Accra|Lagos|Nairobi|Kampala|Dar es Salaam|Addis Ababa|Johannesburg|Cape Town|Pretoria|London|Manchester|Dublin|Remote|Worldwide)\b", RegexOptions.IgnoreCase);
            if (city.Success) return city.Value;
        }
        return string.Empty;
    }

    [GeneratedRegex(@"(?:19|20)\d{2}-\d{2}-\d{2}(?:T[\d:.+Z-]+)?", RegexOptions.None, 2000)]
    private static partial Regex IsoDate();

    public static string? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();

        var iso = IsoDate().Match(text);
        if (iso.Success && DateTime.TryParse(iso.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var isoDate))
            return isoDate.ToString("yyyy-MM-dd");

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToString("yyyy-MM-dd");

        var epoch = Regex.Match(text, @"(?<!\d)(?<epoch>1[0-9]{9})(?!\d)");
        if (epoch.Success && long.TryParse(epoch.Groups["epoch"].Value, out var unix))
            return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd");

        return null;
    }

    public static bool ParseBool(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("1", StringComparison.Ordinal) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("remote", StringComparison.OrdinalIgnoreCase));
}