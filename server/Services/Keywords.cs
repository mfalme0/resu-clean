using System.Text.RegularExpressions;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Keyword extraction and matching between a job description and a resume.
/// Also used to rank job postings against a chosen resume version.
/// </summary>
public static partial class Keywords
{
    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'\-\+\.#/]{1,24}", RegexOptions.None, 2000)]
    private static partial Regex TokenPattern();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","with","you","your","our","their","this","that","these","those","will","have","has","had",
        "are","was","were","been","being","not","but","can","could","should","would","may","might","must","from",
        "into","than","then","them","they","his","her","its","our","out","off","over","under","about","above","after",
        "again","all","also","any","because","before","between","both","during","each","few","more","most","other",
        "some","such","only","own","same","very","just","who","what","when","where","why","how","which","while",
        "here","there","hereby","etc","via","per","able","well","also","work","working","works","role","roles",
        "job","jobs","position","positions","candidate","candidates","team","teams","company","companies",
        "including","include","includes","such","other","others","new","years","year","time","times","day","days",
        "week","weeks","month","months","strong","good","great","high","level","plus","must","requirements",
        "required","requirement","qualifications","responsibilities","responsibility","about","please","apply",
        "email","send","resume","cv","benefits","salary","equal","employer","opportunity","us","we","are","who"
    };

    /// <summary>Skills dictionary. Multi-word entries are matched before single tokens.</summary>
    private static readonly string[] SkillDictionary =
    {
        "machine learning","deep learning","artificial intelligence","data engineering","data analysis","data science",
        "power bi","tableau","looker","excel","power query","power pivot","sql server","postgresql","mysql","mongodb",
        "nosql","spark","hadoop","kafka","airflow","dbt","snowflake","bigquery","redshift","azure","aws","gcp",
        "google cloud","docker","kubernetes","terraform","ansible","jenkins","ci/cd","github actions","git","linux",
        "unix","bash","python","java","c#",".net","dotnet","javascript","typescript","node.js","node","react","vue",
        "angular","svelte","sveltekit","html","css","sass","tailwind","rest","rest api","graphql","apis","microservices",
        "etl","ssis","sap","erp","crm","salesforce","hubspot","seo","sem","google analytics","ga4","hubspot",
        "figma","sketch","adobe","photoshop","illustrator","canva","wireframes","prototyping","user research",
        "ux","ui","ui/ux","accessibility","wcag","design systems","graphic design","copywriting","content strategy",
        "project management","agile","scrum","kanban","jira","confluence","stakeholder management","budgeting",
        "financial modeling","forecasting","accounting","bookkeeping","quickbooks","xero","payroll","procurement",
        "supply chain","logistics","inventory","operations","quality assurance","qa","testing","cybersecurity",
        "networking","firewall","devops","site reliability","monitoring","observability","documentation",
        "training","mentoring","coaching","onboarding","recruitment","talent acquisition","performance management",
        "laboratory","clinical","phlebotomy","patient care","nursing","diagnostics","fmcg","manufacturing",
        "mechanical","electrical","engineering","cad","autocad","plc","maintenance","hvac","wiring",
        "government","public sector","procurement","tender","donor","grants","sampling","monitoring",
        "kotlin","swift","flutter","dart","laravel","django","flask","symfony","codeigniter","r","matlab",
        "statistics","regression","classification","nlp","computer vision","pandas","numpy","scikit-learn","pytorch",
        "tensorflow","keras","llm","prompt engineering"
    };

    /// <summary>Extracts ranked keywords from free text.</summary>
    public static List<string> Extract(string text, int max = 40)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        var lower = text.ToLowerInvariant();
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Multi-word skills first: they win over their component words.
        foreach (var skill in SkillDictionary)
        {
            if (lower.Contains(skill, StringComparison.Ordinal))
                found[skill] = 1000 + CountOccurrences(lower, skill);
        }

        foreach (Match m in TokenPattern().Matches(lower))
        {
            var token = m.Value.Trim('.', '#', '/');
            if (token.Length < 3 || StopWords.Contains(token)) continue;
            if (token.Length > 24) continue;
            found[token] = found.TryGetValue(token, out var existing) ? existing + 1 : 1;
        }

        return found
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(max)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>Compares resume text against a job description.</summary>
    public static KeywordMatch Compare(string resumeText, string jobDescription)
    {
        var wanted = Extract(jobDescription, 60);
        var have = Extract(resumeText, 400).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resumeLower = resumeText.ToLowerInvariant();

        var matched = new List<string>();
        var missing = new List<string>();
        foreach (var keyword in wanted)
        {
            if (have.Contains(keyword) || resumeLower.Contains(keyword, StringComparison.Ordinal))
                matched.Add(keyword);
            else
                missing.Add(keyword);
        }

        var matchedSet = matched.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = Extract(resumeText, 200)
            .Where(k => !matchedSet.Contains(k))
            .Take(15)
            .ToList();

        var total = matched.Count + missing.Count;
        var pct = total == 0 ? 0 : (int)Math.Round(matched.Count * 100.0 / total);
        return new KeywordMatch(pct, matched.Count, missing.Count, matched, missing, extra);
    }

    /// <summary>Overlap score between a posting and a resume, 0-100. Cheap symmetric token overlap.</summary>
    public static int ScoreAgainst(string postingText, string resumeText)
    {
        var postingKeywords = Extract(postingText, 40);
        if (postingKeywords.Count == 0 || string.IsNullOrWhiteSpace(resumeText)) return 0;
        var resumeLower = resumeText.ToLowerInvariant();
        var hits = postingKeywords.Count(k => resumeLower.Contains(k, StringComparison.Ordinal));
        return (int)Math.Round(hits * 100.0 / postingKeywords.Count);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}