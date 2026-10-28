using ResuClean.Data;
using ResuClean.Models;
using ResuClean.Services;

namespace ResuClean.Endpoints;

public static class AnalyticsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analytics").WithTags("Analytics");

        group.MapGet("/summary", (ApplicationAnalyticsService analytics) =>
            Api.Guard(() => Task.FromResult(Api.Ok(analytics.Summary()))));

        group.MapGet("/by-source", (ApplicationAnalyticsService analytics) =>
            Api.Guard(() => Task.FromResult(Api.Ok(analytics.BySource()))));

        group.MapGet("/monthly", (ApplicationAnalyticsService analytics) =>
            Api.Guard(() => Task.FromResult(Api.Ok(analytics.Monthly()))));

        group.MapGet("/funnel", (ApplicationAnalyticsService analytics) =>
            Api.Guard(() => Task.FromResult(Api.Ok(analytics.Funnel()))));
    }
}
