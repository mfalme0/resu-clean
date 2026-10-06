// Configuration loaded once from appsettings.json + environment variables (RESUCLEAN_*).
// Values come from .env which npm scripts place into the process environment.
namespace ResuClean.Configuration;

public sealed class AppConfig
{
    public int Port { get; init; } = 5177;
    public string Host { get; init; } = "127.0.0.1";
    public string? ApiKey { get; init; }
    public string CorsOrigins { get; init; } = "";
    public bool Swagger { get; init; } = true;
    public long MaxUploadBytes { get; init; } = 10 * 1024 * 1024;
    public int MaxConcurrency { get; init; } = 2;
    public int CacheTtlSeconds { get; init; } = 900;
    public int SourceMinIntervalMs { get; init; } = 2000;
    public string UserAgent { get; init; } = "resu-clean/1.0 (self-hosted resume toolkit)";
    public string DataDir { get; init; } = "data";

    public string? ResolvedApiKey => string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
    public string[] CorsOriginList =>
        CorsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool AuthRequired => ResolvedApiKey is not null;

    public static AppConfig Load(IConfiguration cfg, string contentRoot)
    {
        var dataDir = cfg["RESUCLEAN_DATA_DIR"] ?? "data";
        if (!Path.IsPathRooted(dataDir)) dataDir = Path.Combine(contentRoot, "..", dataDir);
        return new AppConfig
        {
            Port = ParseInt(cfg["RESUCLEAN_PORT"], 5177),
            Host = cfg["RESUCLEAN_HOST"] ?? "127.0.0.1",
            ApiKey = cfg["RESUCLEAN_API_KEY"],
            CorsOrigins = cfg["RESUCLEAN_CORS"] ?? "",
            Swagger = ParseBool(cfg["RESUCLEAN_SWAGGER"], true),
            MaxUploadBytes = ParseLong(cfg["RESUCLEAN_MAX_UPLOAD_MB"], 10) * 1024 * 1024,
            MaxConcurrency = Math.Clamp(ParseInt(cfg["RESUCLEAN_MAX_CONCURRENCY"], 2), 1, 8),
            CacheTtlSeconds = ParseInt(cfg["RESUCLEAN_CACHE_TTL_SECONDS"], 900),
            SourceMinIntervalMs = ParseInt(cfg["RESUCLEAN_SOURCE_MIN_INTERVAL_MS"], 2000),
            UserAgent = cfg["RESUCLEAN_USER_AGENT"] ?? "resu-clean/1.0 (self-hosted resume toolkit)",
            DataDir = Path.GetFullPath(dataDir)
        };
    }

    private static int ParseInt(string? raw, int fallback) =>
        int.TryParse(raw, out var v) ? v : fallback;

    private static long ParseLong(string? raw, long fallback) =>
        long.TryParse(raw, out var v) ? v : fallback;

    private static bool ParseBool(string? raw, bool fallback) =>
        bool.TryParse(raw, out var v) ? v : fallback;
}