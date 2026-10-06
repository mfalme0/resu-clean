using ResuClean;
using ResuClean.Models;
using ResuClean.Services;
using ResuClean.Services.Sources;

namespace ResuClean.Endpoints;

public static class JobEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/jobs").WithTags("Jobs");

        group.MapGet("/", (JobSearchService jobs, HttpRequest request) =>
            Api.Guard(() => Task.FromResult(Api.Ok(jobs.ListJobs(
                Api.Page(request), Api.Size(request),
                Api.Param(request, "q"), Api.Param(request, "sourceId"),
                Api.Param(request, "manual") is null ? null : Api.Param(request, "manual") == "true",
                Api.Param(request, "versionId"))))));

        /// <summary>Long-running search. Returns 202 with an opId the UI polls at /api/ops/{id}.</summary>
        group.MapPost("/search", (JobSearchRequest body, OpRunner ops, JobSearchService jobs) =>
            Api.Guard(() => Task.FromResult(ApiOps.AcceptedRun(ops, "find_jobs", body,
                async (payload, ct) =>
                {
                    var outcome = await jobs.SearchAsync(payload, ct).ConfigureAwait(false);
                    return (object?)new { outcome.Summary, Jobs = outcome.Jobs };
                }))));

        /// <summary>For link sources: the search URL resu-clean builds for you to open yourself.</summary>
        group.MapPost("/search-url", (UpsertSourceRequest body, SourceRegistry registry) =>
            Api.Guard(() =>
            {
                var source = registry.Save(new UpsertSourceRequest
                {
                    Id = body.Id,
                    Name = body.Name,
                    Type = "link",
                    Url = body.Url,
                    UrlTemplate = body.UrlTemplate,
                    Regions = body.Regions,
                    Notes = body.Notes
                });
                var url = SourceRegistry.SearchUrlFor(source, "query", "location");
                return Task.FromResult(Api.Ok(new SearchUrlResponse(url)));
            }));

        /// <summary>Save any posting by hand so it can enter the pipeline.</summary>
        group.MapPost("/", (SaveJobRequest body, JobSearchService jobs) =>
            Api.Guard(() => Task.FromResult(Api.Created(jobs.SaveManualJob(body), "/api/jobs"))));

        group.MapGet("/{id}", (string id, JobSearchService jobs) =>
            Api.Guard(() =>
            {
                var job = jobs.GetJob(id);
                return Task.FromResult(job is null
                    ? Api.Error(StatusCodes.Status404NotFound, "not_found", $"No job called '{id}'.")
                    : Api.Ok(job));
            }));

        group.MapDelete("/{id}", (string id, JobSearchService jobs) =>
            Api.Guard(() => Task.FromResult(jobs.DeleteJob(id)
                ? Api.Ok(new { deleted = true })
                : Api.Error(StatusCodes.Status404NotFound, "not_found", $"No job called '{id}'."))));
    }
}