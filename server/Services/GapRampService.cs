using System.Text.Json;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Suggests how to close a named gap, grounded in something you already do.
///
/// "What should I learn" is the easiest question in the world to answer with confident fiction, so
/// this service is built around a hard rule: every suggestion must name a verified fact it builds
/// on. A model reply that cannot cite one is dropped, not softened. The deterministic path makes no
/// suggestions at all — it reports the nearest thing you already have and stops there, because
/// "you have KVM via Proxmox, which is the same mental model as ESXi" is defensible and
/// "take this course" is not.
/// </summary>
public sealed partial class GapRampService
{
    public const string Workflow = "gap_ramp";

    private readonly ModelRouter _router;
    private readonly ProfileService _profile;

    public GapRampService(ModelRouter router, ProfileService profile)
    {
        _router = router;
        _profile = profile;
    }

    public sealed record Suggestion(
        string Text,
        /// <summary>The verified fact this builds on. Never empty: an uncited suggestion is discarded.</summary>
        string BuildsOnFactId,
        string BuildsOnFactText,
        string Why);

    public sealed record Report(
        string Requirement,
        string Class,
        IReadOnlyList<string> Missing,
        IReadOnlyList<Suggestion> Suggestions,
        /// <summary>The closest verified fact to the gap, whether or not a model was available.</summary>
        string? NearestFact,
        bool UsedModel,
        string Method);

    /// <summary>
    /// The gap has to be closable. A years requirement is reported as not closable rather than
    /// given a ramp, because there is no ramp and pretending otherwise wastes an afternoon.
    /// </summary>
    public bool IsClosable(string requirementClass, IReadOnlyList<string> missing) =>
        requirementClass != RequirementsService.Blocker || missing.All(m => !m.Any(char.IsDigit));

    public async Task<Report> SuggestAsync(
        string requirement,
        string requirementClass,
        IReadOnlyList<string> missing,
        CancellationToken ct = default)
    {
        var nearest = NearestFact(requirement);
        var method = "deterministic";
        var suggestions = new List<Suggestion>();
        var usedModel = false;

        if (_router.AnyConfigured(Workflow))
        {
            var run = await ModelSuggestAsync(requirement, missing, ct).ConfigureAwait(false);
            if (run.Count > 0)
            {
                suggestions = run;
                usedModel = true;
                method = "model";
            }
        }

        return new Report(requirement, requirementClass, missing, suggestions, nearest, usedModel, method);
    }

    /// <summary>
    /// The verified fact closest to the gap, or null when nothing is close enough to name.
    ///
    /// Two ways to count as close. Literal word overlap catches "you already use Terraform" against
    /// a Terraform requirement. Concept overlap catches the transfer case that matters most here:
    /// "you run KVM through Proxmox" against a VMware ESXi requirement, where no word is shared but
    /// the ground is the same. Without the second one the useful answer is thrown away, because
    /// adjacent tools rarely share vocabulary. The concept groups are small and named explicitly
    /// rather than inferred, so this stays a lookup and not a guess.
    /// </summary>
    public string? NearestFact(string requirement)
    {
        var requirementTokens = Tokens(requirement);
        var requirementConcepts = Concepts(requirement);
        if (requirementTokens.Count == 0 && requirementConcepts.Count == 0) return null;

        string? best = null;
        var bestScore = 0.0;
        foreach (var fact in _profile.ListFacts())
        {
            var factTokens = Tokens(fact.Text);
            var factConcepts = Concepts(fact.Text);
            if (factTokens.Count == 0 && factConcepts.Count == 0) continue;

            var lexical = 0.0;
            if (requirementTokens.Count > 0 && factTokens.Count > 0)
            {
                lexical = requirementTokens.Intersect(factTokens).Count()
                    / (double)requirementTokens.Union(factTokens).Count();
            }

            // Measured against the requirement, not the union: a long fact that happens to share
            // one word with a short requirement is not what "nearest" is asking about.
            var conceptual = requirementConcepts.Count == 0
                ? 0
                : requirementConcepts.Intersect(factConcepts).Count() / (double)requirementConcepts.Count;

            var score = Math.Max(lexical, conceptual);
            if (score > bestScore)
            {
                bestScore = score;
                best = fact.Text;
            }
        }

        // Below a third there is nothing worth calling "nearest"; saying so is better than a stretch.
        return bestScore >= 0.33 ? best : null;
    }

    /// <summary>
    /// Which named technology families this text falls into. A family is named once if any of its
    /// terms appears as a whole term, so "infrastructure as code" counts and "nosql" does not
    /// smuggle itself in as "sql".
    /// </summary>
    private static HashSet<string> Concepts(string text)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return found;

        var lower = text.ToLowerInvariant();
        foreach (var (concept, terms) in ConceptFamilies)
        {
            foreach (var term in terms)
            {
                if (Keywords.ContainsTerm(lower, term))
                {
                    found.Add(concept);
                    break;
                }
            }
        }
        return found;
    }

    private static HashSet<string> Tokens(string text) =>
        new(System.Text.RegularExpressions.Regex
                .Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}")
                .Select(m => m.Value)
                .Where(t => !StopWords.Contains(t)),
            StringComparer.Ordinal);

    private async Task<List<Suggestion>> ModelSuggestAsync(string requirement, IReadOnlyList<string> missing, CancellationToken ct)
    {
        var facts = _profile.ListFacts();
        if (facts.Count == 0) return new List<Suggestion>();

        var system = ModelRouter.SystemPrompt(Workflow, _profile.BuildReference());
        var user =
            "Suggest at most three concrete ways to close this gap: " + requirement + "\n" +
            (missing.Count > 0 ? "Specifically missing: " + string.Join(", ", missing) + "\n" : "") +
            "\nEvery suggestion MUST build on a verified fact listed above and MUST name its id in \"buildsOnFactId\". " +
            "Prefer an analogy, a home lab, or a small transferable step over a course. Do not suggest a course, " +
            "certification or book unless a verified fact already shows grounding in the subject. " +
            "Return a JSON array of {\"text\": string, \"buildsOnFactId\": string, \"why\": string} and nothing else.";

        var run = await _router.RunStructuredAsync(Workflow, system, user, Parse, ct: ct).ConfigureAwait(false);
        if (run.Value is null) return new List<Suggestion>();

        var byId = facts.ToDictionary(f => f.Id, f => f.Text, StringComparer.Ordinal);
        var kept = new List<Suggestion>();
        foreach (var s in run.Value)
        {
            // The citation is the whole safety property. No fact, no suggestion.
            if (string.IsNullOrWhiteSpace(s.BuildsOnFactId) || !byId.TryGetValue(s.BuildsOnFactId.Trim(), out var factText))
                continue;
            kept.Add(new Suggestion(s.Text, s.BuildsOnFactId.Trim(), factText, s.Why));
        }
        return kept;
    }

    private sealed record ModelSuggestion(string Text, string BuildsOnFactId, string Why);

    private static List<ModelSuggestion>? Parse(string reply)
    {
        var start = reply.IndexOf('[');
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<List<ModelSuggestion>>(reply[start..(end + 1)],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return parsed?
                .Where(s => !string.IsNullOrWhiteSpace(s.Text) && !string.IsNullOrWhiteSpace(s.BuildsOnFactId))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Technology families that transfer to each other. Deliberately small: every entry is a pair
    /// of things where doing one teaches you the other, which is the only claim a gap ramp is
    /// allowed to make. Anything not listed simply does not get a concept match.
    /// </summary>
    private static readonly (string Concept, string[] Terms)[] ConceptFamilies =
    {
        ("virtualisation", new[]
        {
            "virtualisation", "virtualization", "virtual machine", "vmware", "vsphere", "esxi", "vcenter",
            "hypervisor", "kvm", "qemu", "proxmox", "libvirt", "xen", "hyper-v", "vagrant"
        }),
        ("containers", new[]
        {
            "container", "containers", "docker", "kubernetes", "k8s", "podman", "openshift", "containerd",
            "helm", "dockerfile"
        }),
        ("infrastructure as code", new[]
        {
            "terraform", "pulumi", "ansible", "cloudformation", "bicep", "infrastructure as code", "packer"
        }),
        ("cloud", new[]
        {
            "aws", "amazon web services", "azure", "gcp", "google cloud", "ec2", "s3", "lambda",
            "cloud run", "cloud computing", "cloud platform"
        }),
        ("relational data", new[]
        {
            "sql", "postgres", "postgresql", "mysql", "sql server", "oracle", "mariadb", "relational database"
        }),
        ("data platform", new[]
        {
            "spark", "hadoop", "kafka", "airflow", "dbt", "snowflake", "bigquery", "redshift", "databricks",
            "data warehouse", "data pipeline", "etl", "data engineering"
        }),
        ("delivery", new[]
        {
            "ci/cd", "cicd", "github actions", "jenkins", "gitlab ci", "continuous integration",
            "continuous delivery", "continuous deployment"
        }),
        ("frontend", new[]
        {
            "react", "vue", "angular", "svelte", "sveltekit", "next.js", "front-end", "frontend", "tailwind", "css", "html"
        }),
        ("networking", new[]
        {
            "networking", "firewall", "cisco", "tcp/ip", "load balancer", "nginx", "dns", "vpn", "subnet"
        }),
        ("operations", new[]
        {
            "monitoring", "observability", "prometheus", "grafana", "datadog", "site reliability", "logging", "on-call"
        }),
        ("linux", new[]
        {
            "linux", "unix", "bash", "systemd", "debian", "ubuntu", "shell scripting"
        })
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","with","you","your","our","this","that","will","have","has","are","was","were","not","but",
        "can","could","should","would","must","from","into","than","then","them","they","who","what","when","where",
        "how","which","while","experience","experienced","years","skills","skill","knowledge","understanding",
        "familiarity","ability","strong","good","great","level","plus","using","use","used","work","working"
    };
}