using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ResuClean.Configuration;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services.Providers;

/// <summary>Where a stored API key came from. Keys are never returned by any endpoint.</summary>
public sealed record ResolvedKey(string? Value, string Source);

/// <summary>
/// Provider registry and the HTTP layer for model calls. One shared HttpClient for all providers.
/// Supports the Anthropic Messages API and any OpenAI-compatible /chat/completions endpoint
/// (OpenAI, OpenRouter, Groq, Ollama, LM Studio, vLLM, llama.cpp server).
/// </summary>
public sealed class ProviderService
{
    private readonly Db _db;
    private readonly HttpClient _http;

    public ProviderService(Db db, IConfiguration cfg)
    {
        _db = db;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        handler.ConnectTimeout = TimeSpan.FromSeconds(15);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "resu-clean/1.0");
    }

    private static string Now() => DateTime.UtcNow.ToString("O");

    // ---- Registry ----------------------------------------------------------

    public List<ProviderDto> List()
    {
        using var conn = _db.Open();
        return Db.Query(conn,
            "SELECT id, name, type, base_url, env_var, api_key_enc, models, enabled, created_at FROM providers ORDER BY name",
            r =>
            {
                var envVar = Db.Str(r, "env_var");
                var hasStored = !string.IsNullOrEmpty(Db.Str(r, "api_key_enc"));
                var envValue = string.IsNullOrEmpty(envVar) ? null : Environment.GetEnvironmentVariable(envVar);
                return new ProviderDto(
                    Db.Str(r, "id"), Db.Str(r, "name"), Db.Str(r, "type"), Db.Str(r, "base_url"), envVar,
                    hasStored || !string.IsNullOrEmpty(envValue),
                    hasStored ? "stored" : string.IsNullOrEmpty(envValue) ? "none" : "env",
                    StringList(Db.Str(r, "models")), Db.Bool(r, "enabled"), Db.Str(r, "created_at"),
                    hasStored || !string.IsNullOrEmpty(envValue));
            },
            Array.Empty<SqliteParameter>());
    }

    public ProviderDto? Get(string id)
    {
        using var conn = _db.Open();
        return Db.QueryOne(conn,
            "SELECT id, name, type, base_url, env_var, api_key_enc, models, enabled, created_at FROM providers WHERE id = @id",
            r =>
            {
                var envVar = Db.Str(r, "env_var");
                var hasStored = !string.IsNullOrEmpty(Db.Str(r, "api_key_enc"));
                var envValue = string.IsNullOrEmpty(envVar) ? null : Environment.GetEnvironmentVariable(envVar);
                return new ProviderDto(
                    Db.Str(r, "id"), Db.Str(r, "name"), Db.Str(r, "type"), Db.Str(r, "base_url"), envVar,
                    hasStored || !string.IsNullOrEmpty(envValue),
                    hasStored ? "stored" : string.IsNullOrEmpty(envValue) ? "none" : "env",
                    StringList(Db.Str(r, "models")), Db.Bool(r, "enabled"), Db.Str(r, "created_at"),
                    hasStored || !string.IsNullOrEmpty(envValue));
            },
            Db.P("@id", id));
    }

    public async Task<ProviderDto> Save(UpsertProviderRequest request)
    {
        var type = (request.Type ?? "openai").Trim().ToLowerInvariant();
        if (type is not ("anthropic" or "openai"))
            throw new ArgumentException($"Unknown provider type '{type}'. Use 'anthropic' or 'openai'.");
        var name = string.IsNullOrWhiteSpace(request.Name) ? throw new ArgumentException("Provider name is required.") : request.Name.Trim();
        var baseUrl = string.IsNullOrWhiteSpace(request.BaseUrl)
            ? DefaultBaseUrl(type)
            : request.BaseUrl.Trim().TrimEnd('/');

        // A preset supplies a name, type and base URL in one call.
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            var preset = ProviderPresets.Find(request.PresetId ?? string.Empty);
            if (preset is not null)
            {
                if (string.IsNullOrWhiteSpace(request.Name)) name = preset.Name;
                if (string.IsNullOrWhiteSpace(request.BaseUrl)) baseUrl = preset.BaseUrl;
                if (string.IsNullOrWhiteSpace(request.EnvVar)) request = request with { EnvVar = preset.EnvVar };
            }
        }

        var id = request.Id ?? ProfileService.NewId("prv");
        var now = Now();
        var models = request.Models ?? new List<string>();

        using var conn = _db.Open();
        var exists = Db.Scalar(conn, "SELECT 1 FROM providers WHERE id = @id", Db.P("@id", id)) != null;
        var encrypted = string.IsNullOrEmpty(request.ApiKey) ? "" : SecretBox.Protect(request.ApiKey.Trim());

        if (exists)
        {
            Db.Exec(conn,
                "UPDATE providers SET name=@n, type=@t, base_url=@u, env_var=@ev, models=@m, enabled=@e, api_key_enc=CASE WHEN @k='' THEN api_key_enc ELSE @k END WHERE id=@id",
                Db.P("@n", name), Db.P("@t", type), Db.P("@u", baseUrl), Db.P("@ev", request.EnvVar ?? ""),
                Db.P("@m", JsonSerializer.Serialize(models)),
                Db.P("@e", request.Enabled == false ? 0 : 1), Db.P("@k", encrypted), Db.P("@id", id));
        }
        else
        {
            Db.Exec(conn,
                "INSERT INTO providers (id, name, type, base_url, env_var, api_key_enc, models, enabled, created_at) VALUES (@id,@n,@t,@u,@ev,@k,@m,@e,@now)",
                Db.P("@id", id), Db.P("@n", name), Db.P("@t", type), Db.P("@u", baseUrl), Db.P("@ev", request.EnvVar ?? ""),
                Db.P("@k", encrypted), Db.P("@m", JsonSerializer.Serialize(models)),
                Db.P("@e", request.Enabled == false ? 0 : 1), Db.P("@now", now));
        }

        // If no models were given, try to fetch them once. A failure here never fails the save:
        // the user can always type ids by hand or press "Fetch models" later.
        if (models.Count == 0)
        {
            var key = string.IsNullOrEmpty(request.ApiKey) ? null : request.ApiKey.Trim();
            if (string.IsNullOrEmpty(key) && !string.IsNullOrWhiteSpace(request.EnvVar))
                key = Environment.GetEnvironmentVariable(request.EnvVar);
            try
            {
                var discovered = await DiscoverAsync(type, baseUrl, key, request.EnvVar).ConfigureAwait(false);
                if (discovered.Ok && discovered.Models.Count > 0)
                {
                    models = discovered.Models.ToList();
                    Db.Execute(conn, "UPDATE providers SET models = @m WHERE id = @id",
                        Db.P("@m", JsonSerializer.Serialize(models)), Db.P("@id", id));
                }
            }
            catch
            {
                // Best effort only.
            }
        }

        return Get(id) ?? throw new InvalidOperationException("Provider could not be read back after saving.");
    }

    public bool Delete(string id)
    {
        using var conn = _db.Open();
        return Db.Execute(conn, "DELETE FROM providers WHERE id = @id", Db.P("@id", id)) > 0;
    }

    /// <summary>Resolves the key from the stored value first, then the environment variable.</summary>
    public ResolvedKey ResolveKey(ProviderDto provider)
    {
        using var conn = _db.Open();
        var stored = Db.QueryOne(conn, "SELECT api_key_enc FROM providers WHERE id = @id", r => Db.Str(r, "api_key_enc"), Db.P("@id", provider.Id));
        if (!string.IsNullOrEmpty(stored))
        {
            var plain = SecretBox.Unprotect(stored);
            if (!string.IsNullOrEmpty(plain)) return new ResolvedKey(plain, "stored");
        }
        if (!string.IsNullOrEmpty(provider.EnvVar))
        {
            var fromEnv = Environment.GetEnvironmentVariable(provider.EnvVar);
            if (!string.IsNullOrWhiteSpace(fromEnv)) return new ResolvedKey(fromEnv.Trim(), "env:" + provider.EnvVar);
        }
        return new ResolvedKey(null, "none");
    }

    public bool AnyConfigured()
    {
        var list = List();
        return list.Any(p => p.Enabled && p.Reachable);
    }

    // ---- Calls -------------------------------------------------------------

    public sealed record ModelReply(string Text, string Provider, string Model, int PromptTokens, int CompletionTokens);

    /// <summary>Single provider call. Failures throw so the router can fall back.</summary>
    public async Task<ModelReply> CallAsync(ProviderDto provider, string model, string system, string user, CancellationToken ct = default)
    {
        var key = ResolveKey(provider);
        var requiresKey = !IsKeyless(provider);
        if (requiresKey && string.IsNullOrEmpty(key.Value))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no API key. Add one in Models, or set {provider.EnvVar} in .env.");
        if (string.IsNullOrEmpty(model))
            throw new InvalidOperationException($"Provider '{provider.Name}' has no model selected for this workflow.");

        var (url, payload, headers) = provider.Type == "anthropic"
            ? BuildAnthropic(provider.BaseUrl, model, key.Value, system, user)
            : BuildOpenAi(provider.BaseUrl, model, key.Value, system, user);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Deliberately terse: never echo request headers or keys.
            var snippet = body.Length > 400 ? body[..400] : body;
            throw new HttpRequestException($"{provider.Name} returned {(int)response.StatusCode}: {Redact(snippet)}");
        }

        return provider.Type == "anthropic"
            ? ReadAnthropic(body, provider, model)
            : ReadOpenAi(body, provider, model);
    }

    private static bool IsKeyless(ProviderDto provider) =>
        provider.BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
        provider.BaseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        provider.BaseUrl.Contains("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
        provider.BaseUrl.Contains("ollama", StringComparison.OrdinalIgnoreCase);

    private static (string Url, string Payload, Dictionary<string, string> Headers) BuildOpenAi(
        string baseUrl, string model, string? key, string system, string user)
    {
        var url = $"{baseUrl}/chat/completions";
        var payload = JsonSerializer.Serialize(new
        {
            model,
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
            temperature = 0.2,
            max_tokens = 4096
        });
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(key)) headers["Authorization"] = $"Bearer {key}";
        return (url, payload, headers);
    }

    private static (string Url, string Payload, Dictionary<string, string> Headers) BuildAnthropic(
        string baseUrl, string model, string? key, string system, string user)
    {
        var url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? $"{baseUrl}/messages" : $"{baseUrl}/v1/messages";
        var payload = JsonSerializer.Serialize(new
        {
            model,
            max_tokens = 4096,
            temperature = 0.2,
            system,
            messages = new object[] { new { role = "user", content = user } }
        });
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(key))
        {
            headers["x-api-key"] = key;
            headers["anthropic-version"] = "2023-06-01";
        }
        return (url, payload, headers);
    }

    private static ModelReply ReadOpenAi(string body, ProviderDto provider, string model)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var text = string.Empty;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
            {
                text = content.ValueKind == JsonValueKind.String
                    ? content.GetString() ?? string.Empty
                    : string.Join(string.Empty, content.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() ?? "" : ""));
            }
            else if (first.TryGetProperty("text", out var legacy))
            {
                text = legacy.GetString() ?? string.Empty;
            }
        }
        var prompt = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
        var completion = root.TryGetProperty("usage", out var usage2) && usage2.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0;
        return new ModelReply(text, provider.Name, model, prompt, completion);
    }

    private static ModelReply ReadAnthropic(string body, ProviderDto provider, string model)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var sb = new StringBuilder();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
                if (block.TryGetProperty("text", out var t)) sb.Append(t.GetString());
        }
        var prompt = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
        var completion = root.TryGetProperty("usage", out var usage2) && usage2.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
        return new ModelReply(sb.ToString(), provider.Name, model, prompt, completion);
    }

    public async Task<TestProviderResponse> TestAsync(ProviderDto provider, string? model, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var key = ResolveKey(provider);
        if (!IsKeyless(provider) && string.IsNullOrEmpty(key.Value))
            return new TestProviderResponse(false, $"No API key found. Add one in Models or set {provider.EnvVar} in .env.", Array.Empty<string>(), started.ElapsedMilliseconds, "missing_key");

        try
        {
            // A tiny call is the only honest test: it proves the key, the URL and the model name.
            var reply = await CallAsync(provider, model ?? provider.Models.FirstOrDefault() ?? "gpt-4o-mini",
                "You are a connectivity test. Reply with exactly: OK", "ping", ct).ConfigureAwait(false);
            return new TestProviderResponse(true, $"Responded in {started.ElapsedMilliseconds} ms: \"{Truncate(reply.Text, 40)}\"",
                provider.Models, started.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException)
        {
            return new TestProviderResponse(false, "Timed out after 120 seconds.", Array.Empty<string>(), started.ElapsedMilliseconds, "timeout");
        }
        catch (Exception ex)
        {
            return new TestProviderResponse(false, ex.Message, Array.Empty<string>(), started.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    public static string DefaultBaseUrl(string type) => type == "anthropic"
        ? "https://api.anthropic.com"
        : "https://api.openai.com/v1";

    // ---- Model discovery ---------------------------------------------------

    /// <summary>
    /// Fetches the model list a provider offers, so the user can pick from real ids instead of
    /// guessing. Handles the three shapes in the wild: OpenAI-style {data:[{id}]}, Gemini's
    /// {data:[{id:"models/..."}]}, and local servers that answer with {data:[{name:...}]}.
    /// </summary>
    public async Task<DiscoverModelsResponse> DiscoverAsync(
        string type, string baseUrl, string? apiKey, string? envVar, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var url = baseUrl.Trim().TrimEnd('/');

        if (url.Length == 0)
            return new DiscoverModelsResponse(false, Array.Empty<string>(), "", 0, "No base URL to query.", "Fill in the base URL first.");

        string? key = apiKey;
        if (string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(envVar))
            key = Environment.GetEnvironmentVariable(envVar);

        var requiresKey = !IsLocalUrl(url);
        if (requiresKey && string.IsNullOrEmpty(key))
            return new DiscoverModelsResponse(false, Array.Empty<string>(), "", 0, "No API key available.",
                string.IsNullOrWhiteSpace(envVar)
                    ? "Add a key in the form, or set an environment variable."
                    : $"Set {envVar} in .env, or add a key in the form.");

        var modelsUrl = type == "anthropic" && !url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? $"{url}/v1/models"
            : $"{url}/models";

        using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        if (type == "anthropic")
        {
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", key);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
        }
        else if (!string.IsNullOrEmpty(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        string body;
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = body.Length > 300 ? body[..300] : body;
                return new DiscoverModelsResponse(false, Array.Empty<string>(), modelsUrl, started.ElapsedMilliseconds,
                    $"{(int)response.StatusCode} from {modelsUrl}: {Redact(snippet)}",
                    response.StatusCode switch
                    {
                        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                            "The key was rejected or is missing a read scope for the model list.",
                        System.Net.HttpStatusCode.NotFound =>
                            "That base URL has no /models endpoint. Check the URL, or type the model ids by hand.",
                        _ => null
                    });
            }
        }
        catch (OperationCanceledException)
        {
            return new DiscoverModelsResponse(false, Array.Empty<string>(), modelsUrl, started.ElapsedMilliseconds, "Timed out.", null);
        }
        catch (Exception ex)
        {
            return new DiscoverModelsResponse(false, Array.Empty<string>(), modelsUrl, started.ElapsedMilliseconds, ex.Message,
                IsLocalUrl(url) ? "Is the local server running, and is that the right port?" : null);
        }

        var models = ParseModelList(body, url);
        if (models.Count == 0)
            return new DiscoverModelsResponse(false, Array.Empty<string>(), modelsUrl, started.ElapsedMilliseconds,
                "The request succeeded but no model ids were found in the response.",
                "Add the model ids by hand if this provider does not publish a list.");

        return new DiscoverModelsResponse(true, models, modelsUrl, started.ElapsedMilliseconds, null, null);
    }

    public Task<DiscoverModelsResponse> DiscoverSavedAsync(ProviderDto provider, CancellationToken ct = default)
    {
        var key = ResolveKey(provider);
        return DiscoverAsync(provider.Type, provider.BaseUrl, key.Value, string.IsNullOrEmpty(key.Value) ? provider.EnvVar : null, ct);
    }

    /// <summary>Turns a provider's model-list response into a clean, sorted, de-duplicated id list.</summary>
    public static List<string> ParseModelList(string body, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(body)) return new List<string>();

        List<string> raw;
        try
        {
            using var doc = JsonDocument.Parse(body);
            raw = ReadIds(doc.RootElement);
        }
        catch (JsonException)
        {
            return new List<string>();
        }

        var stripModelsPrefix = baseUrl.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);

        var models = new List<string>(raw.Count);
        foreach (var id in raw)
        {
            var value = id.Trim();
            if (value.Length == 0) continue;
            if (stripModelsPrefix && value.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                value = value["models/".Length..];
            if (!IsChatCapable(value)) continue;
            models.Add(value);
        }

        return models.Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ReadIds(JsonElement root)
    {
        var ids = new List<string>();
        // OpenAI / Groq / OpenRouter / Clean APIs / local servers: { "data": [ { "id": ... } ] }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) ids.Add(id.GetString() ?? "");
                // Ollama's compatibility layer answers with "name" on some versions.
                else if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) ids.Add(name.GetString() ?? "");
            }
            return ids;
        }

        // Gemini's native listing: { "models": [ { "name": "models/gemini-..." } ] }
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in models.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) ids.Add(name.GetString() ?? "");
                else if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) ids.Add(id.GetString() ?? "");
            }
            return ids;
        }

        // A bare array of strings, just in case.
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) ids.Add(item.GetString() ?? "");
        }

        return ids;
    }

    /// <summary>Drops embeddings, image, audio and rerank models: only chat models can be routed.</summary>
    public static bool IsChatCapable(string modelId)
    {
        var id = modelId.ToLowerInvariant();
        foreach (var marker in new[]
        {
            "embed", "embedding", "rerank", "whisper", "tts", "imagen", "veo", "audiogen", "live-api",
            "text-embedding", "moderation", "guard", "vision-only", "clip", "whisper-large"
        })
        {
            if (id.Contains(marker, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool IsLocalUrl(string baseUrl) =>
        baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
        baseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        baseUrl.Contains("0.0.0.0", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static string Redact(string text) =>
        Regex.Replace(text, @"(?i)(api[_-]?key|authorization|bearer|x-api-key)[""']?\s*[:=]\s*[""']?[\w\-\.]{8,}", "$1:[redacted]");

    private static List<string> StringList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}

/// <summary>
/// API keys at rest. On Windows this uses DPAPI for the current user; elsewhere it is AES-256
/// with a machine-local key file under data/. The key never leaves the box and is never returned
/// over HTTP.
/// </summary>
public static class SecretBox
{
    private const string PrefixWin = "dpapi:";
    private const string PrefixAes = "aes:";

    private static readonly object Gate = new();
    private static byte[]? _machineKey;

    public static string Protect(string plain)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), optionalEntropy: null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return PrefixWin + Convert.ToBase64String(protectedBytes);
            }
            catch (Exception)
            {
                // Fall through to AES so key storage never silently breaks.
            }
        }
        var iv = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var cipher = AesEncrypt(MachineKey(), iv, Encoding.UTF8.GetBytes(plain));
        return PrefixAes + Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(cipher);
    }

    public static string Unprotect(string stored)
    {
        try
        {
            if (stored.StartsWith(PrefixWin, StringComparison.Ordinal) && OperatingSystem.IsWindows())
            {
                var bytes = Convert.FromBase64String(stored[PrefixWin.Length..]);
                var plain = System.Security.Cryptography.ProtectedData.Unprotect(bytes, optionalEntropy: null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            if (stored.StartsWith(PrefixAes, StringComparison.Ordinal))
            {
                var parts = stored[PrefixAes.Length..].Split(':');
                var iv = Convert.FromBase64String(parts[0]);
                var cipher = Convert.FromBase64String(parts[1]);
                return Encoding.UTF8.GetString(AesDecrypt(MachineKey(), iv, cipher));
            }
        }
        catch
        {
            return string.Empty;
        }
        return string.Empty;
    }

    private static byte[] MachineKey()
    {
        lock (Gate)
        {
            if (_machineKey is not null) return _machineKey;
            var keyPath = Path.Combine(Paths.DataDir, "machine.key");
            if (File.Exists(keyPath))
            {
                _machineKey = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
                return _machineKey;
            }
            Directory.CreateDirectory(Paths.DataDir);
            _machineKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(keyPath, Convert.ToBase64String(_machineKey));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return _machineKey;
        }
    }

    private static byte[] AesEncrypt(byte[] key, byte[] iv, byte[] plain)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        return aes.CreateEncryptor().TransformFinalBlock(plain, 0, plain.Length);
    }

    private static byte[] AesDecrypt(byte[] key, byte[] iv, byte[] cipher)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        return aes.CreateDecryptor().TransformFinalBlock(cipher, 0, cipher.Length);
    }
}

public static class Paths
{
    public static string DataDir { get; set; } = "data";
}