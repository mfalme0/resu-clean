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
        "clean", "ats", "profile", "update_resume", "find_jobs", "apply_email"
    };

    private readonly Db _db;
    private readonly ProviderService _providers;

    public ModelRouter(Db db, ProviderService providers)
    {
        _db = db;
        _providers = providers;
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

    public sealed record RoutedResult(
        string Text,
        bool UsedModel,
        string? Provider,
        string? Model,
        IReadOnlyList<string> Attempts);

    /// <summary>
    /// Runs <paramref name="call"/> against each route entry in order until one succeeds.
    /// Returns UsedModel = false when no route is configured, so callers can fall back to
    /// deterministic behaviour. This is what makes the app work with no model at all.
    /// </summary>
    public async Task<RoutedResult> RunAsync(
        string workflow,
        string system,
        string user,
        Func<ProviderDto, string, string, CancellationToken, Task<string>>? call = null,
        CancellationToken ct = default)
    {
        var route = GetRoute(workflow);
        var attempts = new List<string>();

        if (route.Entries.Count == 0)
            return new RoutedResult(string.Empty, false, null, null, new[] { "No route configured for this workflow; used the deterministic path instead." });

        async Task<string> InvokeDefault(ProviderDto provider, string model, string sys, CancellationToken token)
        {
            var reply = await _providers.CallAsync(provider, model, sys, user, token).ConfigureAwait(false);
            return reply.Text;
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

            try
            {
                var reply = await invoke(provider, entry.Model, system, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(reply))
                {
                    attempts.Add($"{provider.Name}/{entry.Model}: empty reply. Trying the next route.");
                    continue;
                }
                return new RoutedResult(reply, true, provider.Name, entry.Model, attempts);
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

    public Task<RoutedResult> RunAsync(string workflow, string system, string user, CancellationToken ct) =>
        RunAsync(workflow, system, user, null, ct);

    public async Task<RoutePreviewResponse> PreviewAsync(string workflow, string model, CancellationToken ct = default)
    {
        var route = GetRoute(workflow);
        var entry = route.Entries.FirstOrDefault(e => string.IsNullOrWhiteSpace(model) || e.Model == model);
        if (entry is null)
            return new RoutePreviewResponse(false, $"No route entry matches model '{model}'. Add one in Routes first.", 0, null);

        var provider = _providers.Get(entry.ProviderId);
        if (provider is null) return new RoutePreviewResponse(false, "That provider no longer exists.", 0, null);

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var reply = await _providers.CallAsync(provider, entry.Model,
                "You are a routing preview. Reply with exactly the word OK.", "Reply with exactly the word OK.", ct).ConfigureAwait(false);
            return new RoutePreviewResponse(true, $"Live call succeeded in {started.ElapsedMilliseconds} ms: {reply.Text.Trim()}", started.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            return new RoutePreviewResponse(false, ex.Message, started.ElapsedMilliseconds, ex.GetType().Name);
        }
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
        _ => "Return plain text."
    };
}