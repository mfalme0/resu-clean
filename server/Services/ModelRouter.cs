using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResuClean.Data;
using ResuClean.Models;
using ResuClean.Services.Providers;

namespace ResuClean.Services;

/// <summary>
/// Workflow -> ordered provider/model routing with fallback. A failing entry is skipped and the
/// next one is tried, so a bad key or a rate limit on one provider does not break the workflow.
/// </summary>
public sealed class ModelRouter
{
    public static readonly string[] Workflows =
    {
        "clean", "ats", "profile", "update_resume", "find_jobs", "apply_email", "web_search",
        "fact_check", "requirements", "gap_ramp", "dictionary", "job_quality", "interview"
    };

    private readonly Db _db;
    private readonly ProviderService _providers;
    private readonly Configuration.AppConfig _app;

    public ModelRouter(Db db, ProviderService providers, Configuration.AppConfig app)
    {
        _db = db;
        _providers = providers;
        _app = app;
    }

    public RouteDto GetRoute(string workflow)
    {
        if (!Workflows.Contains(workflow)) throw new ArgumentException($"Unknown workflow '{workflow}'.");
        using var conn = _db.Open();
        var entries = Db.Query(conn,
            "SELECT r.ordinal, r.provider_id, r.model, p.name AS provider_name FROM routes r " +
            "LEFT JOIN providers p ON p.id = r.provider_id WHERE r.workflow = @w ORDER BY r.ordinal",
            r => new RouteEntryDto(Db.Int(r, "ordinal"), Db.Str(r, "provider_id"), Db.Str(r, "provider_name"), Db.Str(r, "model")),
            new[] { Db.P("@w", workflow) });

        var first = entries.FirstOrDefault(e => _providers.Get(e.ProviderId) is { Enabled: true });
        return new RouteDto(workflow, entries, first?.Model, first?.ProviderName);
    }

    public RouteDto SetRoute(string workflow, SetRouteRequest request)
    {
        if (!Workflows.Contains(workflow)) throw new ArgumentException($"Unknown workflow '{workflow}'.");
        using var conn = _db.Open();
        var tx = conn.BeginTransaction();
        try
        {
            Db.Exec(conn, "DELETE FROM routes WHERE workflow = @w", Db.P("@w", workflow));
            var ordinal = 0;
            foreach (var entry in request.Entries ?? new List<RouteEntryRequest>())
            {
                if (string.IsNullOrWhiteSpace(entry.ProviderId) || string.IsNullOrWhiteSpace(entry.Model)) continue;
                Db.Exec(conn, "INSERT INTO routes (workflow, ordinal, provider_id, model) VALUES (@w, @o, @p, @m)",
                    Db.P("@w", workflow), Db.P("@o", ordinal++), Db.P("@p", entry.ProviderId), Db.P("@m", entry.Model));
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return GetRoute(workflow);
    }

    public bool AnyConfigured(string workflow)
    {
        var route = GetRoute(workflow);
        return route.Entries.Count > 0 && route.Entries.Any(e =>
        {
            var provider = _providers.Get(e.ProviderId);
            return provider is { Enabled: true, Reachable: true };
        });
    }

    /// <summary>What one provider call returned, including what it cost.</summary>
    public sealed record CallResult(
        string Text,
        int PromptTokens = 0,
        int CompletionTokens = 0,
        bool UsedNativeSearch = false);

    public sealed record RoutedResult(
        string Text,
        bool UsedModel,
        string? Provider,
        string? Model,
        IReadOnlyList<string> Attempts,
        /// <summary>
        /// True only when the provider reported that it actually ran its server-side search tool.
        /// A model can accept the request and answer from memory instead, and the search feature has
        /// to be able to tell the user which of those happened.
        /// </summary>
        bool SearchedTheWeb = false,
        int PromptTokens = 0,
        int CompletionTokens = 0,
        /// <summary>True when the reply was served from <c>model_cache</c> rather than a live call.</summary>
        bool FromCache = false);

    /// <summary>A routed run plus the parsed payload, for workflows whose contract is structured.</summary>
    public sealed record StructuredRun<T>(T? Value, RoutedResult Routed) where T : class;

    /// <summary>
    /// Runs the route entries in order until one succeeds.
    ///
    /// Three things count as a failure and fall through to the next entry, exactly like a network
    /// error: the provider is gone or disabled, the reply is empty, and — when <paramref name="validate"/>
    /// is supplied — the reply does not match the shape the workflow expects. That last one matters
    /// because a model asked for JSON will sometimes answer in prose, and silently parsing that into
    /// an empty result is how a partial failure becomes an invisible one.
    ///
    /// Returns UsedModel = false when nothing succeeded, so callers fall back to deterministic
    /// behaviour. This is what makes the app work with no model at all.
    /// </summary>
    public async Task<RoutedResult> RunAsync(
        string workflow,
        string system,
        string user,
        Func<ProviderDto, string, string, CancellationToken, Task<CallResult>>? call = null,
        CancellationToken ct = default,
        Func<string, bool>? validate = null,
        bool bypassCache = false)
    {
        var route = GetRoute(workflow);
        var attempts = new List<string>();

        if (route.Entries.Count == 0)
            return new RoutedResult(string.Empty, false, null, null, new[] { "No route configured for this workflow; used the deterministic path instead." });

        async Task<CallResult> InvokeDefault(ProviderDto provider, string model, string sys, CancellationToken token)
        {
            var reply = await _providers.CallAsync(provider, model, sys, user, token).ConfigureAwait(false);
            return new CallResult(reply.Text, reply.PromptTokens, reply.CompletionTokens, reply.UsedNativeSearch);
        }

        var invoke = call ?? InvokeDefault;

        foreach (var entry in route.Entries)
        {
            var provider = _providers.Get(entry.ProviderId);
            if (provider is null)
            {
                attempts.Add($"{entry.ProviderName ?? entry.ProviderId}: provider was removed. Skipped.");
                continue;
            }
            if (!provider.Enabled)
            {
                attempts.Add($"{provider.Name}: disabled. Skipped.");
                continue;
            }

            var cacheKey = CacheKey(workflow, system, user, entry.ProviderId, entry.Model);
            if (!bypassCache)
            {
                var cached = ReadCache(cacheKey);
                if (cached is not null)
                {
                    RecordUsage(workflow, provider, entry.Model, new CallResult(cached), cached: true, elapsedMs: 0);
                    return new RoutedResult(cached, true, provider.Name, entry.Model, attempts, FromCache: true);
                }
            }

            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var reply = await invoke(provider, entry.Model, system, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(reply.Text))
                {
                    attempts.Add($"{provider.Name}/{entry.Model}: empty reply. Trying the next route.");
                    continue;
                }
                if (validate is not null && !Matches(validate, reply.Text))
                {
                    attempts.Add($"{provider.Name}/{entry.Model}: reply did not match the expected shape. Trying the next route.");
                    continue;
                }

                started.Stop();
                if (!bypassCache) WriteCache(cacheKey, workflow, entry.ProviderId, entry.Model, reply.Text);
                RecordUsage(workflow, provider, entry.Model, reply, cached: false, started.ElapsedMilliseconds);

                return new RoutedResult(reply.Text, true, provider.Name, entry.Model, attempts,
                    reply.UsedNativeSearch, reply.PromptTokens, reply.CompletionTokens);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempts.Add($"{provider.Name}/{entry.Model}: {ex.Message} Trying the next route.");
            }
        }

        return new RoutedResult(string.Empty, false, null, null, attempts);
    }

    /// <summary>
    /// Runs a shape check that is allowed to fail by throwing. A JSON parser handed prose throws
    /// JsonException; letting that escape would surface as a provider error and name the wrong
    /// thing as the cause. A shape mismatch is a shape mismatch however it is discovered.
    /// </summary>
    private static bool Matches(Func<string, bool> validate, string reply)
    {
        try
        {
            return validate(reply);
        }
        catch
        {
            return false;
        }
    }

    public Task<RoutedResult> RunAsync(string workflow, string system, string user, CancellationToken ct) =>
        RunAsync(workflow, system, user, null, ct);

    /// <summary>
    /// Runs a workflow whose output is a structured document rather than prose.
    ///
    /// <paramref name="parse"/> returns null for anything that is not the expected shape. That is
    /// treated as a routing failure, so a model that ignores the JSON instruction falls through to
    /// the next route entry and, ultimately, to the deterministic path — rather than producing a
    /// half-empty report that looks like a real answer.
    /// </summary>
    public async Task<StructuredRun<T>> RunStructuredAsync<T>(
        string workflow,
        string system,
        string user,
        Func<string, T?> parse,
        Func<ProviderDto, string, string, CancellationToken, Task<CallResult>>? call = null,
        CancellationToken ct = default,
        bool bypassCache = false) where T : class
    {
        T? parsed = null;
        var routed = await RunAsync(workflow, system, user, call, ct, reply =>
        {
            var value = parse(reply);
            if (value is null) return false;
            parsed = value;
            return true;
        }, bypassCache).ConfigureAwait(false);

        return new StructuredRun<T>(routed.UsedModel ? parsed : default, routed);
    }

    /// <summary>
    /// Makes a real call to test a provider/model pair. The pair comes from the request rather than
    /// the saved route, so "Live test" also works on an unsaved draft. Matching against saved
    /// entries made testing a brand new model impossible: it replied "add one in Routes first",
    /// which is circular when the point of the button is to check the line you just typed.
    /// </summary>
    public async Task<RoutePreviewResponse> PreviewAsync(string workflow, string? providerId, string? model, CancellationToken ct = default)
    {
        if (!Workflows.Contains(workflow))
            throw new ArgumentException($"Workflow must be one of: {string.Join(", ", Workflows)}.");

        // No provider given: fall back to the first saved entry for this workflow.
        if (string.IsNullOrWhiteSpace(providerId))
        {
            var saved = GetRoute(workflow).Entries.FirstOrDefault();
            providerId = saved?.ProviderId;
            if (string.IsNullOrWhiteSpace(model)) model = saved?.Model;
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            return new RoutePreviewResponse(false,
                $"Nothing to test for \"{workflow}\" yet. Write a line like prv_abc::model-name above, then press Save or Live test.",
                0, null);
        }

        var provider = _providers.Get(providerId);
        if (provider is null)
        {
            // Returned rather than thrown: this is a normal outcome when a provider was deleted
            // while a route still referenced it, and the UI shows this message verbatim.
            return new RoutePreviewResponse(false,
                $"Provider '{providerId}' no longer exists. Press Refresh on the Models page and pick another one.",
                0, null);
        }

        if (string.IsNullOrWhiteSpace(model))
            return new RoutePreviewResponse(false, "No model selected. Write one after the :: in the line above.", 0, null);

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var reply = await _providers.CallAsync(provider, model,
                "You are a routing preview. Reply with exactly the word OK.", "Reply with exactly the word OK.", ct).ConfigureAwait(false);
            return new RoutePreviewResponse(true, $"Live call succeeded in {started.ElapsedMilliseconds} ms: {reply.Text.Trim()}", started.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            return new RoutePreviewResponse(false, ex.Message, started.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    // ---- Cache and cost telemetry ------------------------------------------

    /// <summary>
    /// Content hash of everything that determines the answer. Identical input on the same model is
    /// the same question, so it does not get asked twice. Every component is separated by a unit
    /// separator that cannot appear in the payloads, so two different splits cannot collide.
    /// </summary>
    private const string Separator = "\u001f";

    private static string CacheKey(string workflow, string system, string user, string providerId, string model) =>
        ProfileService.ShortHash(string.Join(Separator, workflow, system, user, providerId, model));

    private string? ReadCache(string key)
    {
        if (_app.CacheTtlSeconds <= 0) return null;
        try
        {
            using var conn = _db.Open();
            var row = Db.QueryOne(conn,
                "SELECT response, created_at FROM model_cache WHERE key = @k",
                r => (Response: Db.Str(r, "response"), Created: Db.Str(r, "created_at")),
                Db.P("@k", key));
            if (row.Response is null) return null;
            if (!DateTime.TryParse(row.Created, out var created)) return null;
            return DateTime.UtcNow - created < TimeSpan.FromSeconds(_app.CacheTtlSeconds) ? row.Response : null;
        }
        catch
        {
            // A cache read failure must never break a workflow. The call is simply made instead.
            return null;
        }
    }

    private void WriteCache(string key, string workflow, string providerId, string model, string response)
    {
        if (_app.CacheTtlSeconds <= 0) return;
        try
        {
            using var conn = _db.Open();
            Db.Exec(conn,
                "INSERT INTO model_cache (key, workflow, provider_id, model, response, created_at) "
                + "VALUES (@k,@w,@p,@m,@r,@n) ON CONFLICT(key) DO UPDATE SET response=@r, created_at=@n",
                Db.P("@k", key), Db.P("@w", workflow), Db.P("@p", providerId), Db.P("@m", model),
                Db.P("@r", response), Db.P("@n", DateTime.UtcNow.ToString("O")));
        }
        catch
        {
            // Same rule as SourceHttp: a cache write failure must never break a search.
        }
    }

    /// <summary>
    /// Records what a call cost. Models are the one resource in this system with no natural ceiling,
    /// and the app already tracks RAM the same way.
    /// </summary>
    private void RecordUsage(string workflow, ProviderDto provider, string model, CallResult result, bool cached, long elapsedMs)
    {
        try
        {
            using var conn = _db.Open();
            Db.Exec(conn,
                "INSERT INTO model_usage (id, workflow, provider_id, provider_name, model, prompt_tokens, completion_tokens, cached, elapsed_ms, created_at) "
                + "VALUES (@i,@w,@p,@pn,@m,@pt,@ct,@c,@e,@n)",
                Db.P("@i", ProfileService.NewId("use")), Db.P("@w", workflow), Db.P("@p", provider.Id),
                Db.P("@pn", provider.Name), Db.P("@m", model),
                Db.P("@pt", result.PromptTokens), Db.P("@ct", result.CompletionTokens),
                Db.P("@c", cached ? 1 : 0), Db.P("@e", elapsedMs),
                Db.P("@n", DateTime.UtcNow.ToString("O")));
        }
        catch
        {
            // Telemetry is not worth failing a workflow over.
        }
    }

    /// <summary>Recent model calls, newest first, for the Models screen.</summary>
    public List<ModelUsageDto> RecentUsage(int limit = 50) => Db.Query(_db.Open(),
        "SELECT id, workflow, provider_name, model, prompt_tokens, completion_tokens, cached, elapsed_ms, created_at "
        + "FROM model_usage ORDER BY created_at DESC, rowid DESC LIMIT @l",
        r => new ModelUsageDto(
            Db.Str(r, "id"), Db.Str(r, "workflow"), Db.Str(r, "provider_name"), Db.Str(r, "model"),
            Db.Int(r, "prompt_tokens"), Db.Int(r, "completion_tokens"), Db.Bool(r, "cached"),
            Db.Int(r, "elapsed_ms"), Db.Str(r, "created_at")),
        Db.P("@l", Math.Clamp(limit, 1, 500)));

    /// <summary>Totals per workflow, so the expensive one is visible rather than inferred.</summary>
    public List<ModelUsageSummaryDto> UsageByWorkflow() => Db.Query(_db.Open(),
        "SELECT workflow, COUNT(*) AS calls, "
        + "SUM(cached) AS cached_calls, "
        + "SUM(prompt_tokens + completion_tokens) AS tokens, "
        + "CAST(AVG(elapsed_ms) AS INTEGER) AS avg_ms "
        + "FROM model_usage GROUP BY workflow ORDER BY tokens DESC, workflow",
        r => new ModelUsageSummaryDto(
            Db.Str(r, "workflow"), Db.Int(r, "calls"), Db.Int(r, "cached_calls"),
            Db.Int(r, "tokens"), Db.Int(r, "avg_ms")));

    public void ClearUsage()
    {
        using var conn = _db.Open();
        Db.Exec(conn, "DELETE FROM model_usage");
        Db.Exec(conn, "DELETE FROM model_cache");
    }

    // ---- Prompts -----------------------------------------------------------

    /// <summary>
    /// The single system prompt that carries the hard no-fabrication rule, used by every
    /// model-assisted workflow.
    /// </summary>
    public static string SystemPrompt(string workflow, string profileReference) =>
$@"You are the writing engine inside resu-clean, a personal resume toolkit. You write for one person only.

ABSOLUTE RULES (violating any of these makes your output useless):
1. Never invent, infer or embellish any fact. Do not add employers, job titles, dates, metrics, technologies, certifications, education or locations that are not present in the facts you were given.
2. You may only reorganise, tighten, rephrase and reorder what was given.
3. You may mirror the wording of a job description ONLY where the underlying fact is already true for this person. Wording must stay inside the truth.
4. Never add a skill just because the job description mentions it.
5. If information seems to be missing, do not fill the gap. Say so in the NOTES section instead.
6. Keep every number, date and proper noun exactly as supplied. Do not round, convert or 'improve' figures.
7. Return plain text only. No markdown fences, no HTML, no tables, no bullet glyphs other than '- '.

OUTPUT FORMAT:
{OutputFormatFor(workflow)}

PROFILE (verified facts, the only source of truth):
{(string.IsNullOrWhiteSpace(profileReference) ? "(no profile facts recorded yet; work only from the resume text given below)" : profileReference)}";

    private static string OutputFormatFor(string workflow) => workflow switch
    {
        "clean" => "Return the full cleaned resume text. Then a blank line, then 'NOTES:' and at most three short bullet lines explaining what you changed and why.",
        "ats" => "Return 'FIXES:' followed by a numbered list of at most 6 prioritised fixes, most impactful first. Each fix is one line: what to change and why. No preamble.",
        "update_resume" => "Return the full updated resume text. Then a blank line, then 'NOTES:' with at most three lines. Then 'NEW INFO:' with one line per piece of information that is not in the profile or the resume, phrased as a standalone fact. If there is none, write 'NEW INFO: none'.",
        "profile" => "Return a comma-separated list of standalone facts extracted from the text, one per line, no numbering, no commentary.",
        "find_jobs" => "Return a JSON array of job objects with keys title, company, url. Return [] if there are no relevant results.",
        "apply_email" => "Return the email body only: at most 150 words, plain text, no subject line, no placeholders, no signature block placeholder. Every claim must come from the profile or resume.",
        // Search output is the one workflow allowed to contain numbers, dates and outside names,
        // because it is quoting the web rather than describing the user. It must cite [n] and must
        // never be treated as a source of profile facts.
        "web_search" => "Answer from the search results only. Cite sources inline as [1], [2]. If the results do not answer the question, say so plainly instead of guessing. Never state a statistic, date or company detail that is not in the results.",
        "fact_check" => "Return a JSON array and nothing else. Each entry: {\"text\": string, \"category\": string, \"factIds\": string[], \"grounded\": boolean}. Only use fact ids that appear in the verified facts above.",
        "requirements" => "Return a JSON array and nothing else. Each entry: {\"text\": string, \"class\": \"blocker\"|\"required\"|\"preferred\"|\"implicit\", \"evidence\": string}. One entry per requirement stated in the posting.",
        "gap_ramp" => "Return a JSON array and nothing else. Each entry: {\"text\": string, \"buildsOnFactId\": string, \"why\": string}. Every entry MUST name a verified fact id it builds on. An entry without one is discarded.",
        "dictionary" => "Return a JSON array and nothing else. Each entry: {\"term\": string, \"occurrences\": number}. Only terms that appear in the supplied text and are absent from the existing dictionary.",
        "job_quality" => "Return a JSON array and nothing else. Each entry: {\"kind\": string, \"severity\": \"info\"|\"warn\", \"detail\": string, \"quote\": string}. \"quote\" must be copied verbatim from the posting; an empty quote means the flag is dropped.",
        "interview" => "Return a JSON array and nothing else. Each entry: {\"question\": string, \"probes\": string, \"groundedIn\": string}. \"groundedIn\" must be a phrase copied from the resume or the posting.",
        _ => "Return plain text."
    };
}