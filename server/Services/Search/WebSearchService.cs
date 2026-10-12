using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ResuClean.Models;
using ResuClean.Services.Providers;

namespace ResuClean.Services.Search;

/// <summary>One result from the web, kept as a citation the user can open.</summary>
public sealed record SearchHit(string Title, string Url, string Snippet, string Source);

public sealed record WebSearchRequest(string Query, string? Location, int MaxResults = 8);

/// <summary>
/// What actually answered, so the UI can be honest about it. Exactly one tier produces the answer;
/// the tiers are tried in order and the first one that works wins.
/// </summary>
public sealed record WebSearchAnswer(
    string Answer,
    IReadOnlyList<SearchHit> Results,
    string Tier,
    bool UsedModel,
    string? Provider = null,
    string? Model = null,
    IReadOnlyList<string>? Notes = null);

/// <summary>
/// A keyless-by-default web search that degrades honestly.
///
/// Tier 1  native  - the routed model's own server-side search tool. No extra key, real citations.
/// Tier 2  api     - a dedicated search API (Tavily or Brave) read from the environment.
/// Tier 3  raw     - the search API's results with no model involved. Still useful, never a guess.
///
/// A deliberate design constraint: output from this service is QUOTED and is never written to the
/// verified profile. Search introduces numbers, dates and company names by definition, so it must
/// never pass through the resume no-fabrication guard, which exists to police claims about *you*.
/// </summary>
public sealed class WebSearchService
{
    public const string TavilyEnvVar = "TAVILY_API_KEY";
    public const string BraveEnvVar = "BRAVE_SEARCH_API_KEY";

    /// <summary>Route used for the native tier. Deliberately separate from the resume workflows.</summary>
    public const string Workflow = "web_search";

    private readonly ProviderService _providers;
    private readonly ModelRouter _router;
    private readonly IHttpClientFactory _factory;

    public WebSearchService(ProviderService providers, ModelRouter router, IHttpClientFactory factory)
    {
        _providers = providers;
        _router = router;
        _factory = factory;
    }

    /// <summary>Which tiers could run right now, without spending anything.</summary>
    public WebSearchCapabilities Capabilities()
    {
        var hasApi = !string.IsNullOrWhiteSpace(ApiKey());
        return new WebSearchCapabilities(
            NativeConfigured: _router.AnyConfigured(Workflow),
            SearchApiConfigured: hasApi,
            SearchApiName: SearchApiName());
    }

    public sealed record WebSearchCapabilities(
        bool NativeConfigured,
        bool SearchApiConfigured,
        string? SearchApiName);

    private static string? ApiKey() =>
        Environment.GetEnvironmentVariable(TavilyEnvVar) is { Length: > 0 } t ? t
        : Environment.GetEnvironmentVariable(BraveEnvVar);

    private static string? SearchApiName() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TavilyEnvVar)) ? "Tavily"
        : !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(BraveEnvVar)) ? "Brave Search"
        : null;

    public async Task<WebSearchAnswer> SearchAsync(WebSearchRequest request, CancellationToken ct)
    {
        var notes = new List<string>();
        var query = BuildQuery(request);

        // ---- Tier 1: the routed model's own search tool ------------------------
        if (_router.AnyConfigured(Workflow))
            {
                try
                {
                    var system = BuildSystemPrompt();
                    var user = BuildUserPrompt(query, request.Location);

                    // The reply itself reports whether the provider actually ran its search tool.
                    var routed = await _router.RunAsync(
                        Workflow, system, user,
                        call: async (provider, model, sys, token) =>
                        {
                            var reply = await _providers.CallAsync(provider, model, sys, user, token, allowWebSearch: true)
                                .ConfigureAwait(false);
                            return new ModelRouter.CallResult(
                                reply.Text, reply.PromptTokens, reply.CompletionTokens, reply.UsedNativeSearch);
                        },
                        ct: ct).ConfigureAwait(false);

                    if (routed.UsedModel && !string.IsNullOrWhiteSpace(routed.Text))
                    {
                        // If the provider silently ignored the tool, say so instead of pretending it searched.
                        if (!routed.SearchedTheWeb)
                            notes.Add("The model answered without using its search tool. The answer may not reflect current information.");
                        return new WebSearchAnswer(
                            routed.Text, Array.Empty<SearchHit>(), "native", true,
                            routed.Provider, routed.Model, notes);
                    }
                }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"Native model search unavailable: {ex.Message}");
            }
        }

        // ---- Tier 2: a dedicated search API -----------------------------------
        var hits = await SearchApiAsync(query, request.MaxResults, ct).ConfigureAwait(false);
        if (hits.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(ApiKey()) && _router.AnyConfigured(Workflow))
            {
                // A model is configured, so summarise the results rather than dumping a bare list.
                var summarised = await SummariseAsync(query, hits, ct).ConfigureAwait(false);
                if (summarised is not null)
                    return new WebSearchAnswer(summarised, hits, "api+model", true, Notes: notes);
            }

            return new WebSearchAnswer(
                string.Empty, hits, "api", false, Notes: notes);
        }

        if (notes.Count == 0)
            notes.Add("No search backend is available. Set " + TavilyEnvVar + " or " + BraveEnvVar
                      + " in .env, or route the 'web_search' workflow to a model with a search tool.");

        return new WebSearchAnswer(
            string.Empty, Array.Empty<SearchHit>(), "none", false, Notes: notes);
    }

    private static string BuildQuery(WebSearchRequest request)
    {
        var q = request.Query.Trim();
        if (!string.IsNullOrWhiteSpace(request.Location)) q += $" {request.Location.Trim()}";
        return q;
    }

    private static string BuildSystemPrompt() =>
        "You are a research assistant helping someone job hunt. Answer using web search results only. "
        + "Cite sources inline as [1], [2] matching the numbered results. "
        + "If the results do not answer the question, say so plainly instead of guessing. "
        + "Never invent statistics, company details or dates that were not in the results.";

    private static string BuildUserPrompt(string query, string? location) =>
        string.IsNullOrWhiteSpace(location)
            ? $"Search the web for: {query}\n\nGive a direct, factual answer with inline citations."
            : $"Search the web for: {query}\n\nPreferred location: {location}. Give a direct, factual answer with inline citations.";

    /// <summary>
    /// Optional model pass over results from the search API. Returns null when no model is routed,
    /// so the caller can fall back to the raw list rather than reporting an empty answer.
    /// </summary>
    private async Task<string?> SummariseAsync(string query, IReadOnlyList<SearchHit> hits, CancellationToken ct)
    {
        try
        {
            var numbered = string.Join("\n", hits.Select((h, i) =>
                $"[{i + 1}] {h.Title}\n{h.Url}\n{h.Snippet}"));

            var system = "You summarise web search results for a job seeker. Be concise and factual. "
                + "Cite with [n] matching the numbered list. Do not add facts that are not in the results.";

            var routed = await _router.RunAsync(Workflow, system, $"Query: {query}\n\nResults:\n{numbered}", ct: ct)
                .ConfigureAwait(false);

            return routed.UsedModel && !string.IsNullOrWhiteSpace(routed.Text) ? routed.Text : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // ---- Search API ---------------------------------------------------------

    private async Task<List<SearchHit>> SearchApiAsync(string query, int maxResults, CancellationToken ct)
    {
        var tavily = Environment.GetEnvironmentVariable(TavilyEnvVar);
        if (!string.IsNullOrWhiteSpace(tavily)) return await TavilyAsync(tavily, query, maxResults, ct).ConfigureAwait(false);

        var brave = Environment.GetEnvironmentVariable(BraveEnvVar);
        if (!string.IsNullOrWhiteSpace(brave)) return await BraveAsync(brave, query, maxResults, ct).ConfigureAwait(false);

        return new List<SearchHit>();
    }

    private async Task<List<SearchHit>> TavilyAsync(string key, string query, int maxResults, CancellationToken ct)
    {
        var client = _factory.CreateClient("search");
        var payload = JsonSerializer.Serialize(new
        {
            api_key = key,
            query,
            max_results = maxResults,
            search_depth = "basic",
            include_answer = false
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new List<SearchHit>();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var hits = new List<SearchHit>();
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                var title = Str(r, "title");
                var url = Str(r, "url");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url)) continue;
                hits.Add(new SearchHit(title, url, Str(r, "content"), "Tavily"));
                if (hits.Count >= maxResults) break;
            }
        }
        return hits;
    }

    private async Task<List<SearchHit>> BraveAsync(string key, string query, int maxResults, CancellationToken ct)
    {
        var client = _factory.CreateClient("search");
        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={maxResults}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Subscription-Token", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return new List<SearchHit>();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var hits = new List<SearchHit>();
        if (doc.RootElement.TryGetProperty("web", out var web)
            && web.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in results.EnumerateArray())
            {
                var title = Str(r, "title");
                var link = Str(r, "url");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;
                hits.Add(new SearchHit(title, link, Str(r, "description"), "Brave Search"));
                if (hits.Count >= maxResults) break;
            }
        }
        return hits;
    }

    private static string Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? string.Empty)
            : string.Empty;
}