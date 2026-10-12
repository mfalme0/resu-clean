using Microsoft.Data.Sqlite;
using ResuClean.Data;
using ResuClean.Models;
using ResuClean.Services.Sources;

namespace ResuClean.Services;

/// <summary>
/// Runs a job search across the enabled sources, dedupes the results, ranks them against a resume
/// version and stores the survivors. Fetching happens in parallel per source with a global cap.
/// </summary>
public sealed class JobSearchService
{
    private readonly Db _db;
    private readonly SourceRegistry _registry;
    private readonly SourceHttp _http;

    public JobSearchService(Db db, SourceRegistry registry, SourceHttp http)
    {
        _db = db;
        _registry = registry;
        _http = http;
    }

    private static string Now() => DateTime.UtcNow.ToString("O");

    public sealed record Outcome(SearchSummary Summary, IReadOnlyList<JobDto> Jobs);

    public async Task<Outcome> SearchAsync(JobSearchRequest request, CancellationToken ct = default)
    {
        var runId = ProfileService.NewId("run");
        var sources = SelectSources(request);
        var errors = new List<SourceError>();
        var used = new List<string>();
        var skipped = new List<string>();

        var resumeText = LoadResumeText(request.ResumeVersionId);
        var raw = new List<(RawJob Job, SourceDto Source)>();

        // Bounded parallelism keeps RAM and politeness in check.
        using var throttle = new SemaphoreSlim(4);
        var tasks = sources.Select(async source =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await FetchSourceAsync(source, request, ct).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var result in results)
        {
            if (result.Error is not null) { errors.Add(result.Error); continue; }
            if (result.Items.Count == 0) { skipped.Add(result.Source.Name); continue; }
            used.Add(result.Source.Name);
            raw.AddRange(result.Items.Select(job => (job, result.Source)));
        }

        // ---- Filter -------------------------------------------------------
        IEnumerable<(RawJob Job, SourceDto Source)> filtered = raw;
        if (request.RemoteOnly)
            filtered = filtered.Where(x => x.Job.Remote || Dedupe.Normalize(x.Job.Title + " " + x.Job.Location).Contains("remote"));
        if (!string.IsNullOrWhiteSpace(request.Location))
        {
            var needle = request.Location.Trim();
            filtered = filtered.Where(x =>
                Dedupe.Normalize(x.Job.Location).Contains(Dedupe.Normalize(needle)) ||
                Dedupe.Normalize(x.Job.Title + " " + x.Job.Description).Contains(Dedupe.Normalize(needle)) ||
                x.Job.Remote);
        }

        var candidates = filtered.ToList();

        // ---- Dedupe -------------------------------------------------------
        var deduped = new List<(RawJob Job, SourceDto Source)>();
        foreach (var candidate in candidates)
        {
            var clash = deduped.FirstOrDefault(existing =>
                Dedupe.SameUrl(existing.Job.Url, candidate.Job.Url) ||
                Dedupe.Same(existing.Job.Title, existing.Job.Company, candidate.Job.Title, candidate.Job.Company));

            if (clash.Job is null)
            {
                deduped.Add(candidate);
                continue;
            }

            // Keep the richer record, but remember that another board also had it.
            if (candidate.Job.Description.Length > clash.Job.Description.Length ||
                (string.IsNullOrWhiteSpace(clash.Job.Company) && !string.IsNullOrWhiteSpace(candidate.Job.Company)))
            {
                deduped[deduped.IndexOf(clash)] = candidate;
            }
        }

        // ---- Rank ---------------------------------------------------------
        var ranked = deduped
            .Select(x =>
            {
                var pct = string.IsNullOrWhiteSpace(resumeText) ? 0 : Keywords.ScoreAgainst(x.Job.Title + " " + x.Job.Description, resumeText);
                return (Item: x, Pct: pct);
            })
            .OrderByDescending(x => x.Pct)
            .ThenByDescending(x => x.Item.Job.PostedAt ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(x => x.Item.Job.Title, StringComparer.Ordinal)
            .Take(Math.Clamp(request.Limit <= 0 ? 50 : request.Limit, 1, 300))
            .ToList();

        // ---- Persist ------------------------------------------------------
        var saved = new List<JobDto>();
        if (request.SaveResults)
            saved = SaveJobs(ranked.Select(x => (x.Item.Job, x.Item.Source, x.Pct)).ToList(), request.ResumeVersionId);

        var stats = new Dictionary<string, string>
        {
            ["fetched"] = raw.Count.ToString(),
            ["sources"] = string.Join(", ", used),
            ["skipped"] = string.Join(", ", skipped)
        };

        using (var conn = _db.Open())
        {
            Db.Exec(conn,
                "INSERT INTO search_runs (id, query, location, remote_only, version_id, created_at, stats) VALUES (@id,@q,@l,@r,@v,@c,@s)",
                Db.P("@id", runId), Db.P("@q", request.Query), Db.P("@l", request.Location),
                Db.P("@r", request.RemoteOnly ? 1 : 0), Db.P("@v", request.ResumeVersionId ?? ""),
                Db.P("@c", Now()), Db.P("@s", System.Text.Json.JsonSerializer.Serialize(stats)));
        }

        var summary = new SearchSummary(
            runId, request.Query, request.Location, request.RemoteOnly,
            raw.Count, deduped.Count, saved.Count, used, skipped, errors, request.ResumeVersionId);

        return new Outcome(summary, saved.Count > 0 ? saved : ranked.Select(x => ToDto(x.Item.Job, x.Item.Source, x.Pct, null, Now())).ToList());
    }

    private sealed record SourceResult(SourceDto Source, IReadOnlyList<RawJob> Items, SourceError? Error);

    private async Task<SourceResult> FetchSourceAsync(SourceDto source, JobSearchRequest request, CancellationToken ct)
    {
        try
        {
            // 'link' sources are never fetched. We hand the user a search URL instead.
            if (source.Type == "link")
                return new SourceResult(source, Array.Empty<RawJob>(), new SourceError(source.Name, "This is a link source: resu-clean does not fetch it. Open the search URL and save postings manually."));

            if (!string.IsNullOrEmpty(source.RequiresKey) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(source.RequiresKey)))
                return new SourceResult(source, Array.Empty<RawJob>(), new SourceError(source.Name, $"Needs {source.RequiresKey}. Set it in .env, or ask the API provider for access."));

            var url = SourceRegistry.SearchUrlFor(source, request.Query, request.Location);
            if (string.IsNullOrWhiteSpace(url))
                return new SourceResult(source, Array.Empty<RawJob>(), new SourceError(source.Name, "No URL configured."));

            var fetched = await _http.GetAsync(url, source.RateLimitMs, false, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(fetched.RobotsNote))
                return new SourceResult(source, Array.Empty<RawJob>(), new SourceError(source.Name, fetched.RobotsNote));
            if (string.IsNullOrWhiteSpace(fetched.Body))
                return new SourceResult(source, Array.Empty<RawJob>(), new SourceError(source.Name, DescribeEmpty(source, url, fetched.Status)));

            var items = SourceParser.ParseAsync(source, fetched.Body, url, ct);
            return new SourceResult(source, items, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SourceResult(source, Array.Empty<RawJob>(), new SourceError(source.Name, ex.Message));
        }
    }

    /// <summary>
    /// Turns a bare status code into something the user can act on. The common cases are a bot wall
    /// and an unapproved credential, and a bare "403" tells them nothing about which it is.
    /// </summary>
    private static string DescribeEmpty(SourceDto source, string url, int status)
    {
        if (status == 403 || status == 401)
        {
            if (url.Contains("RELIEFWEB_APPNAME", StringComparison.OrdinalIgnoreCase))
                return "ReliefWeb returned 403. Since 1 Nov 2025 the appname parameter must be pre-approved by "
                     + "ReliefWeb: replace RELIEFWEB_APPNAME in the URL with an appname you have applied for.";

            if (!string.IsNullOrEmpty(source.RequiresKey))
                return $"The provider refused the request (status {status}). Check that {source.RequiresKey} is set "
                     + "and that the key is valid for this endpoint.";

            return $"The provider refused the request (status {status}). This is usually a bot wall or a key this "
                 + "plan is not allowed to use, not a malformed URL. Link sources exist for exactly this reason.";
        }

        if (status == 404)
            return $"The endpoint returned 404, so the URL or field mapping has changed. Press Test on the Sources "
                 + "screen to confirm the path, then fix the source.";

        if (status == 429)
            return "Rate limited by the provider (429). Raise this source's minimum interval before searching again.";

        return $"No content returned (status {status}).";
    }

    private List<SourceDto> SelectSources(JobSearchRequest request)
    {
        var all = _registry.List(enabledOnly: true);
        if (request.SourceIds is { Count: > 0 })
        {
            var wanted = request.SourceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return all.Where(s => wanted.Contains(s.Id)).ToList();
        }
        if (request.Regions is { Count: > 0 })
        {
            var regions = request.Regions.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return all.Where(s => s.Regions.Any(r => regions.Contains(r))).ToList();
        }
        return all;
    }

    private string? LoadResumeText(string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return null;
        using var conn = _db.Open();
        return Db.QueryOne(conn, "SELECT text FROM resume_versions WHERE id = @id", r => Db.Str(r, "text"), Db.P("@id", versionId));
    }

    private List<JobDto> SaveJobs(List<(RawJob Job, SourceDto Source, int Pct)> items, string? versionId)
    {
        var saved = new List<JobDto>();
        using var conn = _db.Open();
        var tx = conn.BeginTransaction();
        try
        {
            foreach (var (job, source, pct) in items)
            {
                if (string.IsNullOrWhiteSpace(job.Title)) continue;
                var id = ProfileService.NewId("job");
                var now = Now();
                var raw = System.Text.Json.JsonSerializer.Serialize(new { job.Description.Length, job.Remote });
                var inserted = Db.Execute(conn,
                    "INSERT OR IGNORE INTO jobs (id, title, company, location, url, posted_at, description, source_id, source_name, match_pct, remote, is_manual, best_version_id, raw, created_at) " +
                    "VALUES (@id,@t,@c,@l,@u,@p,@d,@s,@sn,@m,@r,0,@bv,@raw,@now)",
                    Db.P("@id", id), Db.P("@t", job.Title), Db.P("@c", job.Company ?? ""), Db.P("@l", job.Location ?? ""),
                    Db.P("@u", job.Url ?? ""), Db.P("@p", job.PostedAt), Db.P("@d", job.Description ?? ""),
                    Db.P("@s", source.Id), Db.P("@sn", source.Name), Db.P("@m", pct),
                    Db.P("@r", job.Remote ? 1 : 0), Db.P("@bv", versionId), Db.P("@raw", raw), Db.P("@now", now));
                if (inserted == 0) continue;
                saved.Add(new JobDto(id, job.Title, job.Company ?? "", job.Location ?? "", job.Url ?? "", job.PostedAt,
                    job.Description ?? "", source.Id, source.Name, pct, job.Remote, false, versionId, now));
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        return saved;
    }

    // ---- Queries -----------------------------------------------------------

    /// <summary>
    /// Unmet blockers and unmet required items per job, as correlated subqueries. Counting them here
    /// rather than storing a column keeps the number honest: reclassifying a posting, or approving a
    /// fact that closes a gap, changes the ranking on the next read with no migration and no cache.
    /// </summary>
    private const string RequirementCounts =
        "(SELECT COUNT(*) FROM job_requirements qr WHERE qr.job_id = jobs.id AND qr.class = 'blocker' AND qr.met = 0) AS unmet_blockers, " +
        "(SELECT COUNT(*) FROM job_requirements qr WHERE qr.job_id = jobs.id AND qr.class = 'required' AND qr.met = 0) AS unmet_required, " +
        "(SELECT COUNT(*) FROM job_requirements qr WHERE qr.job_id = jobs.id) AS requirements_total";

    /// <summary>
    /// Rank by what is actually winnable rather than by keyword overlap alone.
    ///
    /// Three tiers, in order: a posting you can meet, a posting nobody has classified yet, and a
    /// posting carrying a blocker you do not have. The middle tier is deliberately not folded in
    /// with the first — an unchecked posting is unknown, not clear, and treating it as clear is how
    /// a seven-year requirement ends up at the top of the list.
    /// </summary>
    private const string WinnabilityOrder =
        "CASE WHEN " + "(SELECT COUNT(*) FROM job_requirements qr WHERE qr.job_id = jobs.id) > 0 " +
        "THEN " + "(SELECT COUNT(*) FROM job_requirements qr WHERE qr.job_id = jobs.id AND qr.class = 'blocker' AND qr.met = 0) " +
        "ELSE -1 END ASC";

    public Paged<JobDto> ListJobs(int page, int size, string? query, string? sourceId, bool? manual, string? versionId)
    {
        page = Math.Max(1, page);
        size = Math.Clamp(size <= 0 ? 25 : size, 1, 100);
        var filters = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (!string.IsNullOrWhiteSpace(query))
        {
            filters.Add("(title LIKE @q OR company LIKE @q OR location LIKE @q OR description LIKE @q)");
            parameters.Add(Db.P("@q", "%" + query.Trim() + "%"));
        }
        if (!string.IsNullOrWhiteSpace(sourceId))
        {
            filters.Add("source_id = @src");
            parameters.Add(Db.P("@src", sourceId));
        }
        if (manual is not null)
        {
            filters.Add("is_manual = @m");
            parameters.Add(Db.P("@m", manual.Value ? 1 : 0));
        }
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            filters.Add("best_version_id = @v");
            parameters.Add(Db.P("@v", versionId));
        }

        var where = filters.Count > 0 ? " WHERE " + string.Join(" AND ", filters) : "";
        using var conn = _db.Open();

        var total = Convert.ToInt32(Db.Scalar(conn, "SELECT COUNT(*) FROM jobs" + where, parameters.ToArray()) ?? 0);
        var offset = (page - 1) * size;
        var sql = "SELECT id,title,company,location,url,posted_at,description,source_id,source_name,match_pct,remote,is_manual,best_version_id,created_at, " +
                  RequirementCounts + " FROM jobs" + where + " ORDER BY " + WinnabilityOrder + ", match_pct DESC, created_at DESC LIMIT @limit OFFSET @offset";
        var pageParameters = new List<SqliteParameter>(parameters)
        {
            Db.P("@limit", size),
            Db.P("@offset", offset)
        };

        var items = Db.Query(conn, sql, r => new JobDto(
            Db.Str(r, "id"), Db.Str(r, "title"), Db.Str(r, "company"), Db.Str(r, "location"), Db.Str(r, "url"),
            Db.StrOrNull(r, "posted_at"), Db.Str(r, "description"), Db.Str(r, "source_id"), Db.Str(r, "source_name"),
            Db.Int(r, "match_pct"), Db.Bool(r, "remote"), Db.Bool(r, "is_manual"), Db.StrOrNull(r, "best_version_id"),
            Db.Str(r, "created_at"), Db.Int(r, "unmet_blockers"), Db.Int(r, "unmet_required"),
            Db.Int(r, "requirements_total") > 0), pageParameters.ToArray());

        return new Paged<JobDto>(items, page, size, total);
    }

    public JobDto? GetJob(string id)
    {
        using var conn = _db.Open();
        return Db.QueryOne(conn,
            "SELECT id,title,company,location,url,posted_at,description,source_id,source_name,match_pct,remote,is_manual,best_version_id,created_at FROM jobs WHERE id = @id",
            r => new JobDto(
                Db.Str(r, "id"), Db.Str(r, "title"), Db.Str(r, "company"), Db.Str(r, "location"), Db.Str(r, "url"),
                Db.StrOrNull(r, "posted_at"), Db.Str(r, "description"), Db.Str(r, "source_id"), Db.Str(r, "source_name"),
                Db.Int(r, "match_pct"), Db.Bool(r, "remote"), Db.Bool(r, "is_manual"), Db.StrOrNull(r, "best_version_id"), Db.Str(r, "created_at")),
            Db.P("@id", id));
    }

    public JobDto SaveManualJob(SaveJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) && string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Give at least a title or the posting text.");

        var title = string.IsNullOrWhiteSpace(request.Title) ? DeriveTitle(request.Text!) : request.Title.Trim();
        var text = request.Text?.Trim() ?? string.Empty;
        var company = request.Company?.Trim() ?? SourceParser.GuessCompany(text, title);
        var location = request.Location?.Trim() ?? SourceParser.GuessLocation(text, company, title);

        using var conn = _db.Open();
        var id = ProfileService.NewId("job");
        var now = Now();
        var sourceName = string.IsNullOrWhiteSpace(request.SourceName) ? "manual" : request.SourceName.Trim();

        Db.Exec(conn,
            "INSERT INTO jobs (id,title,company,location,url,posted_at,description,source_id,source_name,match_pct,remote,is_manual,best_version_id,raw,created_at) " +
            "VALUES (@id,@t,@c,@l,@u,@p,@d,'',@sn,0,@r,1,NULL,'{}',@now)",
            Db.P("@id", id), Db.P("@t", title), Db.P("@c", company), Db.P("@l", location), Db.P("@u", request.Url ?? ""),
            Db.P("@p", request.PostedAt), Db.P("@d", text), Db.P("@sn", sourceName),
            Db.P("@r", request.Remote ? 1 : 0), Db.P("@now", now));

        return GetJob(id)!;
    }

    public bool DeleteJob(string id)
    {
        using var conn = _db.Open();
        return Db.Execute(conn, "DELETE FROM jobs WHERE id = @id", Db.P("@id", id)) > 0;
    }

    private static string DeriveTitle(string text)
    {
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Untitled job";
        return firstLine.Length > 120 ? firstLine[..120] : firstLine;
    }

    private static JobDto ToDto(RawJob job, SourceDto source, int pct, string? versionId, string created) =>
        new(ProfileService.NewId("job"), job.Title, job.Company, job.Location, job.Url, job.PostedAt,
            job.Description, source.Id, source.Name, pct, job.Remote, false, versionId, created);
}