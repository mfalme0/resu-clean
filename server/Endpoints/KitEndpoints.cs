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

        // ---- Export / import -------------------------------------------------

        /// <summary>
        /// The whole tracker as one small JSON file: company slug -> stage, note and dates. It carries
        /// no resume text, no job descriptions and no keys, so it is safe to keep in a notes file.
        ///
        /// The analysis tier is on by default and carries what a person established by reading a
        /// posting: the URL, the posting date, the score, the closing date, and whether the role was
        /// confirmed open and on what date. Pass ?analysis=false for the old narrow shape.
        /// </summary>
        group.MapGet("/export", (bool? analysis, TrackerTransfer transfer) =>
            Api.Guard(() => Task.FromResult(Results.Bytes(
                System.Text.Encoding.UTF8.GetBytes(transfer.ExportJson(analysis ?? true)),
                "application/json", "applications.json"))));

        group.MapGet("/export-text", (bool? analysis, TrackerTransfer transfer) =>
            Api.Guard(() => Task.FromResult(Api.Ok(new { json = transfer.ExportJson(analysis ?? true) }))));

        /// <summary>
        /// Reads that file back in. Entries already tracked are left alone unless overwrite is set, so
        /// re-importing an older file cannot silently reset work you have since moved forward.
        /// </summary>
        group.MapPost("/import", (ImportTrackerRequest body, TrackerTransfer transfer) =>
            Api.Guard(() => Task.FromResult(Api.Ok(transfer.ImportJson(body.Json ?? "", body.Overwrite ?? false)))));

        // ---- Importing positions tracked elsewhere ---------------------------

        /// <summary>
        /// Reads a position file and works out what each entry would become, without writing anything.
        ///
        /// Preview first, always. The extraction is the interesting part — a company, a title, a
        /// location and a posting URL pulled out of a sentence somebody wrote — and it is exactly
        /// the kind of thing a person should see before it lands in their tracker.
        /// </summary>
        group.MapPost("/import-positions/preview", (ImportPositionsRequest body, PositionImport importer) =>
            Api.Guard(() =>
            {
                var preview = importer.Parse(body.Json ?? "");
                return Task.FromResult(Api.Ok(new PositionPreviewDto(
                    preview.Total,
                    preview.Positions.Select(p => new ParsedPositionDto(
                        p.Key, p.Stage, p.Company, p.Title, p.Location, p.Url, p.DeadlineAt,
                        p.Verified, p.Applied, p.Updated, p.Note, p.Missing)).ToList(),
                    preview.Warnings)));
            }));

        /// <summary>
        /// Writes the positions. One job and one application per entry, keyed on the file's own key
        /// so re-running updates rather than duplicating.
        /// </summary>
        group.MapPost("/import-positions", (ImportPositionsRequest body, PositionImport importer) =>
            Api.Guard(() =>
            {
                var result = importer.Import(body.Json ?? "", body.Overwrite ?? false);
                return Task.FromResult(Api.Ok(new PositionImportResultDto(
                    result.Imported, result.Created, result.Updated, result.Skipped, result.Warnings)));
            }));
    }
}