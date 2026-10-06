using System.Text;
using System.Text.RegularExpressions;

namespace ResuClean.Services;

/// <summary>
/// Deterministic resume cleaner. Runs before any model is involved so model input is
/// already tidy, and so the tool is useful with no model configured at all.
/// Never adds, invents or reorders facts.
/// </summary>
public static partial class Cleaner
{
    // PUA / dingbats / pictographs commonly pasted from LinkedIn, Word and job boards.
    [GeneratedRegex(@"[\uE000-\uF8FF\u2600-\u27BF\uFE0F\u200D\u2060]", RegexOptions.None, 2000)]
    private static partial Regex PrivateUseAndIcons();

    // Zero-width and soft hyphen noise.
    [GeneratedRegex(@"[\u00AD\u200B\u200C\u200D\uFEFF]", RegexOptions.None, 2000)]
    private static partial Regex InvisibleChars();

    [GeneratedRegex(@"^\s*[\u2022\u25AA\u25CF\u25E6\u2043\u2219\u00B7\u2027\uFEFF\-*\u2013]\s*", RegexOptions.Multiline, 2000)]
    private static partial Regex LeadingBullet();

    [GeneratedRegex(@"[\u2022\u2043\u2219\u25AA\u25CF\u25E6\u2027\u00B7]{2,}", RegexOptions.None, 2000)]
    private static partial Regex RepeatedBullets();

    [GeneratedRegex(@"\s*\|\s*", RegexOptions.None, 2000)]
    private static partial Regex PipeSpacer();

    [GeneratedRegex(@"(\r?\n){3,}", RegexOptions.None, 2000)]
    private static partial Regex ExcessBlankLines();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.None, 2000)]
    private static partial Regex MultiSpace();

    [GeneratedRegex(@"[ \t]+\r?\n", RegexOptions.None, 2000)]
    private static partial Regex TrailingSpace();

    [GeneratedRegex(@"(?m)^[ \t]+", RegexOptions.None, 2000)]
    private static partial Regex Indent();

    [GeneratedRegex(@"(?<!\d),(?=\d{3}\b)", RegexOptions.None, 2000)]
    private static partial Regex GroupSeparator();

    [GeneratedRegex(@"(?m)^(\p{Lu}[\p{Ll}\p{M}'\-]+(?:\s+\p{Lu}[\p{Ll}\p{M}'\-]+)*)$", RegexOptions.None, 2000)]
    private static partial Regex TitleCaseLine();

    [GeneratedRegex(@"[ \t]+$", RegexOptions.Multiline, 2000)]
    private static partial Regex LineTrailingWs();

    public sealed record Result(string Text, IReadOnlyList<Change> Changes, IReadOnlyList<string> Warnings);

    public sealed record Change(string Kind, string Detail, int Count);

    public static Result Run(string input)
    {
        var changes = new List<Change>();
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(input))
            return new Result(string.Empty, changes, new List<string> { "Nothing to clean: the resume text is empty." });

        var text = input;
        var original = text;

        // 1. Unicode normalisation: NFC so accented characters are consistent.
        var before = text;
        text = text.Normalize(NormalizationForm.FormC);
        Count(changes, "unicode", "Normalised to Unicode NFC", text, before);

        // 2. Line endings -> LF only.
        before = text;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        Count(changes, "line-endings", "Converted CRLF/CR line endings to LF", text, before);

        // 3. Strip invisible characters and icon glyphs.
        before = text;
        text = InvisibleChars().Replace(text, string.Empty);
        Count(changes, "invisible", "Removed zero-width and soft-hyphen characters", text, before);

        before = text;
        text = PrivateUseAndIcons().Replace(text, string.Empty);
        Count(changes, "icons", "Removed icon and dingbat glyphs (LinkedIn/Word icons, variation selectors)", text, before);

        // 4. Normalise bullet characters on their own line and in "Title | Company" lines.
        before = text;
        text = LeadingBullet().Replace(text, "- ");
        Count(changes, "bullets", "Normalised leading bullets to a single '- '", text, before);

        before = text;
        text = RepeatedBullets().Replace(text, "•");
        Count(changes, "bullets", "Collapsed repeated bullet runs", text, before);

        // 5. Tidy separators: "A |  B" -> "A | B".
        before = text;
        text = PipeSpacer().Replace(text, " | ");
        Count(changes, "separators", "Normalised spacing around '|' separators", text, before);

        // 6. Collapse runs of dots used as fill ("...........") and stray underscores.
        before = text;
        text = Regex.Replace(text, @"[.\u2026]{4,}", "...");
        text = Regex.Replace(text, "_{3,}", "-");
        Count(changes, "fill", "Replaced dot and underscore fill characters", text, before);

        // 7. Collapse whitespace.
        before = text;
        text = MultiSpace().Replace(text, " ");
        text = TrailingSpace().Replace(text, "\n");
        text = Indent().Replace(text, string.Empty);
        text = LineTrailingWs().Replace(text, string.Empty);
        text = text.Trim();
        Count(changes, "whitespace", "Collapsed repeated spaces and stripped line indentation", text, before);

        // 8. At most one blank line between blocks.
        before = text;
        text = ExcessBlankLines().Replace(text, "\n\n");
        Count(changes, "blank-lines", "Limited consecutive blank lines to one", text, before);

        // 9. Thousands separators: "1,000" stays, "1 ,000" and "1, 000" become "1,000".
        before = text;
        text = Regex.Replace(text, @"(?<=\d)\s*,\s*(?=\d{3}\b)", ",");
        Count(changes, "numbers", "Tightened thousands separators in metrics", text, before);

        // 7. Dates: "January, 2020" -> "January 2020", "Mar 2021, - Present" -> "Mar 2021 - Present".
        before = text;
        text = Regex.Replace(text, @"(?i)\b(Jan(uary)?|Feb(ruary)?|Mar(ch)?|Apr(il)?|May|Jun(e)?|Jul(y)?|Aug(ust)?|Sep(tember)?|Oct(ober)?|Nov(ember)?|Dec(ember)?),\s+(?=\d{4})", "$1 ");
        text = Regex.Replace(text, @"(?<!\d)(\d{4})\s*,\s*(?=[^\d\s])", "$1 ");
        Count(changes, "dates", "Removed stray commas inside month-year dates", text, before);

        // 11. Trailing punctuation on section headings ("EXPERIENCE." -> "EXPERIENCE").
        before = text;
        text = Regex.Replace(text, @"(?m)^([A-Z][A-Z /&]{2,40})[.:;]+$", "$1");
        Count(changes, "headings", "Stripped trailing punctuation from ALL-CAPS section headings", text, before);

        // 12. Collapse accidental repeated words ("the the" -> "the"). Anchored so only
        // genuine repeats of the same word are collapsed.
        before = text;
        text = Regex.Replace(text, @"\b(\p{L}+)(?:\s+\1\b)+", "$1");
        Count(changes, "typos", "Removed repeated words", text, before);

        // Final safety pass: exactly two trailing newlines max.
        text = text.TrimEnd() + "\n";

        if (changes.Count == 0)
            changes.Add(new Change("none", "No changes were needed", 0));

        if (ContainsIconHint().IsMatch(text))
            warnings.Add("Some unusual glyphs survived cleaning. Review the highlighted characters manually.");

        return new Result(text, changes, warnings);
    }

    private static void Count(List<Change> changes, string kind, string detail, string after, string before)
    {
        if (string.Equals(after, before, StringComparison.Ordinal)) return;
        var count = Math.Max(1, Math.Abs(before.Length - after.Length));
        changes.Add(new Change(kind, detail, count));
    }

    [GeneratedRegex(@"[\uE000-\uF8FF\u2600-\u27BF\uFE0F]", RegexOptions.None, 2000)]
    private static partial Regex ContainsIconHint();

    /// <summary>
    /// Line-level guard used by every model prompt and by output validation: the model may only
    /// return text whose factual tokens appear in its input. Used to detect invented content.
    /// </summary>
    public static IReadOnlyList<string> FindUngroundedTerms(string input, string output, IReadOnlySet<string> allowedExtra)
    {
        var source = Tokenize(input);
        var suspicious = new List<string>();
        foreach (var token in Tokenize(output).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (allowedExtra.Contains(token)) continue;
            if (token.Length < 4) continue;
            if (!source.Contains(token) && !IsCommonResumeWord(token)) suspicious.Add(token);
        }
        return suspicious;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in WordRegex().Matches(text)) set.Add(m.Value);
        return set;
    }

    [GeneratedRegex(@"[\p{L}][\p{L}\p{M}'\.\-\+]{2,}", RegexOptions.None, 2000)]
    private static partial Regex WordRegex();

    private static readonly HashSet<string> CommonResumeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "the", "with", "for", "from", "that", "this", "role", "team", "teams", "work", "working", "years", "year",
        "across", "using", "used", "using", "build", "building", "built", "manage", "managing", "managed", "support",
        "supporting", "supported", "develop", "developing", "developed", "deliver", "delivering", "delivered",
        "experience", "skills", "skill", "requirements", "required", "responsibilities", "strong", "excellent",
        "ability", "including", "include", "includes", "such", "other", "into", "ensure", "ensuring", "help",
        "helping", "within", "while", "about", "also", "than", "then", "over", "under", "between", "through",
        "employer", "employers", "employment", "hiring", "hire", "candidate", "candidates", "resume", "summary",
        "objective", "profile", "contact", "email", "phone", "location", "address", "links", "linkedin",
        "github", "portfolio", "http", "https", "www", "com", "org", "net", "ref", "refs", "reference", "references",
        "present", "current", "previous", "junior", "senior", "lead", "manager", "director", "officer", "officer",
        "engineer", "engineering", "developer", "development", "analyst", "analysis", "specialist", "assistant",
        "coordinator", "administrator", "executive", "intern", "consultant", "specialist", "technician", "officer",
        "improved", "increase", "increased", "reduce", "reduced", "save", "saved", "saving", "growth", "reach",
        "revenue", "profit", "profitability", "budget", "budgets", "cost", "costs", "client", "clients", "customer",
        "customers", "stakeholder", "stakeholders", "process", "processes", "system", "systems", "tool", "tools",
        "project", "projects", "data", "reports", "reporting", "targets", "goals", "goal", "kpi", "kpis", "volume",
        "stakeholder", "senior", "stakeholders", "operation", "operations", "product", "products", "service",
        "services", "quality", "risk", "risks", "compliance", "audit", "audits", "training", "manageable",
        "manage", "significant", "successful", "success", "successfully", "proven", "record", "records", "identify",
        "identified", "improve", "improvement", "improvements", "improve", "learn", "learned", "learning", "know",
        "knowledge", "understanding", "familiar", "experience", "skilled", "skillful", "effective", "efficient",
        "efficiency", "productive", "productivity", "motivated", "driven", "dynamic", "proven", "results", "results",
        "oriented", "focused", "focus", "focusing", "attention", "detail", "details", "care", "quality", "standards",
        "standard", "practices", "practice", "methods", "methodology", "requirements", "requirement", "suitable",
        "suitability", "ability", "abilities", "excellent", "good", "great", "high", "low", "best", "better", "new",
        "existing", "current", "various", "different", "multiple", "several", "various", "general", "specific",
        "relevant", "professional", "business", "company", "organization", "organisation", "industry", "sector",
        "market", "sales", "marketing", "financial", "finance", "accounting", "legal", "compliance", "security",
        "network", "server", "servers", "web", "mobile", "cloud", "api", "apis", "app", "apps", "software",
        "hardware", "computer", "computers", "internet", "online", "digital", "platform", "platforms", "solution",
        "solutions", "infrastructure", "integration", "integrations", "automation", "automated", "manual",
        "written", "verbal", "communication", "communications", "interpersonal", "teamwork", "collaboration",
        "collaborative", "adaptable", "flexible", "self-starter", "self-starter", "motivated", "ownership", "owner",
        "accountable", "accountability", "deadline", "deadlines", "priority", "priorities", "fast-paced", "fast",
        "paced", "busy", "fast-paced", "environment", "environments", "culture", "values", "diversity",
        "inclusion", "inclusive", "equal", "opportunity", "employer", "responsibility", "responsibilities"
    };

    public static bool IsCommonResumeWord(string token) => CommonResumeWords.Contains(token);
}