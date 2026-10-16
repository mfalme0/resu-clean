using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ResuClean.Data;
using ResuClean.Models;

namespace ResuClean.Services;

/// <summary>
/// Sends one digest per day, addressed to @roles, saying how many prospects turned up and how many
/// need a decision.
///
/// Deliberately not a stream. A board sweep can save hundreds of postings in one run; firing a
/// message for each produces a wall of noise that gets muted within a day, which is the same
/// outcome as having no webhook at all. A count is what survives a notification channel.
///
/// What leaves the machine, and what does not. The payload carries job titles, companies, URLs and
/// verdicts. It never carries resume text, profile facts, verified note content or generated kits.
/// That is what keeps "your resume never leaves this machine" literally true with a webhook
/// configured, and <see cref="BuildDigest"/> is where that is enforced rather than documented.
///
/// Counts are "since the last digest sent", which is the cursor that makes a daily job mean
/// anything. Without it every run would repeat everything ever found.
/// </summary>
public sealed class WebhookService
{
    /// <summary>New postings saved since the last digest.</summary>
    public const string EventNewJobs = "new_jobs";

    /// <summary>A posting's verification verdict changed since the last digest.</summary>
    public const string EventVerification = "verification_changed";

    public const string DefaultUrgentDays = "7";

    private static readonly string[] KnownEvents = { EventNewJobs, EventVerification };

    private readonly Db _db;
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<WebhookService> _log;

    public WebhookService(Db db, IHttpClientFactory factory, ILogger<WebhookService> log)
    {
        _db = db;
        _factory = factory;
        _log = log;
    }

    // ---- Configuration ------------------------------------------------------

    public List<WebhookDto> List()
    {
        using var conn = _db.Open();
        return Db.Query(conn,
            "SELECT id, label, url, enabled, mentions, sender_name, events, urgent_days, last_sent_at, created_at " +
            "FROM webhooks ORDER BY created_at",
            Map, Array.Empty<Microsoft.Data.Sqlite.SqliteParameter>());
    }

    public WebhookDto? Get(string id)
    {
        using var conn = _db.Open();
        return Db.QueryOne(conn,
            "SELECT id, label, url, enabled, mentions, sender_name, events, urgent_days, last_sent_at, created_at " +
            "FROM webhooks WHERE id = @id", Map, Db.P("@id", id));
    }

    public WebhookDto Save(WebhookRequest request)
    {
        var url = (request.Url ?? "").Trim();
        if (url.Length == 0) throw new ArgumentException("Give the webhook URL to post to.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != "http" && parsed.Scheme != "https"))
            throw new ArgumentException("That is not an http(s) URL.");

        // Mentions are names, not accounts, so the only rule is that they read as handles: no
        // whitespace, and nothing that could break the line the receiver parses.
        var mentions = (request.Mentions ?? new List<string>())
            .Select(m => m.Trim().TrimStart('@').Trim())
            .Where(m => m.Length > 0 && !m.Any(char.IsWhiteSpace))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var events = (request.Events ?? new List<string>())
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => KnownEvents.Contains(e))
            .Distinct()
            .ToList();
        if (events.Count == 0) events = KnownEvents.ToList();

        var urgent = Math.Clamp(request.UrgentDays ?? 7, 0, 90);
        var now = JobSearchService.Now();
        var id = request.Id is { Length: > 0 } existing ? existing : ProfileService.NewId("wh");
        var label = (request.Label ?? "").Trim();

        using var conn = _db.Open();
        var exists = Db.Scalar(conn, "SELECT 1 FROM webhooks WHERE id = @id", Db.P("@id", id)) != null;
        if (exists)
        {
            Db.Exec(conn,
                "UPDATE webhooks SET label=@l, url=@u, enabled=@e, mentions=@m, sender_name=@s, " +
                "events=@ev, urgent_days=@ud, updated_at=@now WHERE id=@id",
                Db.P("@l", label), Db.P("@u", url), Db.P("@e", request.Enabled == false ? 0 : 1),
                Db.P("@m", JsonSerializer.Serialize(mentions)), Db.P("@s", (request.SenderName ?? "").Trim()),
                Db.P("@ev", JsonSerializer.Serialize(events)), Db.P("@ud", urgent),
                Db.P("@now", now), Db.P("@id", id));
        }
        else
        {
            Db.Exec(conn,
                "INSERT INTO webhooks (id, label, url, enabled, mentions, sender_name, events, urgent_days, created_at, updated_at) " +
                "VALUES (@id,@l,@u,@e,@m,@s,@ev,@ud,@now,@now)",
                Db.P("@id", id), Db.P("@l", label), Db.P("@u", url), Db.P("@e", request.Enabled == false ? 0 : 1),
                Db.P("@m", JsonSerializer.Serialize(mentions)), Db.P("@s", (request.SenderName ?? "").Trim()),
                Db.P("@ev", JsonSerializer.Serialize(events)), Db.P("@ud", urgent), Db.P("@now", now));
        }

        return Get(id)!;
    }

    public bool Delete(string id)
    {
        using var conn = _db.Open();
        return Db.Execute(conn, "DELETE FROM webhooks WHERE id = @id", Db.P("@id", id)) > 0;
    }

    // ---- The digest ---------------------------------------------------------

    public sealed record Item(string Key, string Title, string Company, string Url, string Stage,
        string Verdict, string? Deadline, bool Urgent, string Why);

    public sealed record Digest(
        string Message,
        IReadOnlyList<Item> NewJobs,
        IReadOnlyList<Item> Changed,
        int NewCount,
        int UrgentCount,
        DateTime Since);

    /// <summary>
    /// Works out what has happened since the last digest and phrases it as one line.
    ///
    /// Pure: it reads and writes nothing, so the message can be previewed in the settings screen
    /// before a webhook is ever pointed anywhere. That preview is the only way to find out that
    /// your receiver wants the counts somewhere else.
    /// </summary>
    public Digest BuildDigest(WebhookDto hook, DateTime? now = null)
    {
        var since = LastSent(hook) ?? DateTime.UtcNow.AddDays(-1);
        var until = now ?? DateTime.UtcNow;
        var urgentWindow = hook.UrgentDays;
        var events = hook.Events.Count > 0 ? hook.Events : KnownEvents.ToList();

        var newJobs = events.Contains(EventNewJobs) ? Load(hook, since, until, urgentWindow, "created_at") : new List<Item>();
        var changed = events.Contains(EventVerification) ? Load(hook, since, until, urgentWindow, "status_changed_at") : new List<Item>();

        // Urgent is counted across both lists and de-duplicated by key, because a posting can be
        // new and also newly verified, and "10 prospects and 3 need action" must not double-count.
        var urgent = newJobs.Concat(changed)
            .Where(i => i.Urgent)
            .GroupBy(i => i.Key, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var message = Compose(hook.Mentions, hook.SenderName, newJobs, urgent.Count, changed.Count);

        return new Digest(message, newJobs, changed, newJobs.Count, urgent.Count, since);
    }

    private string Compose(
        IReadOnlyList<string> mentions, string sender, List<Item> newJobs, int urgent, int changed)
    {
        // "@jobseekers userx has 10 new prospects and 3 need immediate action"
        var address = mentions.Count > 0 ? string.Join(" ", mentions.Select(m => "@" + m)) + " " : "";
        var who = sender.Length > 0 ? sender + " " : "";

        var found = newJobs.Count;
        var parts = new List<string>
        {
            found == 0
                ? "no new prospects"
                : $"has {found} new {(found == 1 ? "prospect" : "prospects")}"
        };

        // "3 need immediate action" — but only when there is something to act on, because a daily
        // message that always ends "and 0 need immediate action" trains people to ignore it.
        if (urgent > 0) parts.Add(urgent == 1 ? "1 needs immediate action" : $"{urgent} need immediate action");
        else if (found == 0 && changed == 0) parts.Add("nothing to look at");

        // Verification changes are reported separately from new prospects, because a posting that
        // went from open to closed is not a new prospect and counting it as one overstates the news.
        if (changed > 0)
            parts.Add(changed == 1 ? "1 posting changed status" : $"{changed} postings changed status");

        // Commas between the clauses, "and" only before the last one. Joining everything with "and"
        // produces "has 12 new prospects and 2 need immediate action and 2 postings changed status",
        // which reads as a list the writer lost track of.
        return (address + who + And(parts)).Trim();
    }

    /// <summary>Three or more clauses read as a list, so only the last join is "and".</summary>
    private static string And(IReadOnlyList<string> parts)
    {
        if (parts.Count <= 1) return string.Concat(parts);
        return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];
    }

    private List<Item> Load(WebhookDto hook, DateTime since, DateTime until, int urgentWindow, string stampColumn)
    {
        using var conn = _db.Open();
        var rows = Db.Query(conn,
            "SELECT j.id, j.title, j.company, j.url, COALESCE(j.deadline_at,'') AS deadline, " +
            "COALESCE(j.status,'') AS status, " +
            "COALESCE((SELECT a.status FROM applications a WHERE a.job_id = j.id ORDER BY a.updated_at DESC LIMIT 1),'') AS stage " +
            "FROM jobs j WHERE j." + stampColumn + " IS NOT NULL AND j." + stampColumn + " > @since AND j." + stampColumn + " <= @until " +
            "ORDER BY " + stampColumn + " DESC LIMIT 200",
            r => new
            {
                Id = Db.Str(r, "id"),
                Title = Db.Str(r, "title"),
                Company = Db.Str(r, "company"),
                Url = Db.Str(r, "url"),
                Deadline = Db.Str(r, "deadline"),
                Status = Db.Str(r, "status"),
                Stage = Db.Str(r, "stage")
            },
            Db.P("@since", Iso(since)), Db.P("@until", Iso(until)));

        var urgentCutoff = until.AddDays(urgentWindow);
        return rows.Select(r =>
        {
            var deadline = ParseDate(r.Deadline);

            // Urgent means one of two things, and both are things a person must act on today:
            // it closes within the window, or it has already closed and is still sitting in the list.
            var closesSoon = deadline is not null && deadline <= urgentCutoff;
            var alreadyClosed = r.Status is "closed" || r.Stage == "closed";
            var unverified = r.Status == "unverifiable";
            var why = closesSoon ? "closes soon"
                : alreadyClosed ? "closed"
                : unverified ? "unverified"
                : "";

            return new Item(r.Id, r.Title, r.Company, r.Url, r.Stage, r.Status, r.Deadline,
                closesSoon || alreadyClosed || unverified, why);
        }).ToList();
    }

    // ---- Sending ------------------------------------------------------------

    /// <summary>
    /// Posts the digest and moves the cursor forward.
    ///
    /// The cursor only moves on success. A digest that failed to send has still not been seen, so
    /// advancing past it would silently swallow a day's worth of news — which is the one failure
    /// mode a notification system cannot have.
    /// </summary>
    public async Task<WebhookSendResult> SendAsync(WebhookDto hook, CancellationToken ct = default)
    {
        var digest = BuildDigest(hook);
        var payload = new
        {
            text = digest.Message,
            digest = new
            {
                since = digest.Since,
                newCount = digest.NewCount,
                urgentCount = digest.UrgentCount,
                newJobs = digest.NewJobs,
                changed = digest.Changed
            }
        };

        var client = _factory.CreateClient("webhooks");
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            using var response = await client.PostAsync(hook.Url, JsonContent.Create(payload), ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new WebhookSendResult(false, digest.Message, (int)response.StatusCode,
                    $"The endpoint answered {(int)response.StatusCode}.",
                    Truncate(body));
            }

            // Only now does the day count as seen.
            using var conn = _db.Open();
            Db.Exec(conn, "UPDATE webhooks SET last_sent_at = @now, updated_at = @now WHERE id = @id",
                Db.P("@now", JobSearchService.Now()), Db.P("@id", hook.Id));

            return new WebhookSendResult(true, digest.Message, (int)response.StatusCode, "Sent.", Truncate(body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The endpoint's body is not logged: a webhook receiver may well be echoing back
            // something, and this app does not put your data in logs.
            _log.LogWarning("Webhook {Id} could not be delivered: {Reason}", hook.Id, ex.GetType().Name);
            return new WebhookSendResult(false, digest.Message, 0, "Could not reach the endpoint.", "");
        }
    }

    /// <summary>Every enabled webhook, or none. Used by the once-a-day runner.</summary>
    public List<WebhookDto> Enabled()
    {
        using var conn = _db.Open();
        return Db.Query(conn,
            "SELECT id, label, url, enabled, mentions, sender_name, events, urgent_days, last_sent_at, created_at " +
            "FROM webhooks WHERE enabled = 1", Map, Array.Empty<Microsoft.Data.Sqlite.SqliteParameter>());
    }

    private DateTime? LastSent(WebhookDto hook) =>
        DateTime.TryParse(hook.LastSentAt ?? "", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
            ? when
            : null;

    /// <summary>
    /// Timestamps for the digest window, in the same shape the database stores.
    ///
    /// This has to be the identical format rather than a tidier one. SQLite compares TEXT
    /// lexicographically, so "…:45.123Z" sorts before "…:45Z" — the full stop is a lower
    /// character than the Z. Comparing a second-precision boundary against a full-precision
    /// created_at silently drops postings created in the same second, which is exactly the
    /// population a daily digest exists to report.
    /// </summary>
    private static string Iso(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
            ? when : null;

    private static string Truncate(string body) => body.Length > 400 ? body[..400] : body;

    private static WebhookDto Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        Db.Str(r, "id"),
        Db.Str(r, "label"),
        Db.Str(r, "url"),
        Db.Bool(r, "enabled"),
        Strings(Db.Str(r, "mentions")),
        Db.Str(r, "sender_name"),
        Strings(Db.Str(r, "events")),
        Db.Int(r, "urgent_days"),
        Db.StrOrNull(r, "last_sent_at"),
        Db.Str(r, "created_at"));

    private static List<string> Strings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            return new List<string>();
        }
    }
}

public sealed record WebhookSendResult(
    bool Ok,
    string Message,
    int Status,
    string Detail,
    string ResponseBody);