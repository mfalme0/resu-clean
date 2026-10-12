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

        // ---- Requirement classification --------------------------------------

        /// <summary>
        /// Splits a posting's demands into blockers, required, preferred and implicit, and checks each
        /// against the verified profile. Needs no model; a route adds judgement for postings that do
        /// not follow the usual shape.
        /// </summary>
        group.MapGet("/{id}/requirements", (string id, RequirementsService requirements, JobSearchService jobs, ResumeStore store) =>
            Api.Guard(async () =>
            {
                var job = jobs.GetJob(id)
                    ?? throw new KeyNotFoundException($"No job called '{id}'.");
                if (string.IsNullOrWhiteSpace(job.Description))
                    throw new ArgumentException("This posting has no description text to read. Saved feed rows often carry title and company only.");

                var resumeText = store.GetText(store.BestFitVersionId() ?? "") ?? "";
                var report = await requirements.AnalyseAsync(id, job.Description, resumeText).ConfigureAwait(false);
                return Api.Ok(ToDto(report));
            }));

        group.MapGet("/{id}/requirements/saved", (string id, RequirementsService requirements) =>
            Api.Guard(() => Task.FromResult(Api.Ok(ToDto(new RequirementsService.Report(id, requirements.ForJob(id), false, null, "stored"))))));

        /// <summary>
        /// Classifies every posting with enough text to read. Deterministic-only by design: this is
        /// the batch that reorders the job list, and it should not spend a model call per posting.
        /// </summary>
        group.MapPost("/requirements/refresh", (RequirementsService requirements, ResumeStore store) =>
            Api.Guard(async () =>
            {
                var resumeText = store.GetText(store.BestFitVersionId() ?? "") ?? "";
                var candidates = requirements.Classifiable();
                var blockers = 0;
                foreach (var (jobId, description) in candidates)
                {
                    var report = await requirements.AnalyseAsync(jobId, description, resumeText).ConfigureAwait(false);
                    blockers += report.UnmetBlockers;
                }
                return Api.Ok(new { checkedJobs = candidates.Count, unmetBlockers = blockers });
            }));

        // ---- Posting quality -------------------------------------------------

        /// <summary>
        /// Staleness, title-vs-description mismatch and eligibility caveats. Staleness is arithmetic;
        /// the rest needs a quote from the posting before it will raise a flag.
        /// </summary>
        group.MapGet("/{id}/quality", (string id, JobQualityService quality, JobSearchService jobs) =>
            Api.Guard(async () =>
            {
                var job = jobs.GetJob(id) ?? throw new KeyNotFoundException($"No job called '{id}'.");
                var report = await quality.AnalyseAsync(id, job).ConfigureAwait(false);
                return Api.Ok(new JobQualityDto(
                    report.JobId,
                    report.Flags.Select(f => new JobFlagDto(f.Kind, f.Severity, f.Detail, f.Quote)).ToList(),
                    report.HasWarnings, report.UsedModel, report.Method));
            }));

        // ---- Interview prep --------------------------------------------------

        /// <summary>
        /// Questions the posting is likely to produce, each anchored to a phrase from the posting or
        /// the resume, and each tied to the claim it probes. With no model it still reports the
        /// claims on the resume that nothing backs — the landmines.
        /// </summary>
        group.MapGet("/{id}/interview", (string id, InterviewService interview, FactSupportService facts, JobSearchService jobs, ResumeStore store) =>
            Api.Guard(async () =>
            {
                var job = jobs.GetJob(id) ?? throw new KeyNotFoundException($"No job called '{id}'.");
                var versionId = store.BestFitVersionId()
                    ?? throw new ArgumentException("Mark a resume version as best-fit first, so the questions can be tied to the claims on it.");
                var resumeText = store.GetText(versionId) ?? "";

                var grounding = await facts.CheckAsync(versionId, resumeText).ConfigureAwait(false);
                var report = await interview.PredictAsync(id, versionId, job.Description, resumeText, grounding.Claims)
                    .ConfigureAwait(false);

                return Api.Ok(new InterviewPrepDto(
                    report.JobId, report.VersionId,
                    report.Questions.Select(q => new InterviewQuestionDto(q.Text, q.Probes, q.GroundedIn, q.Risk, q.RiskNote)).ToList(),
                    report.UsedModel, report.Model, report.Method, report.Notes));
            }));

        // ---- Gap ramp --------------------------------------------------------

        /// <summary>
        /// A grounded way to close a named gap. Every suggestion must cite a verified fact; with no
        /// model it reports the nearest thing you already have and makes no suggestion at all.
        /// </summary>
        group.MapPost("/gap-ramp", (GapRampRequest body, GapRampService gaps) =>
            Api.Guard(async () =>
            {
                if (string.IsNullOrWhiteSpace(body.Requirement))
                    throw new ArgumentException("Name the requirement you want to close.");

                var missing = body.Missing ?? new List<string>();
                var cls = body.RequirementClass ?? RequirementsService.Required;
                var report = await gaps.SuggestAsync(body.Requirement, cls, missing).ConfigureAwait(false);

                return Api.Ok(new GapRampDto(
                    report.Requirement, report.Class, report.Missing,
                    report.Suggestions.Select(s => new GapSuggestionDto(s.Text, s.BuildsOnFactId, s.BuildsOnFactText, s.Why)).ToList(),
                    report.NearestFact, gaps.IsClosable(cls, missing), report.UsedModel, report.Method));
            }));
    }

    private static JobRequirementsDto ToDto(RequirementsService.Report report) => new(
        report.JobId,
        report.Requirements.Select(r => new JobRequirementDto(r.Text, r.Class, r.Met, r.Missing, r.Evidence)).ToList(),
        report.UnmetBlockers,
        report.UnmetRequired,
        report.UsedModel,
        report.Model,
        report.Method);
}