using System.Text.Json;
using ResuClean;
using ResuClean.Configuration;
using ResuClean.Data;
using ResuClean.Endpoints;
using ResuClean.Services;
using ResuClean.Services.Kits;
using ResuClean.Services.Providers;
using ResuClean.Services.Sources;

// resu-clean: a self-hosted resume toolkit. One process serves the API and the built SPA.
var builder = WebApplication.CreateBuilder(args);

// .env values arrive as environment variables from the npm scripts.
builder.Configuration.AddEnvironmentVariables();

var appConfig = AppConfig.Load(builder.Configuration, builder.Environment.ContentRootPath);
Paths.DataDir = appConfig.DataDir;
Directory.CreateDirectory(appConfig.DataDir);

// Console output stays quiet: no request logging that could echo resume content.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Error);
builder.Logging.AddFilter("Microsoft.Data.Sqlite", LogLevel.Error);

builder.WebHost.UseUrls($"http://{appConfig.Host}:{appConfig.Port}");
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = appConfig.MaxUploadBytes;
    options.Limits.MaxConcurrentConnections = 64;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton(appConfig);
builder.Services.AddSingleton(provider => new Db(appConfig));
builder.Services.AddSingleton<ResumeStore>();
builder.Services.AddSingleton<ProfileService>();
builder.Services.AddSingleton<ProviderService>();
builder.Services.AddSingleton<ModelRouter>();
builder.Services.AddSingleton<SourceRegistry>();
builder.Services.AddSingleton<SourceHttp>();
builder.Services.AddSingleton<SourceDetector>();
builder.Services.AddSingleton<JobSearchService>();
builder.Services.AddSingleton<ApplicationService>();
builder.Services.AddSingleton<ResumeWorkflows>();
builder.Services.AddSingleton<OpRunner>();

// Response caching is deliberately NOT registered: resume bodies are large and personal.
builder.Services.AddResponseCaching(options =>
{
    options.MaximumBodySize = 64 * 1024;
});

var app = builder.Build();

// ---- Auth ------------------------------------------------------------------
// Only enforced when RESUCLEAN_API_KEY is set. Binds to localhost by default.
if (appConfig.AuthRequired)
{
    app.Use(async (ctx, next) =>
    {
        if (!ctx.Request.Path.StartsWithSegments("/api"))
        {
            await next(ctx);
            return;
        }

        var provided = ctx.Request.Headers["X-API-Key"].FirstOrDefault();
        if (!FixedTimeEquals(provided, appConfig.ResolvedApiKey!))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = new { code = "unauthorized", message = "Missing or wrong X-API-Key header." } }, Api.Json);
            return;
        }

        await next(ctx);
    });
}

if (appConfig.CorsOriginList.Length > 0)
{
    app.UseCors(options => options
        .WithOrigins(appConfig.CorsOriginList)
        .AllowAnyHeader()
        .AllowAnyMethod());
}

if (appConfig.Swagger)
{
    // The OpenAPI document is served as JSON; a Swagger UI is deliberately not added because it
    // would need a CDN, and this app must work fully offline. dev-only and optional.
    app.MapGet("/api/openapi.json", () => Results.Json(new
    {
        openapi = "3.0.1",
        info = new { title = "resu-clean", version = SystemEndpoints.Version },
        servers = new[] { new { url = "/" } },
        paths = new Dictionary<string, object>
        {
            ["/api/resumes"] = new { get = "List resumes (paginated)", post = "Create a resume from pasted text or an uploaded .txt/.docx/.pdf" },
            ["/api/resumes/versions/{versionId}/clean"] = new { post = "Deterministic clean, optionally a model rewrite. Returns 202 + opId" },
            ["/api/resumes/versions/{versionId}/ats"] = new { post = "Rule-based ATS score with per-check pass/fail and tip. Returns 202 + opId" },
            ["/api/resumes/versions/{versionId}/update"] = new { post = "Tailor a resume. Returns 202 + opId, and stops if new unapproved info is found" },
            ["/api/profile/pending"] = new { get = "Facts awaiting your approval", post = "Approve or reject" },
            ["/api/sources"] = new { get = "Source registry (paginated by convention)", post = "Add or update a source" },
            ["/api/sources/detect"] = new { post = "Add a source by URL: auto-detect feed/API/HTML/link and preview" },
            ["/api/sources/test"] = new { post = "Dry run a source definition" },
            ["/api/sources/packs/all"] = new { get = "Seed packs you can install" },
            ["/api/jobs/search"] = new { post = "Search all enabled sources. Returns 202 + opId" },
            ["/api/jobs"] = new { get = "Saved jobs (paginated)", post = "Save a job manually by pasting text" },
            ["/api/kits"] = new { post = "Generate an application kit. Returns 202 + opId" },
            ["/api/kits/{id}/download/{kind}"] = new { get = "kind = eml | zip | text | cover" },
            ["/api/tracker"] = new { get = "Application tracker (paginated)", post = "Add an entry", patch = "Update a status" },
            ["/api/providers"] = new { get = "Providers (keys are never returned)", post = "Add a provider" },
            ["/api/routes/{workflow}"] = new { get = "Ordered fallback list", put = "Replace the list" },
            ["/api/ops/{id}"] = new { get = "Poll a long operation" },
            ["/api/capabilities"] = new { get = "Which features need a model" },
            ["/api/metrics/mem"] = new { get = "Live memory reading" }
        },
        note = appConfig.AuthRequired
            ? "Send header X-API-Key on every /api request."
            : "No API key is required; the server binds to localhost."
    }, Api.Json));
}

// ---- Routes ----------------------------------------------------------------
ResumeEndpoints.Map(app);
ProfileEndpoints.Map(app);
SourceEndpoints.Map(app);
JobEndpoints.Map(app);
KitEndpoints.Map(app);
KitEndpoints.MapTracker(app);
ModelEndpoints.Map(app);
SystemEndpoints.Map(app);

app.MapGet("/", () => Results.Redirect("/index.html"));

// ---- Static SPA ------------------------------------------------------------
// npm start and npm run publish place the built Svelte app in wwwroot.
var wwwroot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (Directory.Exists(wwwroot))
{
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(wwwroot) });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(wwwroot),
        OnPrepareResponse = ctx =>
        {
            var path = ctx.File.Name;
            // Hashed assets can be cached hard; index.html must not be.
            ctx.Context.Response.Headers.CacheControl = path == "index.html"
                ? "no-cache"
                : "public,max-age=31536000,immutable";
        }
    });

    // SPA fallback: any non-/api path serves index.html so client routing works on refresh.
    app.MapFallback(async ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsJsonAsync(new { error = new { code = "not_found", message = $"No API route {ctx.Request.Path}." } }, Api.Json);
            return;
        }
        var index = Path.Combine(wwwroot, "index.html");
        if (!File.Exists(index))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync(
                "The web UI has not been built yet. Run `npm run build` (for `npm start`) or `npm run dev` for hot reload.");
            return;
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(index);
    });
}
else
{
    app.MapGet("/", () => Results.Text(
        "resu-clean API is running. The web UI has not been built yet: run `npm run build`, or `npm run dev` for hot reload.",
        "text/plain"));
}

// ---- Start -----------------------------------------------------------------
PdfWriter.Initialise();

app.Lifetime.ApplicationStarted.Register(() =>
{
    var lines = new[]
    {
        $"resu-clean {SystemEndpoints.Version} listening on http://{appConfig.Host}:{appConfig.Port}",
        $"  data        {appConfig.DataDir}",
        $"  auth        {(appConfig.AuthRequired ? "X-API-Key required" : "open (localhost only)")}"
    };
    foreach (var line in lines) Console.WriteLine(line);
});

app.Run();

static bool FixedTimeEquals(string? a, string b)
{
    if (string.IsNullOrEmpty(a)) return false;
    if (a.Length != b.Length) return false;
    var diff = 0;
    for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
    return diff == 0;
}