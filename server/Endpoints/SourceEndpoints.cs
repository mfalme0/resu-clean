using ResuClean;
using ResuClean.Models;
using ResuClean.Services;
using ResuClean.Services.Sources;

namespace ResuClean.Endpoints;

public static class SourceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sources").WithTags("Sources");

        group.MapGet("/", (SourceRegistry registry, HttpRequest request) =>
            Api.Guard(() => Task.FromResult(Api.Ok(registry.List(enabledOnly: Api.Param(request, "enabled") == "true", Api.Param(request, "region"))))));

        group.MapPost("/", (UpsertSourceRequest body, SourceRegistry registry) =>
            Api.Guard(() => Task.FromResult(Api.Created(registry.Save(body), "/api/sources"))));

        group.MapGet("/{id}", (string id, SourceRegistry registry) =>
            Api.Guard(() =>
            {
                var source = registry.Get(id);
                return Task.FromResult(source is null
                    ? Api.Error(StatusCodes.Status404NotFound, "not_found", $"No source called '{id}'.")
                    : Api.Ok(source));
            }));

        group.MapPut("/{id}", (string id, UpsertSourceRequest body, SourceRegistry registry) =>
            Api.Guard(() => Task.FromResult(Api.Ok(registry.Save(new UpsertSourceRequest
            {
                Id = id,
                Name = body.Name,
                Type = body.Type,
                Url = body.Url,
                UrlTemplate = body.UrlTemplate,
                Regions = body.Regions,
                Enabled = body.Enabled,
                RateLimitMs = body.RateLimitMs,
                FieldMap = body.FieldMap,
                Selectors = body.Selectors,
                RequiresKey = body.RequiresKey,
                Notes = body.Notes
            })))));

        group.MapDelete("/{id}", (string id, SourceRegistry registry) =>
            Api.Guard(() => Task.FromResult(registry.Delete(id)
                ? Api.Ok(new { deleted = true })
                : Api.Error(StatusCodes.Status404NotFound, "not_found", $"No source called '{id}'."))));

        group.MapPost("/{id}/enabled", (bool body, string id, SourceRegistry registry) =>
            Api.Guard(() => Task.FromResult(registry.SetEnabled(id, body)
                ? Api.Ok(new { id, enabled = body })
                : Api.Error(StatusCodes.Status404NotFound, "not_found", $"No source called '{id}'."))));

        // ---- Discovery ------------------------------------------------------

        /// <summary>Add a source by URL: probe it, guess the type and preview the parsed jobs.</summary>
        group.MapPost("/detect", (DetectSourceRequest body, SourceHttp http, SourceDetector detector, CancellationToken ct) =>
            Api.Guard(async () =>
            {
                if (string.IsNullOrWhiteSpace(body.Url))
                    throw new ArgumentException("Paste the URL of the job board or feed.");

                var detection = await detector.DetectAsync(http, body.Url.Trim(), body.Query, body.Location, ct).ConfigureAwait(false);
                return Api.Ok(new DetectSourceResponse(
                    detection.Url, detection.SuggestedType, detection.Confidence, detection.FeedUrl,
                    detection.Candidates, detection.Preview, detection.Notes, true, null));
            }));

        /// <summary>Dry run against an unsaved source definition.</summary>
        group.MapPost("/test", (TestSourceRequest body, SourceHttp http, SourceRegistry registry, CancellationToken ct) =>
            Api.Guard(async () =>
            {
                var started = System.Diagnostics.Stopwatch.StartNew();

                SourceDto source;
                if (!string.IsNullOrWhiteSpace(body.Id))
                {
                    source = registry.Get(body.Id) ?? throw new KeyNotFoundException($"No source called '{body.Id}'.");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(body.Url))
                        throw new ArgumentException("Give either an id of a saved source, or a url to test.");
                    source = new SourceDto("", "test", body.Type ?? "rss", body.Url, null, Array.Empty<string>(), false,
                        0, body.FieldMap ?? new Dictionary<string, string>(), body.Selectors ?? new Dictionary<string, string>(), "", "", "");
                }

                if (body.FieldMap is { Count: > 0 }) source = source.WithFieldMap(body.FieldMap);
                if (body.Selectors is { Count: > 0 }) source = source.WithSelectors(body.Selectors);

                if (source.Type == "link")
                {
                    var url = SourceRegistry.SearchUrlFor(source, body.Query, body.Location);
                    return Api.Ok(new TestSourceResponse(true, "link", 0, Array.Empty<SourcePreviewItem>(), started.ElapsedMilliseconds, false, null,
                        $"Not fetched. This is a link source: open {url} in your browser and save the posting with POST /api/jobs."));
                }

                var url2 = SourceRegistry.SearchUrlFor(source, body.Query, body.Location);
                var robots = await http.IsAllowedAsync(Uri.TryCreate(url2, UriKind.Absolute, out var u) ? u : null, ct).ConfigureAwait(false);
                var fetched = await http.GetAsync(url2, source.RateLimitMs, body.BypassCache, ct).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(fetched.RobotsNote))
                    return Api.Ok(new TestSourceResponse(false, source.Type, 0, Array.Empty<SourcePreviewItem>(),
                        started.ElapsedMilliseconds, fetched.FromCache, fetched.RobotsNote, robots.Reason));

                if (string.IsNullOrWhiteSpace(fetched.Body))
                    return Api.Ok(new TestSourceResponse(false, source.Type, 0, Array.Empty<SourcePreviewItem>(),
                        started.ElapsedMilliseconds, fetched.FromCache, $"No content returned (status {fetched.Status}).", robots.Reason));

                var jobs = SourceParser.ParseAsync(source, fetched.Body, url2, ct);
                var preview = jobs.Take(20)
                    .Select(j => new SourcePreviewItem(j.Title, j.Company, j.Location, j.Url, j.PostedAt ?? "", Snippet(j.Description), 0))
                    .ToList();

                return Api.Ok(new TestSourceResponse(jobs.Count > 0, source.Type, jobs.Count, preview,
                    started.ElapsedMilliseconds, fetched.FromCache,
                    jobs.Count > 0 ? null : "The page fetched fine but no listings were parsed. Check the field mapping or CSS selectors.", robots.Reason));
            }));

        // ---- Packs ----------------------------------------------------------

        group.MapGet("/packs/all", (SourceRegistry registry) => Api.Guard(() => Task.FromResult(Api.Ok(registry.ListPacks()))));

        group.MapPost("/packs/{packId}/install", (string packId, SourceRegistry registry) =>
            Api.Guard(() => Task.FromResult(Api.Ok(registry.InstallPack(packId)))));

        group.MapDelete("/packs/{packId}", (string packId, SourceRegistry registry) =>
            Api.Guard(() => Task.FromResult(registry.UninstallPack(packId)
                ? Api.Ok(new { removed = true })
                : Api.Error(StatusCodes.Status404NotFound, "not_found", $"No source pack called '{packId}'."))));
    }

private static string Snippet(string text)
    {
        var flat = text.Replace("\n", " ").Trim();
        return flat.Length <= 240 ? flat : flat[..240] + "...";
    }
}