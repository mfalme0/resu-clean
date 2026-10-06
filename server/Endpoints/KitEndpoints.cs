using ResuClean;
using ResuClean.Models;
using ResuClean.Services;

namespace ResuClean.Endpoints;

public static class KitEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/kits").WithTags("Application kits");

        group.MapPost("/", (CreateKitRequest body, OpRunner ops, ApplicationService kits, Configuration.AppConfig app) =>
            Api.Guard(() => Task.FromResult(ApiOps.AcceptedRun(ops, "apply_email", body,
                async (payload, ct) =>
                {
                    var (kit, _) = await kits.CreateKitAsync(payload, app, ct).ConfigureAwait(false);
                    return (object?)kit;
                }))));

        group.MapGet("/{id}", (string id, ApplicationService kits, Configuration.AppConfig app) =>
            Api.Guard(() =>
            {
                var (kit, _) = kits.GetKit(id, app);
                return Task.FromResult(Api.Ok(kit));
            }));

        // Downloads stream from disk; nothing is buffered in memory.
        group.MapGet("/{id}/download/{kind}", (string id, string kind, ApplicationService kits, Configuration.AppConfig app) =>
            Api.Guard(() =>
            {
                var (_, files) = kits.GetKit(id, app);
                return Task.FromResult(kind.ToLowerInvariant() switch
                {
                    "eml" => Api.Download(files.Eml ?? "", $"application-{id}.eml", "message/rfc822"),
                    "zip" => Api.Download(files.Zip ?? "", $"application-kit-{id}.zip", "application/zip"),
                    "text" or "email" => Api.Download(files.EmailText ?? "", $"email-{id}.txt", "text/plain; charset=utf-8"),
                    "cover" => Api.Download(files.CoverPdf ?? "", $"cover-letter-{id}.pdf", "application/pdf"),
                    _ => Api.Error(StatusCodes.Status400BadRequest, "bad_request", "Kind must be eml, zip, text or cover.")
                });
            }));

        group.MapGet("/{id}/mailto", (string id, ApplicationService kits, Configuration.AppConfig app) =>
            Api.Guard(() => Task.FromResult(Api.Ok(new { link = kits.GetKit(id, app).Kit.MailtoLink }))));
    }

    public static void MapTracker(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tracker").WithTags("Tracker");

        group.MapGet("/", (ApplicationService kits, HttpRequest request) =>
            Api.Guard(() => Task.FromResult(Api.Ok(kits.ListTracker(Api.Page(request), Api.Size(request), Api.Param(request, "status"))))));

        group.MapPost("/", (CreateTrackerRequest body, ApplicationService kits) =>
            Api.Guard(() => Task.FromResult(Api.Created(kits.AddTrackerEntry(body), "/api/tracker"))));

        group.MapPatch("/{id}", (string id, PatchTrackerRequest body, ApplicationService kits) =>
            Api.Guard(() => Task.FromResult(Api.Ok(kits.PatchTracker(id, body)))));

        group.MapDelete("/{id}", (string id, ApplicationService kits) =>
            Api.Guard(() => Task.FromResult(kits.DeleteTrackerEntry(id)
                ? Api.Ok(new { deleted = true })
                : Api.Error(StatusCodes.Status404NotFound, "not_found", $"No tracker entry called '{id}'."))));
    }
}