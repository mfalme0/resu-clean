using ResuClean;
using ResuClean.Models;
using ResuClean.Services;
using ResuClean.Services.Providers;

namespace ResuClean.Endpoints;

public static class ModelEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var providers = app.MapGroup("/api/providers").WithTags("Models");

        // Built-in templates. Selecting one fills in the form; nothing is saved.
        providers.MapGet("/presets", () => Api.Guard(() => Task.FromResult(Api.Ok(ProviderPresets.AsDtos()))));

        // Model discovery for a draft that has not been saved yet.
        providers.MapPost("/discover", (DiscoverModelsRequest body, ProviderService service, CancellationToken ct) =>
            Api.Guard(async () => Api.Ok(await service.DiscoverAsync(
                (body.Type ?? "openai").Trim().ToLowerInvariant(),
                body.BaseUrl ?? string.Empty,
                body.ApiKey,
                body.EnvVar,
                ct).ConfigureAwait(false))));

        // Model discovery for a saved provider, using its stored or environment key.
        providers.MapPost("/{id}/models", (string id, ProviderService service, CancellationToken ct) =>
            Api.Guard(async () =>
            {
                var provider = service.Get(id) ?? throw new KeyNotFoundException($"No provider called '{id}'.");
                return Api.Ok(await service.DiscoverSavedAsync(provider, ct).ConfigureAwait(false));
            }));

        // Keys are never returned by any endpoint: only hasKey and keySource.
        providers.MapGet("/", (ProviderService service) => Api.Guard(() => Task.FromResult(Api.Ok(service.List()))));

        providers.MapGet("/{id}", (string id, ProviderService service) =>
            Api.Guard(() =>
            {
                var provider = service.Get(id);
                return Task.FromResult(provider is null
                    ? Api.Error(StatusCodes.Status404NotFound, "not_found", $"No provider called '{id}'.")
                    : Api.Ok(provider));
            }));

        providers.MapPost("/", (UpsertProviderRequest body, ProviderService service) =>
            Api.Guard(async () => Api.Created(await service.Save(body).ConfigureAwait(false), "/api/providers")));

        providers.MapPut("/{id}", (string id, UpsertProviderRequest body, ProviderService service) =>
            Api.Guard(async () => Api.Ok(await service.Save(new UpsertProviderRequest
            {
                Id = id,
                PresetId = body.PresetId,
                Name = body.Name,
                Type = body.Type,
                BaseUrl = body.BaseUrl,
                EnvVar = body.EnvVar,
                ApiKey = body.ApiKey,
                Models = body.Models,
                Enabled = body.Enabled
            }).ConfigureAwait(false))));

        providers.MapDelete("/{id}", (string id, ProviderService service) =>
            Api.Guard(() => Task.FromResult(service.Delete(id)
                ? Api.Ok(new { deleted = true })
                : Api.Error(StatusCodes.Status404NotFound, "not_found", $"No provider called '{id}'."))));

        providers.MapPost("/{id}/test", (string id, TestProviderRequest body, ProviderService service, CancellationToken ct) =>
            Api.Guard(async () =>
            {
                var provider = service.Get(id) ?? throw new KeyNotFoundException($"No provider called '{id}'.");
                return Api.Ok(await service.TestAsync(provider, body.Model, ct).ConfigureAwait(false));
            }));

        // ---- Routing --------------------------------------------------------

        var routes = app.MapGroup("/api/routes").WithTags("Models");

        routes.MapGet("/", (ModelRouter router) => Api.Guard(() =>
            Task.FromResult(Api.Ok(ModelRouter.Workflows.Select(router.GetRoute).ToList()))));

        routes.MapGet("/{workflow}", (string workflow, ModelRouter router) =>
            Api.Guard(() =>
            {
                if (!ModelRouter.Workflows.Contains(workflow))
                    throw new ArgumentException($"Workflow must be one of: {string.Join(", ", ModelRouter.Workflows)}.");
                return Task.FromResult(Api.Ok(router.GetRoute(workflow)));
            }));

        routes.MapPut("/{workflow}", (string workflow, SetRouteRequest body, ModelRouter router) =>
            Api.Guard(() => Task.FromResult(Api.Ok(router.SetRoute(workflow, body)))));

        routes.MapPost("/{workflow}/preview", (string workflow, SetRouteRequest body, ModelRouter router, CancellationToken ct) =>
            Api.Guard(async () =>
            {
                // Test whatever the user typed, whether or not the route has been saved yet.
                var entry = body.Entries.FirstOrDefault();
                return Api.Ok(await router.PreviewAsync(workflow, entry?.ProviderId, entry?.Model, ct).ConfigureAwait(false));
            }));
    }
}