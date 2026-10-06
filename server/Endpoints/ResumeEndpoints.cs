using ResuClean;
using ResuClean.Data;
using ResuClean.Models;
using ResuClean.Services;
using ResuClean.Services.Kits;
using ResuClean.Services.Sources;

namespace ResuClean.Endpoints;

public static class ResumeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/resumes").WithTags("Resumes");

        group.MapGet("/", (ResumeStore store, HttpRequest request) =>
            Api.Guard(() => Task.FromResult(Api.Ok(store.List(Api.Page(request), Api.Size(request))))));

        group.MapGet("/{id}", (string id, ResumeStore store) =>
            Api.Guard(() =>
            {
                var detail = store.Get(id);
                return Task.FromResult(detail is null
                    ? Api.Error(StatusCodes.Status404NotFound, "not_found", $"No resume called '{id}'.")
                    : Api.Ok(detail));
            }));

        group.MapPost("/", (HttpRequest request, ResumeStore store, Configuration.AppConfig app) =>
            Api.Guard(async () =>
            {
                var (text, label, sourceKind, labelFromRequest) = await ReadIntakeAsync(request, store, app).ConfigureAwait(false);
                var (resumeId, versionId) = store.Create(label, text, sourceKind);
                return Api.Created(new CreateResumeResponse(resumeId, versionId, text, sourceKind != "paste"), labelFromRequest);
            }));

        group.MapGet("/{id}/versions", (string id, ResumeStore store) =>
            Api.Guard(() =>
            {
                var detail = store.Get(id);
                return Task.FromResult(detail is null
                    ? Api.Error(StatusCodes.Status404NotFound, "not_found", $"No resume called '{id}'.")
                    : Api.Ok(detail.Versions));
            }));

        group.MapGet("/versions/{versionId}/text", (string versionId, ResumeStore store) =>
            Api.Guard(() =>
            {
                var text = store.GetText(versionId);
                return Task.FromResult(text is null
                    ? Api.Error(StatusCodes.Status404NotFound, "not_found", "That version no longer exists.")
                    : Api.Ok(new { versionId, text }));
            }));

        group.MapPost("/{id}/versions", (string id, HttpRequest request, ResumeStore store) =>
            Api.Guard(async () =>
            {
                var payload = await request.ReadFromJsonAsync<ResumeModels.NewVersionRequest>(Api.Json).ConfigureAwait(false)
                    ?? throw new ArgumentException("A body with label and text is required.");
                var versionId = store.AddVersion(id, payload.ParentVersionId ?? "", payload.Label, payload.Text, payload.SourceKind ?? "paste");
                return Api.Created(new { versionId }, $"/api/resumes/{id}/versions/{versionId}");
            }));

        group.MapPost("/versions/{versionId}/best-fit", (string versionId, ResumeStore store) =>
            Api.Guard(() => Task.FromResult(store.SetBestFit(versionId, true)
                ? Api.Ok(new { versionId, bestFit = true })
                : Api.Error(StatusCodes.Status404NotFound, "not_found", "That version no longer exists."))));

        group.MapDelete("/{id}", (string id, ResumeStore store) =>
            Api.Guard(() => Task.FromResult(store.DeleteResume(id)
                ? Api.Ok(new { deleted = true })
                : Api.Error(StatusCodes.Status404NotFound, "not_found", $"No resume called '{id}'."))));

        // ---- Export ---------------------------------------------------------

        group.MapPost("/versions/{versionId}/export", (string versionId, ExportRequest body, ResumeStore store, CancellationToken ct) =>
            Api.Guard(() =>
            {
                var format = (body.Format ?? "pdf").ToLowerInvariant();
                if (format is not ("pdf" or "docx"))
                    throw new ArgumentException("Format must be 'pdf' or 'docx'.");
                var (docx, pdf) = store.EnsureDocuments(versionId, store.GetLabel(versionId) ?? "resume");
                return Task.FromResult(format == "pdf"
                    ? Api.Download(pdf!, $"resume.{format}", "application/pdf")
                    : Api.Download(docx!, $"resume.{format}", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
            }));

        // ---- Clean / ATS / Update ------------------------------------------

        group.MapPost("/versions/{versionId}/clean", (string versionId, CleanRequest body, OpRunner ops, ResumeWorkflows workflows) =>
            Api.Guard(() => Task.FromResult(ApiOps.AcceptedRun(ops, "clean", body,
                async (payload, ct) => (object?)(await workflows.CleanAsync(versionId, payload, ct).ConfigureAwait(false)).Result))));

        group.MapPost("/versions/{versionId}/ats", (string versionId, AtsRequest body, OpRunner ops, ResumeWorkflows workflows) =>
            Api.Guard(() => Task.FromResult(ApiOps.AcceptedRun(ops, "ats", body,
                async (payload, ct) => (object?)await workflows.AtsAsync(versionId, payload, ct).ConfigureAwait(false)))));

        group.MapPost("/versions/{versionId}/update", (string versionId, UpdateResumeRequest body, OpRunner ops, ResumeWorkflows workflows) =>
            Api.Guard(() => Task.FromResult(ApiOps.AcceptedRun(ops, "update_resume", body,
                async (payload, ct) => (object?)await workflows.UpdateAsync(versionId, payload, ct).ConfigureAwait(false)))));
    }

    private static async Task<(string Text, string Label, string SourceKind, string LabelHint)> ReadIntakeAsync(
        HttpRequest request, ResumeStore store, Configuration.AppConfig app)
    {
        var contentType = request.ContentType ?? string.Empty;

        if (contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            var form = await request.ReadFormAsync().ConfigureAwait(false);
            var file = form.Files.FirstOrDefault();
            if (file is null) throw new ArgumentException("Attach a .txt, .docx or .pdf file, or paste the text in the 'text' field.");
            if (file.Length > app.MaxUploadBytes)
                throw new ApiException(StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                    $"That file is {file.Length / 1024 / 1024} MB. The limit is {app.MaxUploadBytes / 1024 / 1024} MB.");

            var temp = Path.Combine(store.UploadsDir, $"{ProfileService.NewId("up")}{Path.GetExtension(file.FileName)}");
            Directory.CreateDirectory(store.UploadsDir);
            await using (var output = File.Create(temp))
                await file.CopyToAsync(output).ConfigureAwait(false);

            var extracted = Extractor.Extract(file.FileName, File.OpenRead(temp));
            File.Delete(temp);

            var kind = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
            var label = form["label"].FirstOrDefault();
            return (extracted.Text, string.IsNullOrWhiteSpace(label) ? Path.GetFileNameWithoutExtension(file.FileName) : label!, kind, label ?? "");
        }

        var body = await request.ReadFromJsonAsync<CreateResumeRequest>(Api.Json).ConfigureAwait(false)
            ?? throw new ArgumentException("A JSON body with text is required.");
        if (string.IsNullOrWhiteSpace(body.Text))
            throw new ArgumentException("Paste your resume text, or upload a .txt, .docx or .pdf file.");
        if (body.Text.Length > 2_000_000)
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "That text is too large. Split it or upload a file.");

        var lbl = string.IsNullOrWhiteSpace(body.Label) ? "Resume" : body.Label.Trim();
        return (body.Text, lbl, "paste", lbl);
    }
}

public static class ApiOps
{
    /// <summary>Wraps a long operation so every endpoint answers 202 + opId consistently.</summary>
    public static IResult AcceptedRun<T>(OpRunner ops, string kind, T payload, Func<T, CancellationToken, Task<object?>> work)
    {
        var accepted = ops.Enqueue(kind, payload, work);
        return Results.Json(accepted, Api.Json, statusCode: StatusCodes.Status202Accepted);
    }
}

public static class ResumeModels
{
    public sealed record NewVersionRequest(string Label, string Text, string? SourceKind, string? ParentVersionId);
}