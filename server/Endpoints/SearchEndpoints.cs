using ResuClean;
using ResuClean.Models;
using ResuClean.Services;
using ResuClean.Services.Search;

namespace ResuClean.Endpoints;

/// <summary>
/// General web search, kept deliberately separate from the job-source pipeline.
///
/// Two things this endpoint must never do:
///   1. write anything to the verified profile. Search output is quoted, not asserted.
///   2. pass through the resume no-fabrication guard. That guard exists to police claims about the
///      user, and search results are made of exactly the numbers and dates it would reject.
/// </summary>
public static class SearchEndpoints
{
    public sealed record WebSearchRequestBody(string Query, string? Location, int? MaxResults);

    public sealed record WebSearchResponse(
        string Answer,
        List<SearchHitDto> Results,
        string Tier,
        bool UsedModel,
        string? Provider,
        string? Model,
        List<string> Notes);

    public sealed record SearchHitDto(string Title, string Url, string Snippet, string Source);

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search").WithTags("Search");

        group.MapPost("/", (WebSearchRequestBody body, WebSearchService search, OpRunner ops) =>
            Api.Guard(() =>
            {
                if (string.IsNullOrWhiteSpace(body?.Query))
                    return Task.FromResult(Api.Error(StatusCodes.Status400BadRequest, "bad_request", "Give a query to search for."));

                var request = new WebSearchRequest(
                    body!.Query,
                    body.Location,
                    Math.Clamp(body.MaxResults ?? 8, 1, 20));

                // Long-running like a job search: a model that searches can take many seconds.
                return Task.FromResult(ApiOps.AcceptedRun(ops, "web_search", request,
                    async (payload, ct) =>
                    {
                        var answer = await search.SearchAsync(payload, ct).ConfigureAwait(false);
                        return (object?)new WebSearchResponse(
                            answer.Answer,
                            answer.Results.Select(r => new SearchHitDto(r.Title, r.Url, r.Snippet, r.Source)).ToList(),
                            answer.Tier,
                            answer.UsedModel,
                            answer.Provider,
                            answer.Model,
                            answer.Notes?.ToList() ?? new List<string>());
                    }));
            }));

        /// <summary>Which search tiers are available right now, without spending anything.</summary>
        group.MapGet("/capabilities", (WebSearchService search) =>
            Api.Guard(() =>
            {
                var caps = search.Capabilities();
                return Task.FromResult(Api.Ok(new
                {
                    caps.NativeConfigured,
                    caps.SearchApiConfigured,
                    caps.SearchApiName,
                    CanSearch = caps.NativeConfigured || caps.SearchApiConfigured
                }));
            }));
    }
}