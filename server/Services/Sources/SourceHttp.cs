using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Data.Sqlite;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// One shared HttpClient for the whole process (connection reuse, back-pressure via max connections).
/// A tiny SQLite cache and per-host rate limiting live here so sources stay cheap and polite.
/// </summary>
public sealed class SourceHttp
{
    public const string CacheNamespace = "resu-clean/1.0 (+self-hosted)";

    private readonly Db _db;
    private readonly Configuration.AppConfig _app;
    private readonly HttpClient _client;
    private readonly Dictionary<string, DateTime> _lastHit = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _robotsGate = new(1, 1);
    private readonly Dictionary<string, RobotsRules> _robotsCache = new(StringComparer.Ordinal);

    public SourceHttp(Db db, Configuration.AppConfig app)
    {
        _db = db;
        _app = app;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli | DecompressionMethods.Deflate,
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5
        };
        handler.ConnectTimeout = TimeSpan.FromSeconds(10);

        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 8 * 1024 * 1024
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(_app.UserAgent);
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }

    public HttpClient Client => _client;

    public sealed record FetchResult(string Body, int Status, bool FromCache, long ElapsedMs, string? RobotsNote);

    public async Task<FetchResult> GetAsync(string url, int? rateLimitMs, bool bypassCache, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var cfg = _app;
        var ttl = TimeSpan.FromSeconds(Math.Max(0, cfg.CacheTtlSeconds));

        if (!bypassCache)
        {
            var cached = ReadCache(url, ttl);
            if (cached != null)
                return new FetchResult(cached.Value.Body, cached.Value.Status, true, started.ElapsedMilliseconds, cached.Value.RobotsNote);
        }

        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "unknown";
        var minInterval = Math.Max(rateLimitMs ?? 0, cfg.SourceMinIntervalMs);
        await RateLimitAsync(host, minInterval, ct).ConfigureAwait(false);

        var robots = await IsAllowedAsync(uri, ct).ConfigureAwait(false);
        if (!robots.Allowed)
            return new FetchResult(string.Empty, 0, false, started.ElapsedMilliseconds,
                $"Not fetched: {robots.Reason} (robots.txt for {host})");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept-Language", "en");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return new FetchResult(string.Empty, (int)response.StatusCode, false, started.ElapsedMilliseconds, null);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var charset = response.Content.Headers.ContentType?.CharSet;
        var body = Decode(bytes, charset);
        WriteCache(url, body, (int)response.StatusCode);
        return new FetchResult(body, (int)response.StatusCode, false, started.ElapsedMilliseconds, null);
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8.GetString(bytes);
        try
        {
            return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private async Task RateLimitAsync(string host, int minIntervalMs, CancellationToken ct)
    {
        if (minIntervalMs <= 0) return;
        var delay = 0;
        lock (_gate)
        {
            if (_lastHit.TryGetValue(host, out var last))
            {
                var elapsed = (DateTime.UtcNow - last).TotalMilliseconds;
                if (elapsed < minIntervalMs) delay = (int)(minIntervalMs - elapsed);
            }
            _lastHit[host] = DateTime.UtcNow.AddMilliseconds(delay);
        }
        if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    // ---- Cache -------------------------------------------------------------

    private (string Body, int Status, string? RobotsNote)? ReadCache(string url, TimeSpan ttl)
    {
        try
        {
            using var conn = _db.Open();
            var row = Db.QueryOne(conn, "SELECT body, status, fetched_at FROM http_cache WHERE url = @u", r => (
                Body: Db.Str(r, "body"), Status: Db.Int(r, "status"), Fetched: Db.Str(r, "fetched_at")), Db.P("@u", url));
            if (row == default) return null;
            if (!DateTime.TryParse(row.Fetched, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)) return null;
            if (DateTime.UtcNow - at > ttl) return null;
            return (row.Body, row.Status, null);
        }
        catch
        {
            return null;
        }
    }

    private void WriteCache(string url, string body, int status)
    {
        try
        {
            using var conn = _db.Open();
            Db.Exec(conn, "INSERT INTO http_cache (url, body, status, fetched_at) VALUES (@u, @b, @s, @t) " +
                          "ON CONFLICT(url) DO UPDATE SET body = @b, status = @s, fetched_at = @t",
                Db.P("@u", url), Db.P("@b", body), Db.P("@s", status), Db.P("@t", DateTime.UtcNow.ToString("O")));
        }
        catch
        {
            // A cache write failure must never break a search.
        }
    }

    // ---- robots.txt --------------------------------------------------------

        public async Task<RobotsRules> IsAllowedAsync(Uri? uri, CancellationToken ct = default)
    {
        if (uri is null) return new RobotsRules();
        var rules = await LoadRobotsAsync(uri, ct).ConfigureAwait(false);
        var path = uri.PathAndQuery.TrimEnd('/');
        path = string.IsNullOrEmpty(path) ? "/" : path;
        var allowed = rules.AllowedPaths.Count == 0 || rules.AllowedPaths.Any(p => path.StartsWith(p, StringComparison.Ordinal));
        if (allowed) return new RobotsRules();

        var disallow = rules.DisallowedPaths.FirstOrDefault(p => path.StartsWith(p, StringComparison.Ordinal)) ?? "/";
        return new RobotsRules { Allowed = false, Reason = $"robots.txt disallows {disallow}" };
    }

    private async Task<RobotsRules> LoadRobotsAsync(Uri uri, CancellationToken ct)
    {
        var origin = $"{uri.Scheme}://{uri.Authority}";
        await _robotsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_robotsCache.TryGetValue(origin, out var cached)) return cached;

            var rules = new RobotsRules();
            var allowed = new List<string>();
            var disallowed = new List<string>();
            try
            {
                var robotsUri = new Uri(new Uri(origin), "/robots.txt");
                using var request = new HttpRequestMessage(HttpMethod.Get, robotsUri);
                request.Headers.TryAddWithoutValidation("User-Agent", _app.UserAgent);
                using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var appliesToUs = false;
                    var lastDirectiveWasUserAgent = false;
                    foreach (var raw in text.Split('\n'))
                    {
                        var line = raw.Split('#')[0].Trim();
                        if (line.Length == 0) continue;
                        var colon = line.IndexOf(':');
                        if (colon <= 0) continue;
                        var field = line[..colon].Trim().ToLowerInvariant();
                        var value = line[(colon + 1)..].Trim();

                        if (field == "user-agent")
                        {
                            if (lastDirectiveWasUserAgent) { appliesToUs = false; }
                            appliesToUs = value == "*" || _app.UserAgent.ToLowerInvariant().Contains(value.ToLowerInvariant());
                            lastDirectiveWasUserAgent = true;
                            continue;
                        }
                        if (!appliesToUs) continue;
                        lastDirectiveWasUserAgent = false;
                        if (field == "disallow" && value.Length > 0) disallowed.Add(value);
                        else if (field == "allow" && value.Length > 0) allowed.Add(value);
                    }
                    // Longest match wins.
                    allowed.Sort((a, b) => b.Length.CompareTo(a.Length));
                    disallowed.Sort((a, b) => b.Length.CompareTo(a.Length));
                    rules = new RobotsRules() { AllowedPaths = allowed, DisallowedPaths = disallowed };
                }
            }
            catch
            {
                // No robots.txt or unreachable: nothing to enforce.
            }

            _robotsCache[origin] = rules;
            return rules;
        }
        finally
        {
            _robotsGate.Release();
        }
    }

    private static string BuildUrl(string template, string? query, string? location)
    {
        var url = template
            .Replace("{query}", Uri.EscapeDataString(query ?? string.Empty))
            .Replace("{location}", Uri.EscapeDataString(location ?? string.Empty));
        return url;
    }

    public static string ComposeUrl(SourceDto source, string? query, string? location) =>
        BuildUrl(source.UrlTemplate ?? source.Url ?? string.Empty, query, location);
}

/// <summary>Minimal robots rule set (longest-match semantics, no full RFC implementation).</summary>
public sealed class RobotsRules
{
    public bool Allowed { get; init; }
    public string Reason { get; init; } = string.Empty;
    public List<string> AllowedPaths { get; init; } = new();
    public List<string> DisallowedPaths { get; init; } = new();
}

