using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services.Sources;

/// <summary>The source registry. Sources live in the database, not in code.</summary>
public sealed class SourceRegistry
{
    private readonly Db _db;

    public SourceRegistry(Db db) => _db = db;

    private static string Now() => DateTime.UtcNow.ToString("O");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public List<SourceDto> List(bool enabledOnly = false, string? region = null)
    {
        using var conn = _db.Open();
        var rows = Db.Query(conn,
            "SELECT id, name, type, url, url_template, regions, enabled, rate_limit_ms, field_map, selectors, requires_key, notes, created_at " +
            "FROM job_sources" + (enabledOnly ? " WHERE enabled = 1" : "") + " ORDER BY name",
            r => new SourceDto(
                Db.Str(r, "id"), Db.Str(r, "name"), Db.Str(r, "type"),
                Db.StrOrNull(r, "url"), Db.StrOrNull(r, "url_template"),
                StringList(Db.Str(r, "regions")), Db.Bool(r, "enabled"),
                Db.Int(r, "rate_limit_ms"), Dict(Db.Str(r, "field_map")), Dict(Db.Str(r, "selectors")),
                Db.Str(r, "requires_key"), Db.Str(r, "notes"), Db.Str(r, "created_at")),
            Array.Empty<SqliteParameter>());

        if (string.IsNullOrWhiteSpace(region)) return rows;
        return rows.Where(s => s.Regions.Any(r => r.Equals(region, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public SourceDto? Get(string id)
    {
        using var conn = _db.Open();
        return Db.QueryOne(conn,
            "SELECT id, name, type, url, url_template, regions, enabled, rate_limit_ms, field_map, selectors, requires_key, notes, created_at FROM job_sources WHERE id = @id",
            r => new SourceDto(
                Db.Str(r, "id"), Db.Str(r, "name"), Db.Str(r, "type"),
                Db.StrOrNull(r, "url"), Db.StrOrNull(r, "url_template"),
                StringList(Db.Str(r, "regions")), Db.Bool(r, "enabled"),
                Db.Int(r, "rate_limit_ms"), Dict(Db.Str(r, "field_map")), Dict(Db.Str(r, "selectors")),
                Db.Str(r, "requires_key"), Db.Str(r, "notes"), Db.Str(r, "created_at")),
            Db.P("@id", id));
    }

    public SourceDto Save(UpsertSourceRequest request)
    {
        var type = (request.Type ?? "rss").Trim().ToLowerInvariant();
        if (type is not ("rss" or "atom" or "json" or "html" or "link"))
            throw new ArgumentException($"Unknown source type '{type}'. Use rss, json, html or link.");

        var name = string.IsNullOrWhiteSpace(request.Name) ? DeriveName(request.Url, request.UrlTemplate) : request.Name.Trim();
        var now = Now();
        var id = request.Id ?? ProfileService.NewId("src");
        var regions = JsonSerializer.Serialize(request.Regions ?? new List<string>());
        var fieldMap = JsonSerializer.Serialize(request.FieldMap ?? new Dictionary<string, string>());
        var selectors = JsonSerializer.Serialize(request.Selectors ?? new Dictionary<string, string>());

        using var conn = _db.Open();
        var exists = Db.Scalar(conn, "SELECT 1 FROM job_sources WHERE id = @id", Db.P("@id", id)) != null;
        if (exists)
        {
            Db.Exec(conn,
                "UPDATE job_sources SET name=@n, type=@t, url=@u, url_template=@ut, regions=@rg, enabled=@e, rate_limit_ms=@rl, field_map=@fm, selectors=@se, requires_key=@rk, notes=@no, updated_at=@now WHERE id=@id",
                Db.P("@n", name), Db.P("@t", type), Db.P("@u", request.Url), Db.P("@ut", request.UrlTemplate),
                Db.P("@rg", regions), Db.P("@e", request.Enabled == false ? 0 : 1), Db.P("@rl", request.RateLimitMs ?? 2000),
                Db.P("@fm", fieldMap), Db.P("@se", selectors), Db.P("@rk", request.RequiresKey ?? ""),
                Db.P("@no", request.Notes ?? ""), Db.P("@now", now), Db.P("@id", id));
        }
        else
        {
            Db.Exec(conn,
                "INSERT INTO job_sources (id, name, type, url, url_template, regions, enabled, rate_limit_ms, field_map, selectors, requires_key, notes, created_at, updated_at) " +
                "VALUES (@id, @n, @t, @u, @ut, @rg, @e, @rl, @fm, @se, @rk, @no, @now, @now)",
                Db.P("@id", id), Db.P("@n", name), Db.P("@t", type), Db.P("@u", request.Url), Db.P("@ut", request.UrlTemplate),
                Db.P("@rg", regions), Db.P("@e", request.Enabled == false ? 0 : 1), Db.P("@rl", request.RateLimitMs ?? 2000),
                Db.P("@fm", fieldMap), Db.P("@se", selectors), Db.P("@rk", request.RequiresKey ?? ""),
                Db.P("@no", request.Notes ?? ""), Db.P("@now", now));
        }

        return Get(id) ?? throw new InvalidOperationException("The source could not be read back after saving.");
    }

    public bool Delete(string id)
    {
        using var conn = _db.Open();
        return Db.Execute(conn, "DELETE FROM job_sources WHERE id = @id", Db.P("@id", id)) > 0;
    }

    public bool SetEnabled(string id, bool enabled)
    {
        using var conn = _db.Open();
        return Db.Execute(conn, "UPDATE job_sources SET enabled = @e, updated_at = @now WHERE id = @id",
            Db.P("@e", enabled ? 1 : 0), Db.P("@now", Now()), Db.P("@id", id)) > 0;
    }

    /// <summary>Builds the URL to open when the type is 'link' and the tool must not fetch it.</summary>
    public static string SearchUrlFor(SourceDto source, string? query, string? location)
    {
        var template = source.UrlTemplate ?? source.Url ?? string.Empty;
        return SourceDetector.Compose(template, query, location);
    }

    private static string DeriveName(string? url, string? template)
    {
        var value = url ?? template ?? "Untitled source";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) return uri.Host.Replace("www.", string.Empty);
        return value.Length > 40 ? value[..40] : value;
    }

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

    private static Dictionary<string, string> Dict(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    // ---- Packs -------------------------------------------------------------

    public List<SourcePackDto> ListPacks()
    {
        var packs = SeedPacks.All;
        var installed = new HashSet<string>(StringComparer.Ordinal);
        using var conn = _db.Open();
        foreach (var id in Db.Query(conn, "SELECT id FROM source_packs WHERE id IS NOT NULL", r => Db.Str(r, "id"), Array.Empty<SqliteParameter>()))
            installed.Add(id);

        return packs.Select(p => new SourcePackDto(
            p.Id, p.Name, p.Description, installed.Contains(p.Id),
            p.Entries.Select(e => new PackEntry(e.Key, e.Name, e.Type, e.Url, e.UrlTemplate, e.Regions,
                    e.EnabledByDefault, e.RateLimitMs, e.FieldMap, e.Selectors, e.RequiresKey, e.Notes)).ToList())).ToList();
    }

    public SourcePackDto InstallPack(string packId, bool? overwrite = null)
    {
        var pack = SeedPacks.All.FirstOrDefault(p => p.Id == packId)
            ?? throw new KeyNotFoundException($"There is no source pack called '{packId}'.");

        var existing = List().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var now = Now();
        var installedNames = new List<string>();

        using var conn = _db.Open();
        var tx = conn.BeginTransaction();
        try
        {
            foreach (var entry in pack.Entries)
            {
                if (overwrite != true && existing.ContainsKey(entry.Name))
                    continue;

                var id = ProfileService.NewId("src");
                Db.Exec(conn,
                    "INSERT INTO job_sources (id, name, type, url, url_template, regions, enabled, rate_limit_ms, field_map, selectors, requires_key, notes, created_at, updated_at) " +
                    "VALUES (@id, @n, @t, @u, @ut, @rg, @e, @rl, @fm, @se, @rk, @no, @now, @now)",
                    Db.P("@id", id), Db.P("@n", entry.Name), Db.P("@t", entry.Type),
                    Db.P("@u", entry.Url), Db.P("@ut", entry.UrlTemplate),
                    Db.P("@rg", JsonSerializer.Serialize(entry.Regions)),
                    Db.P("@e", entry.EnabledByDefault ? 1 : 0),
                    // Each source carries its own politeness floor; some boards block aggressive clients.
                    Db.P("@rl", entry.RateLimitMs > 0 ? entry.RateLimitMs : 2000),
                    Db.P("@fm", JsonSerializer.Serialize(entry.FieldMap)),
                    Db.P("@se", JsonSerializer.Serialize(entry.Selectors)),
                    Db.P("@rk", entry.RequiresKey), Db.P("@no", entry.Notes), Db.P("@now", now));
                installedNames.Add(entry.Name);
            }

            Db.Exec(conn, "INSERT INTO source_packs (id, name, description, installed_at) VALUES (@id, @n, @d, @now) " +
                          "ON CONFLICT(id) DO UPDATE SET installed_at = @now",
                Db.P("@id", pack.Id), Db.P("@n", pack.Name), Db.P("@d", pack.Description), Db.P("@now", now));
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        var result = ListPacks().First(p => p.Id == pack.Id);
        return result;
    }

    public bool UninstallPack(string packId)
    {
        var pack = SeedPacks.All.FirstOrDefault(p => p.Id == packId);
        if (pack is null) return false;
        var names = pack.Entries.Select(e => e.Name).ToArray();
        using var conn = _db.Open();
        var changed = 0;
        foreach (var name in names)
            changed += Db.Execute(conn, "DELETE FROM job_sources WHERE name = @n AND id NOT IN (SELECT source_id FROM jobs)", Db.P("@n", name));
        Db.Execute(conn, "DELETE FROM source_packs WHERE id = @id", Db.P("@id", packId));
        return changed > 0;
    }
}