using ResuClean;
using ResuClean.Models;
using ResuClean.Services;

namespace ResuClean.Endpoints;

/// <summary>
/// Webhook configuration and the daily digest.
///
/// The settings live behind their own endpoints rather than under /config, because a webhook URL is
/// not a runtime setting — it is a destination somebody chose, and it should be possible to see,
/// change, disable and delete one without touching how the app is configured to run.
/// </summary>
public static class WebhookEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webhooks").WithTags("Webhooks");

        group.MapGet("/", (WebhookService hooks) =>
            Api.Guard(() => Task.FromResult(Api.Ok(hooks.List()))));

        group.MapGet("/{id}", (string id, WebhookService hooks) =>
            Api.Guard(() => Task.FromResult(
                hooks.Get(id) is { } hook ? Api.Ok(hook) : Api.Error(StatusCodes.Status404NotFound, "not_found", $"No webhook called '{id}'."))));

        group.MapPost("/", (WebhookRequest body, WebhookService hooks) =>
            Api.Guard(() => Task.FromResult(Api.Ok(hooks.Save(body)))));

        group.MapDelete("/{id}", (string id, WebhookService hooks) =>
            Api.Guard(() => Task.FromResult(hooks.Delete(id)
                ? Api.Ok(new { deleted = true })
                : Api.Error(StatusCodes.Status404NotFound, "not_found", $"No webhook called '{id}'."))));

        /// <summary>
        /// The message this webhook would send right now, and nothing is sent.
        ///
        /// Configurable does not mean guessable: a receiver may want the counts somewhere other
        /// than the first line, and the only way to find that out is to see the real digest before
        /// pointing anything at it.
        /// </summary>
        group.MapPost("/{id}/preview", (string id, WebhookService hooks) =>
            Api.Guard(() =>
            {
                var hook = hooks.Get(id) ?? throw new KeyNotFoundException($"No webhook called '{id}'.");
                var digest = hooks.BuildDigest(hook);
                return Task.FromResult(Api.Ok(new WebhookPreviewDto(
                    digest.Message, digest.NewCount, digest.UrgentCount, digest.Changed.Count,
                    digest.Since.ToString("O"),
                    digest.NewJobs.Concat(digest.Changed).Select(ToDto).ToList())));
            }));

        /// <summary>
        /// Sends the digest now, and moves the cursor forward.
        ///
        /// The cursor only advances on success, so a failed send leaves the day's news queued for
        /// tomorrow rather than dropping it. That is the one behaviour a notification system cannot
        /// get wrong.
        /// </summary>
        group.MapPost("/{id}/send", (string id, WebhookService hooks, CancellationToken ct) =>
            Api.Guard(async () =>
            {
                var hook = hooks.Get(id) ?? throw new KeyNotFoundException($"No webhook called '{id}'.");
                var result = await hooks.SendAsync(hook, ct).ConfigureAwait(false);
                return result.Ok
                    ? Api.Ok(new WebhookSendResultDto(true, result.Message, result.Status, result.Detail, result.ResponseBody))
                    : Api.Error(StatusCodes.Status502BadGateway, "delivery_failed",
                        $"{result.Detail} The digest was not sent and nothing was lost.",
                        new { result.Message, result.ResponseBody });
            }));

        /// <summary>
        /// Sends to every enabled webhook. This is the once-a-day entry point — a cron job, a
        /// scheduler, or the --watch daemon that is still on the list.
        /// </summary>
        group.MapPost("/send-all", async (WebhookService hooks, CancellationToken ct) =>
            await Api.Guard(async () =>
            {
                var results = new List<WebhookSendResultDto>();
                foreach (var hook in hooks.Enabled())
                {
                    var result = await hooks.SendAsync(hook, ct).ConfigureAwait(false);
                    results.Add(new WebhookSendResultDto(result.Ok, result.Message, result.Status, result.Detail, result.ResponseBody));
                }
                return Api.Ok(new { sent = results.Count(r => r.Ok), total = results.Count, results });
            }).ConfigureAwait(false));
    }

    private static WebhookItemDto ToDto(WebhookService.Item item) =>
        new(item.Title, item.Company, item.Url, item.Verdict, item.Deadline, item.Urgent, item.Why);
}