using System.Text.RegularExpressions;

namespace ResuClean.Services;

/// <summary>
/// Cross-source dedupe. The same posting appears on several boards with different titles and URLs,
/// so matching is on a normalised title + company pair plus fuzzy token similarity.
/// </summary>
public static partial class Dedupe
{
    public sealed record Key(string NormalizedTitle, string NormalizedCompany);

    [GeneratedRegex(@"[^a-z0-9\s+#]", RegexOptions.None, 2000)]
    private static partial Regex NonWord();

    [GeneratedRegex(@"\b(senior|snr|jr|junior|lead|principal|head of|chief|director|manager|intern|internship|graduate|entry level|associate|staff)\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex SeniorityNoise();

    [GeneratedRegex(@"\s*\((?:[^)]*)\)\s*", RegexOptions.None, 2000)]
    private static partial Regex Parenthetical();

    [GeneratedRegex(@"[|/\-–—,:]+", RegexOptions.None, 2000)]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s+", RegexOptions.None, 2000)]
    private static partial Regex Whitespace();

    // Legal suffixes so "Acme Ltd" and "Acme Limited" collapse to the same company.
    [GeneratedRegex(@"\b(ltd|limited|inc|incorporated|llc|plc|gmbh|bv|pty|corp|corporation|co|company|group|holdings|intl|international)\b\.?", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex LegalSuffix();

    public static Key For(string title, string company) =>
        new(Normalize(title), Normalize(company));

    /// <summary>Strips seniority, bracketed location and punctuation so the same role from two boards collides.</summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lower = text.ToLowerInvariant();
        lower = Parenthetical().Replace(lower, " ");
        lower = Separators().Replace(lower, " ");
        lower = SeniorityNoise().Replace(lower, " ");
        lower = NonWord().Replace(lower, " ");
        lower = Whitespace().Replace(lower, " ");
        return lower.Trim();
    }

    /// <summary>
    /// True when one normalised title is a subset of the other with at most two extra tokens.
    /// This is what catches "Data Analyst" on one board and "Data Analyst - Acme" on another,
    /// which plain similarity scores too low to match.
    /// </summary>
    public static bool TitlesMatch(string a, string b)
    {
        var tokensA = Tokenize(a);
        var tokensB = Tokenize(b);
        if (tokensA.Count == 0 || tokensB.Count == 0) return false;

        var (shorter, longer) = tokensA.Count <= tokensB.Count ? (tokensA, tokensB) : (tokensB, tokensA);
        if (shorter.Count < 2) return false;
        if (!shorter.IsSubsetOf(longer)) return false;
        return longer.Count - shorter.Count <= 2;
    }

    /// <summary>Token-set similarity in 0..1, used as a secondary check for near-identical postings.</summary>
    public static double Similarity(string a, string b)
    {
        var tokensA = Tokenize(a);
        var tokensB = Tokenize(b);
        if (tokensA.Count == 0 || tokensB.Count == 0) return 0;
        return (double)tokensA.Intersect(tokensB).Count() / tokensA.Union(tokensB).Count();
    }

    private static HashSet<string> Tokenize(string text) =>
        new(Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    /// <summary>
    /// True when two postings are the same job. Exact normalised title+company always matches;
    /// otherwise the title must be highly similar and the company must match or one be blank.
    /// </summary>
    public static bool Same(string titleA, string companyA, string titleB, string companyB)
    {
        var ka = For(titleA, companyA);
        var kb = For(titleB, companyB);
        if (ka.NormalizedTitle.Length > 0 && ka.NormalizedTitle == kb.NormalizedTitle &&
            (ka.NormalizedCompany.Length == 0 || kb.NormalizedCompany.Length == 0 || ka.NormalizedCompany == kb.NormalizedCompany))
            return true;

        var titleSimilar = Similarity(titleA, titleB);
        var titlesMatch = titleSimilar >= 0.88 || TitlesMatch(titleA, titleB);
        if (!titlesMatch) return false;

        var companyA_ = NormalizeCompany(companyA);
        var companyB_ = NormalizeCompany(companyB);
        if (companyA_.Length == 0 || companyB_.Length == 0) return true;
        if (companyA_ == companyB_) return true;
        return Similarity(companyA, companyB) >= 0.85;
    }

    /// <summary>Company comparison key: like <see cref="Normalize"/> but with legal suffixes removed.</summary>
    public static string NormalizeCompany(string name)
    {
        var trimmed = LegalSuffix().Replace(Normalize(name), " ").Trim();
        return Whitespace().Replace(trimmed, " ").Trim();
    }

    /// <summary>
    /// Same URL (ignoring tracking parameters) is the strongest signal and is checked first.
    /// </summary>
    public static bool SameUrl(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return CanonicalUrl(a) == CanonicalUrl(b);
    }

    public static string CanonicalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url.Trim().ToLowerInvariant();
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        var query = builder.Query.TrimStart('?');
        if (query.Length > 0)
        {
            var kept = query.Split('&')
                .Where(pair => !pair.StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
                .Where(pair => !pair.StartsWith("gh_src", StringComparison.OrdinalIgnoreCase))
                .Where(pair => !pair.StartsWith("ref", StringComparison.OrdinalIgnoreCase))
                .Where(pair => !pair.StartsWith("fbclid", StringComparison.OrdinalIgnoreCase))
                .Where(pair => !pair.StartsWith("gclid", StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair, StringComparer.Ordinal);
            builder.Query = string.Join("&", kept);
        }
        var result = builder.Uri.ToString();
        return result.TrimEnd('/').ToLowerInvariant();
    }
}