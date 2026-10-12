using System.Text.RegularExpressions;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Rule-based ATS score. Every check returns pass/fail with a concrete tip, because the UI shows
/// the tip next to each failure. The non-keyword weights sum to exactly 100, so the score is the
/// weighted pass total out of 100. Keyword match is reported separately as a percentage because
/// it depends on the posting rather than on resume quality.
/// </summary>
public static partial class AtsScorer
{
    [GeneratedRegex(@"\b[\w.+-]+@[\w-]+\.[\w.-]{2,}\b", RegexOptions.None, 2000)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?:\+?\d[\s\-().]{0,3}){7,15}\d", RegexOptions.None, 2000)]
    private static partial Regex Phone();

    [GeneratedRegex(@"https?://(?:www\.)?(linkedin\.com|github\.com|behance\.net|portfolio|gitlab\.com)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ProfileLink();

    [GeneratedRegex(@"^[\s\u00A0]*[-*\u2022\u25AA\u25CF\u25E6\u2013\u2014\u2192\u2794>\u00BB]*[\s\u00A0]+", RegexOptions.None, 2000)]
    private static partial Regex BulletPrefix();

    // The trailing boundary has to be a negative lookahead rather than \b: '%' is a non-word
    // character, so "38%" followed by a space never has a boundary after it and never matched.
    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s*(?:%(?!\w)|percent\b)", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Percent();

    // A currency amount has to LOOK like one: either a currency symbol, or a magnitude suffix.
    // An earlier version made the suffix optional, so a bare 4-digit employment year matched here
    // and the highest-weighted check in the scorer passed on nothing but date ranges.
    [GeneratedRegex(@"[$€£¥₹]\s?\d[\d,.]*\b|\b\d[\d,.]*\s?(?:k|m|bn|million|billion|thousand)\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Money();

    // A count: a comma-grouped figure of 1,000 or more, or a number followed by a thing you can
    // count. Years and months are deliberately excluded: "6 years of experience" is a tenure
    // statement, not an achievement, and counting it is what inflated the score.
    private const string CountUnits =
        "users?|servers?|requests?|transactions?|tickets?|endpoints?|nodes?|clusters?|databases?|tables?|reports?|"
        + "records?|rows?|queries?|scripts?|pipelines?|services?|instances?|machines?|devices?|sites?|clients?|"
        + "customers?|projects?|teams?|members?|analysts?|engineers?|staff|people|hours?|days?|weeks?";

    [GeneratedRegex(@"\b\d{1,3}(?:,\d{3})+\b|\b\d+\s+(?:" + CountUnits + @")\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Volume();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Url();

    /// <summary>
    /// Spans that contain digits but prove nothing: date ranges, bare years, phone numbers, email
    /// addresses and URLs. These are blanked out before the evidence patterns run, which is what
    /// makes the three patterns above disjoint from each other and from employment history.
    /// </summary>
    private static readonly Regex[] NotEvidence =
    {
        Url(),
        ProfileLink(),
        DateRange(),
        Phone(),
        Email(),
        FourDigitYear()
    };

    [GeneratedRegex(@"(?i)\b(19|20)\d{2}\b\s*(?:-|–|—|to)\s*(?:\b(19|20)\d{2}\b|current|present|now)", RegexOptions.None, 2000)]
    private static partial Regex DateRange();

    [GeneratedRegex(@"\b(19|20)\d{2}\b", RegexOptions.None, 2000)]
    private static partial Regex FourDigitYear();

    [GeneratedRegex(@"\S {3,}\S {3,}\S", RegexOptions.None, 2000)]
    private static partial Regex ColumnBanding();

    [GeneratedRegex(@"^\d{1,2}[.)]\s+\S", RegexOptions.None, 2000)]
    private static partial Regex NumberedList();

    [GeneratedRegex(@"[^\p{L}]", RegexOptions.None, 2000)]
    private static partial Regex NonLetter();

    [GeneratedRegex(@"[\p{L}][\p{L}\p{M}'\.\-\+]{1,}", RegexOptions.None, 2000)]
    private static partial Regex Word();

    private static readonly (string Label, string[] Matchers)[] HeadingMatchers =
    {
        ("Summary or Objective", new[] { "summary", "profile", "objective", "about me" }),
        ("Experience", new[] { "experience", "work experience", "employment", "employment history", "career history", "professional experience" }),
        ("Education", new[] { "education", "academic", "qualifications", "academic background" }),
        ("Skills", new[] { "skills", "technical skills", "core competencies", "competencies", "expertise", "technologies", "toolkit" }),
        ("Projects", new[] { "projects", "portfolio", "selected projects" })
    };

    private static readonly string[] Certifications =
    {
        "certification", "certifications", "certified", "license", "licenses", "accreditation", "certificates"
    };

    private sealed record Line(string Raw, string Trimmed, bool IsBullet, bool IsHeading);

    public static AtsReport Score(string versionId, string text, string? jobDescription = null)
    {
        var checks = new List<AtsCheck>();
        var lines = SplitLines(text);

        // Weights: 16+14+10+10+18+9+6+6+5+4+2 = 100.
        checks.Add(ContactInfo(lines));
        checks.Add(Headings(lines));
        checks.Add(Length(text, lines));
        checks.Add(Bullets(lines));
        checks.Add(Quantified(text));
        checks.Add(Dates(lines));
        checks.Add(NoTables(text));
        checks.Add(NoColumns(lines));
        checks.Add(NoIcons(text));
        checks.Add(ActionVerbs(lines));
        checks.Add(SectionOrder(lines));

        KeywordMatch? keywords = null;
        if (!string.IsNullOrWhiteSpace(jobDescription))
        {
            keywords = Keywords.Compare(text, jobDescription);
            // Weight 0: reported for its tip, not counted in the 0-100 score.
            checks.Add(KeywordCoverage(keywords));
        }

        var score = Math.Clamp(checks.Where(c => c.Weight > 0).Sum(c => c.Passed ? c.Weight : 0), 0, 100);
        var verdict = score switch
        {
            >= 85 => "Strong",
            >= 70 => "Good",
            >= 55 => "Needs work",
            _ => "At risk"
        };

        return new AtsReport(
            versionId,
            score,
            verdict,
            false,
            null,
            checks,
            keywords,
            Array.Empty<string>(),
            Words(text).Count,
            lines.Count);
    }

    private static List<Line> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n')
            .Select(raw => new Line(raw, raw.Trim(), IsBulletLine(raw), IsHeadingLine(raw)))
            .ToList();

    private static bool IsBulletLine(string raw)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.Length == 0) return false;
        var first = trimmed[0];
        if (first is '-' or '*' or '\u2022' or '\u25AA' or '\u25CF' or '\u25E6' or '\u2192' or '\u2794' or '>' or '\u00BB' or '\u2013' or '\u2014')
            return trimmed.Length > 1 && (trimmed[1] == ' ' || trimmed[1] == '\t');
        return NumberedList().IsMatch(trimmed);
    }

    private static bool IsHeadingLine(string raw)
    {
        var t = raw.Trim();
        if (t.Length is 0 or > 60) return false;
        var letters = t.Where(char.IsLetter).ToArray();
        if (letters.Length < 3) return false;
        var upperRatio = letters.Count(char.IsUpper) / (double)letters.Length;
        return upperRatio > 0.75;
    }

    // ---- Individual checks. Each one owns its weight and its tip. -----------

    private static AtsCheck ContactInfo(List<Line> lines)
    {
        const int weight = 16;
        var text = string.Join("\n", lines.Select(l => l.Raw));
        var hasEmail = Email().IsMatch(text);
        var hasPhone = Phone().IsMatch(text);
        var hasLink = ProfileLink().IsMatch(text);
        var passed = hasEmail && hasPhone;

        var missing = new List<string>();
        if (!hasEmail) missing.Add("email address");
        if (!hasPhone) missing.Add("phone number");
        if (!hasLink) missing.Add("LinkedIn or portfolio link");

        var detail = passed
            ? "Email and phone number are present." + (hasLink ? " A profile link was found too." : "")
            : "Missing " + string.Join(", ", missing) + ".";
        var tip = passed
            ? (hasLink ? "Keep it as it is." : "Adding a LinkedIn or portfolio URL helps recruiters reach you.")
            : "Put email and phone on the first two lines. Some ATS discard a record that has no reachable contact details.";

        return new AtsCheck("contact", "Contact information", "content", passed, detail, tip, weight);
    }

    private static AtsCheck Headings(List<Line> lines)
    {
        const int weight = 14;
        var lowered = lines.Select(l => l.Trimmed.ToLowerInvariant()).Where(s => s.Length > 0).ToList();
        var found = new List<string>();
        var missing = new List<string>();

        // A heading counts when the word is there. Whether it has content underneath is a separate
        // question, and folding it in here would change what this check means: the existing
        // QuantifiedCheckTests fixture already relies on a bare heading not being treated as an
        // achievement, which is a different rule enforced elsewhere.
        foreach (var (label, matchers) in HeadingMatchers)
        {
            var hit = lowered.Any(s => matchers.Any(m =>
                s == m ||
                s.StartsWith(m + " ", StringComparison.Ordinal) ||
                s.EndsWith(" " + m, StringComparison.Ordinal)));
            if (hit) found.Add(label);
            else missing.Add(label);
        }

        var hasCert = lowered.Any(s => Certifications.Any(c => s.Contains(c)));
        var passed = missing.Count <= 1;
        var detail = passed
            ? $"Found {found.Count} of 5 standard headings" + (hasCert ? ", plus a certifications section." : ".")
            : "Missing heading" + (missing.Count > 1 ? "s: " : ": ") + string.Join(", ", missing) + ".";
        var tip = passed
            ? "Every required heading is present."
            : "Rename your headings to the exact words an ATS expects: Summary, Experience, Education, Skills. Keep each on its own line.";

        return new AtsCheck("headings", "Standard section headings", "structure", passed, detail, tip, weight);
    }

    private static AtsCheck Length(string text, List<Line> lines)
    {
        const int weight = 10;
        var words = Words(text).Count;
        var nonEmpty = lines.Count(l => l.Trimmed.Length > 0);
        var pageEstimate = Math.Max(1, (int)Math.Ceiling(nonEmpty / 52.0));
        var passed = words is >= 250 and <= 950;

        var detail = $"{words} words, about {pageEstimate} page(s), {nonEmpty} non-empty lines.";
        var tip = passed
            ? "Length sits in the range most parsers handle well."
            : words < 250
                ? "This is too short to be competitive. Add concrete achievements, even for an early-career role."
                : "Trim to one page if you have under 10 years of experience, two pages otherwise.";

        return new AtsCheck("length", "Length", "structure", passed, detail, tip, weight);
    }

    private static AtsCheck Bullets(List<Line> lines)
    {
        const int weight = 10;
        var bulletCount = lines.Count(l => l.IsBullet);
        var passed = bulletCount >= 5;
        var detail = $"{bulletCount} bullet lines found.";
        var tip = passed
            ? "Experience is formatted as scannable bullets."
            : "Rewrite experience lines as bullets starting with '- '. Paragraph blocks are the most common reason a parser loses your content.";
        return new AtsCheck("bullets", "Bulleted experience", "format", passed, detail, tip, weight);
    }

    /// <summary>
    /// Three disjoint kinds of evidence, matched over text with dates and contact details removed.
    /// Each distinct span counts once, so "38% across 1,200 transactions" is two results, not four.
    /// </summary>
    private static readonly (string Kind, Regex Pattern)[] EvidencePatterns =
    {
        ("percentages", Percent()),
        ("currency amounts", Money()),
        ("counts", Volume())
    };

    private static AtsCheck Quantified(string text)
    {
        const int weight = 18;

        // Strip dates, phone numbers, emails and URLs first. Without this the employment history
        // alone satisfies an 18-point check that is supposed to be measuring measurable impact.
        var masked = NotEvidence.Aggregate(text, (current, pattern) => pattern.Replace(current, " "));

        var spans = new List<(int Start, int End)>();
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (kind, pattern) in EvidencePatterns)
        {
            foreach (Match m in pattern.Matches(masked))
            {
                // Skip anything an earlier pattern already claimed, so one figure is never counted twice.
                if (spans.Any(s => m.Index < s.End && s.Start < m.Index + m.Length)) continue;
                spans.Add((m.Index, m.Index + m.Length));
                kinds[kind] = kinds.TryGetValue(kind, out var n) ? n + 1 : 1;
            }
        }

        var total = spans.Count;
        var passed = total >= 3;
        var breakdown = string.Join(", ", kinds.OrderByDescending(k => k.Value)
            .Select(k => $"{k.Value} {k.Key}"));

        var detail = passed
            ? $"{total} quantified results found: {breakdown}. Employment dates, phone numbers and links are not counted."
            : total == 0
                ? "No measurable results found. Dates and phone numbers are excluded, so only figures you can defend count."
                : $"{total} quantified results found: {breakdown}. Employment dates and contact details are not counted.";

        var tip = passed
            ? "You have measurable results, which is the strongest signal for both ATS scoring and human reviewers."
            : "Add numbers to at least 3 achievements: team size, revenue, percentage improvement, volume, budget, time saved. Only use figures you can defend.";

        return new AtsCheck("quantified", "Quantified results", "impact", passed, detail, tip, weight);
    }

    private static AtsCheck Dates(List<Line> lines)
    {
        const int weight = 9;
        var withRanges = lines.Count(l => DateRange().IsMatch(l.Raw));
        var withYear = lines.Count(l => FourDigitYear().IsMatch(l.Raw));
        var passed = withRanges >= 3 && withYear >= 5;

        var detail = $"{withRanges} date ranges like 'Mar 2021 - Present', {withYear} lines carrying a year.";
        var tip = passed
            ? "Employment dates use an unambiguous month-year format."
            : "Use 'Mar 2021 - Present' on every role. Ambiguous formats like '03/21' or 'Spring 21' are dropped by parsers.";
        return new AtsCheck("dates", "Employment dates", "content", passed, detail, tip, weight);
    }

    private static AtsCheck NoTables(string text)
    {
        const int weight = 6;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var pipeRows = lines.Count(l => l.Count(c => c == '|') >= 3);
        var tabLines = lines.Count(l => l.Contains('\t'));
        var passed = pipeRows <= 2 && tabLines <= 2;

        var detail = passed
            ? "No table-like structures detected."
            : $"{pipeRows} pipe-delimited rows and {tabLines} tab-indented lines can be read as table layouts.";
        var tip = passed
            ? "Layout is linear, which parsers handle reliably."
            : "Replace tables with one fact per line. Many ATS read two-column tables into unreadable fragments.";
        return new AtsCheck("no-tables", "No tables", "format", passed, detail, tip, weight);
    }

    private static AtsCheck NoColumns(List<Line> lines)
    {
        const int weight = 6;
        var suspicious = lines.Count(l => ColumnBanding().IsMatch(l.Raw));
        var passed = suspicious <= 3;

        var detail = passed
            ? "No multi-column banding detected."
            : $"{suspicious} lines look like they came from a two-column layout.";
        var tip = passed
            ? "Single-column reading order."
            : "Use a single column. Side-by-side columns get interleaved by parsers and scramble your dates.";
        return new AtsCheck("no-columns", "Single column", "format", passed, detail, tip, weight);
    }

    private static AtsCheck NoIcons(string text)
    {
        const int weight = 5;
        var count = text.Count(c => char.GetUnicodeCategory(c) switch
        {
            System.Globalization.UnicodeCategory.PrivateUse => true,
            System.Globalization.UnicodeCategory.OtherSymbol => true,
            _ => false
        });
        var passed = count == 0;

        var detail = passed
            ? "No icon or symbol glyphs found."
            : $"{count} icon or symbol glyphs found (phone, mail and location icons).";
        var tip = passed
            ? "Clean text with no icon artifacts."
            : "Delete contact icons. They arrive as private-use glyphs and can be dropped mid-word, producing garbled contact details.";
        return new AtsCheck("no-icons", "No icon glyphs", "content", passed, detail, tip, weight);
    }

    private static AtsCheck ActionVerbs(List<Line> lines)
    {
        const int weight = 4;
        var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "led", "managed", "built", "designed", "implemented", "developed", "delivered", "increased", "reduced",
            "improved", "launched", "created", "owned", "drove", "grew", "saved", "automated", "migrated", "architected",
            "negotiated", "coordinated", "analysed", "analyzed", "reported", "resolved", "supported", "maintained",
            "established", "spearheaded", "orchestrated", "standardised", "standardized", "optimised", "optimized",
            "streamlined", "revamped", "cut", "expanded", "trained", "mentored", "onboarded", "rebuilt", "consolidated"
        };

        var bullets = lines.Where(l => l.IsBullet).Select(l => l.Trimmed).ToList();
        var withVerb = bullets.Count(b =>
        {
            var cleaned = BulletPrefix().Replace(b, string.Empty);
            var first = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var word = NonLetter().Replace(first, string.Empty);
            return word.Length > 2 && verbs.Contains(word);
        });

        var passed = bullets.Count > 0 && withVerb >= Math.Max(3, bullets.Count / 4);
        var detail = $"{withVerb} of {bullets.Count} bullets start with a strong action verb.";
        var tip = passed
            ? "Bullets lead with action verbs."
            : "Start each bullet with a past-tense action verb: Led, Built, Reduced, Migrated. Avoid 'Responsible for' and 'Helped with'.";

        return new AtsCheck("action-verbs", "Action-verb bullets", "impact", passed, detail, tip, weight);
    }

    private static AtsCheck SectionOrder(List<Line> lines)
    {
        const int weight = 2;
        var order = new List<int>();
        foreach (var line in lines)
        {
            var t = line.Trimmed.ToLowerInvariant();
            if (t.StartsWith("summary") || t.StartsWith("profile") || t.StartsWith("objective")) order.Add(1);
            else if (t.StartsWith("experience") || t.StartsWith("work experience") || t.StartsWith("employment")) order.Add(2);
            else if (t.StartsWith("education")) order.Add(3);
            else if (t.StartsWith("skills") || t.StartsWith("technical skills")) order.Add(4);
        }

        var passed = order.Count >= 2 && order.SequenceEqual(order.OrderBy(x => x));
        var detail = passed
            ? "Sections appear in the conventional order."
            : order.Count < 2
                ? "Could not identify enough sections to judge the order."
                : "Sections are out of the conventional order (Summary, Experience, Education, Skills).";
        var tip = passed
            ? "Section order is conventional."
            : "Reorder sections so the standard sequence is unbroken: contact, summary, experience, skills, education.";

        return new AtsCheck("section-order", "Conventional section order", "structure", passed, detail, tip, weight);
    }

    private static AtsCheck KeywordCoverage(KeywordMatch keywords)
    {
        var passed = keywords.MatchPct >= 55;
        var detail = $"{keywords.MatchPct}% of the job description's keywords appear in your resume ({keywords.MatchedCount} matched, {keywords.MissingCount} missing).";
        var tip = passed
            ? "Keyword coverage is competitive for this posting."
            : "Work in the missing terms only where you genuinely have the experience. Start with: " +
              string.Join(", ", keywords.Missing.Take(6)) + ".";

        return new AtsCheck("keywords", "Job description keywords", "match", passed, detail, tip, 0);
    }

    public static List<string> Words(string text) =>
        Word().Matches(text).Select(m => m.Value).Where(w => w.Length > 1).ToList();
}