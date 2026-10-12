using System.Diagnostics;
using ResuClean;
using ResuClean.Models;
using ResuClean.Services;
using ResuClean.Services.Providers;

namespace ResuClean.Endpoints;

public static class SystemEndpoints
{
    public const string Version = "1.0.0";

    private static long _requests;

    public static void Map(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            System.Threading.Interlocked.Increment(ref _requests);
            await next(ctx).ConfigureAwait(false);
        });

        var group = app.MapGroup("/api").WithTags("System");

        group.MapGet("/health", () => Api.Ok(new HealthResponse("ok", Version,
            (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds)));

        group.MapGet("/metrics/mem", (OpRunner ops) => Api.Ok(ReadMemory(ops)));

        /// <summary>
        /// What the model calls have cost. The app tracks memory with the same care, and models are
        /// the one resource in it with no natural ceiling.
        /// </summary>
        group.MapGet("/metrics/models", (ModelRouter router) => Api.Ok(new ModelUsageResponse(
            router.UsageByWorkflow(), router.RecentUsage(50))));

        group.MapDelete("/metrics/models", (ModelRouter router) =>
            Api.Guard(() => { router.ClearUsage(); return Task.FromResult(Api.Ok(new { cleared = true })); }));

        group.MapGet("/ops/{id}", (string id, OpRunner ops) =>
            Api.Guard(() =>
            {
                var status = ops.Get(id);
                return Task.FromResult(status is null
                    ? Api.Error(StatusCodes.Status404NotFound, "not_found", "No operation with that id.")
                    : Api.Ok(status));
            }));

        group.MapGet("/ops", (OpRunner ops, HttpRequest request) =>
            Api.Guard(() => Task.FromResult(Api.Ok(ops.List(Api.Page(request), Api.Size(request))))));

        /// <summary>Tells the UI which features need a model, so it can explain itself honestly.</summary>
        group.MapGet("/capabilities", (ProviderService providers, ModelRouter router, Configuration.AppConfig app) =>
        {
            var needsModel = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["deterministic clean"] = false,
                ["model rewrite for clarity"] = router.AnyConfigured("clean"),
                ["ats rule scan"] = false,
                ["ats prioritised fixes"] = router.AnyConfigured("ats"),
                ["tailor resume"] = router.AnyConfigured("update_resume"),
                ["extract facts from text"] = router.AnyConfigured("profile"),
                // Grounding and requirements both work without a model: the deterministic passes
                // check unverified figures and classify by section and marker phrase. A route only
                // adds claim-level citation and judgement.
                ["check a resume against the verified profile"] = router.AnyConfigured("fact_check"),
                ["classify job requirements"] = router.AnyConfigured("requirements"),
                ["suggest a ramp to close a gap"] = router.AnyConfigured("gap_ramp"),
                ["propose skill dictionary additions"] = router.AnyConfigured("dictionary"),
                ["flag job posting problems"] = router.AnyConfigured("job_quality"),
                ["predict interview questions"] = router.AnyConfigured("interview"),
                ["job search"] = false,
                ["application email"] = router.AnyConfigured("apply_email"),
                ["template application email"] = false,
                // Search needs a backend, but unlike the others a raw result list is always available
                // once a search key exists, so this reports the model tier separately from the API.
                ["web search (model)"] = router.AnyConfigured("web_search"),
                ["web search (search API)"] =
                    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Services.Search.WebSearchService.TavilyEnvVar))
                    || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Services.Search.WebSearchService.BraveEnvVar))
            };
            return Api.Ok(new CapabilitiesResponse(
                providers.AnyConfigured(), needsModel, ModelRouter.Workflows,
                app.AuthRequired ? "api-key" : "open-localhost",
                (int)(app.MaxUploadBytes / 1024 / 1024), Version));
        });

        group.MapGet("/config", (Configuration.AppConfig app) => Api.Ok(new ConfigResponse(
            app.AuthRequired,
            (int)(app.MaxUploadBytes / 1024 / 1024),
            app.MaxConcurrency,
            app.CacheTtlSeconds,
            app.UserAgent,
            app.Swagger,
            Version)));
    }

    private static MemMetricsResponse ReadMemory(OpRunner ops)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new MemMetricsResponse(
            Math.Round(process.WorkingSet64 / 1048576.0, 1),
            Math.Round(process.PrivateMemorySize64 / 1048576.0, 1),
            Math.Round(process.PeakWorkingSet64 / 1048576.0, 1),
            (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds,
            System.Threading.Interlocked.Read(ref _requests),
            ops.ActiveCount,
            process.Threads.Count);
    }
}