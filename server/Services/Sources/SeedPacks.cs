using ResuClean.Models;

namespace ResuClean.Services.Sources;

/// <summary>
/// Seed packs. Sources live in the database, not in code; these are starting points the user can
/// edit, disable or delete.
///
/// RESEARCH DISCIPLINE
/// Every note below states what was actually checked and when. See docs/SOURCES.md for the full
/// table with links. Where automated access is undocumented, restricted or disallowed the type is
/// "link": resu-clean builds the search URL and the user opens it in their own browser.
/// Nothing in this file is fetched until the user enables it, and link sources are never fetched.
/// </summary>
public sealed record SeedPackEntry(
    string Key,
    string Name,
    string Type,
    string? Url,
    string? UrlTemplate,
    string[] Regions,
    bool EnabledByDefault,
    int RateLimitMs,
    Dictionary<string, string> FieldMap,
    Dictionary<string, string> Selectors,
    string RequiresKey,
    string Notes);

public sealed record SeedPack(string Id, string Name, string Description, SeedPackEntry[] Entries);

public static class SeedPacks
{
    /// <summary>The date the notes in this file were last checked against the providers' own docs.</summary>
    public const string VerifiedOn = "2026-10-06";

    /// <summary>Politeness floor between two requests to the same source host.</summary>
    private const int DefaultRateLimitMs = 2000;

    private static Dictionary<string, string> Map(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static Dictionary<string, string> Sel(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static SeedPackEntry Entry(
        string key,
        string name,
        string type,
        string? url = null,
        string? urlTemplate = null,
        string[]? regions = null,
        bool enabledByDefault = false,
        int rateLimitMs = DefaultRateLimitMs,
        Dictionary<string, string>? fieldMap = null,
        Dictionary<string, string>? selectors = null,
        string requiresKey = "",
        string notes = "")
    {
        return new SeedPackEntry(
            key, name, type, url, urlTemplate,
            regions ?? new[] { "global" },
            enabledByDefault, rateLimitMs,
            fieldMap ?? Map(), selectors ?? Sel(), requiresKey, notes);
    }

    public static readonly SeedPack Kenya = new(
        "kenya",
        "Kenya",
        "Kenyan job boards plus open regional and humanitarian listings. Most boards are link-only by design.",
        new[]
        {
            Entry("brightermonday-ke", "BrighterMonday Kenya", "link",
                urlTemplate: "https://www.brightermonday.co.ke/jobs?query={query}&location={location}",
                regions: new[] { "KE" },
                notes: "No public feed or documented API located. Link only: resu-clean builds the search URL and you open it. Check the site's own terms before you rely on this."),

            Entry("myjobmag-ke", "MyJobMag Kenya", "link",
                urlTemplate: "https://www.myjobmag.com/jobs?job_query={query}&location={location}",
                regions: new[] { "KE" },
                notes: "No documented public API or feed located. Link only."),

            Entry("fuzu", "Fuzu", "link",
                urlTemplate: "https://fuzu.com/?query={query}&location={location}",
                regions: new[] { "KE", "remote" },
                notes: "No documented API located for the current site. Link only."),

            Entry("jobweb-ke", "JobWeb Kenya", "link",
                urlTemplate: "https://www.jobweb.co.ke/jobs/{location}?search={query}",
                regions: new[] { "KE" },
                notes: "No public feed located. Link only."),

            Entry("careerpoint-ke", "CareerPoint Kenya", "link",
                urlTemplate: "https://cpoint.co.ke/jobs?query={query}",
                regions: new[] { "KE" },
                notes: "No public feed or API located. Link only."),

            Entry("corporate-staffing-ke", "Corporate Staffing Kenya", "link",
                urlTemplate: "https://corporatestaffing.co.ke/jobs/?q={query}",
                regions: new[] { "KE" },
                notes: "No public feed located. Link only."),

            Entry("psc-ke", "Public Service Commission Kenya", "link",
                urlTemplate: "https://www.publicservice.go.ke/?s={query}",
                regions: new[] { "KE" },
                notes: "Advertised vacancies are on publicservice.go.ke with no feed. This template is a search-URL guess: fix the path once on the site and save your own version."),

            Entry("indeed-ke", "Indeed Kenya", "link",
                urlTemplate: "https://www.indeed.com/jobs?q={query}&l={location}",
                regions: new[] { "KE", "global" },
                notes: "Indeed's robots.txt disallows its job paths and its terms forbid automated collection. Never fetched. Link only; save postings by hand if you want them in the pipeline."),

            Entry("linkedin-jobs-ke", "LinkedIn Jobs (Kenya)", "link",
                urlTemplate: "https://www.linkedin.com/jobs/search?keywords={query}&location={location}",
                regions: new[] { "KE", "global" },
                notes: "LinkedIn's User Agreement forbids automated access and its robots.txt disallows /jobs. Never fetched. Link only."),

            Entry("reliefweb", "ReliefWeb", "json",
                url: "https://api.reliefweb.int/v2/jobs?appname=RELIEFWEB_APPNAME&limit=100",
                regions: new[] { "global", "remote", "humanitarian" },
                enabledByDefault: false,
                rateLimitMs: 5000,
                fieldMap: Map(
                    ("_root", "data"),
                    ("title", "title"),
                    ("company", "source.name"),
                    ("location", "city.name"),
                    ("description", "body"),
                    ("url", "url_alias"),
                    ("posted", "date.created")),
                notes: "Official public API. VERIFIED " + VerifiedOn + ": v1 is decommissioned, only v2 exists, and since 1 Nov 2025 the appname parameter must be PRE-APPROVED by ReliefWeb. " +
                        "Disabled by default on purpose: request an appname, then replace RELIEFWEB_APPNAME in the URL. Field names confirmed from the ReliefWeb field tables. Read-only API."),

            Entry("un-careers", "UN Careers", "html",
                regions: new[] { "global", "remote", "humanitarian" },
                selectors: Sel(
                    ("item", "div.joblist-item, div.card-body, li.job"),
                    ("title", "a.job-title, h3 a, .job-title"),
                    ("company", ".job-org, .field--name-organization"),
                    ("location", ".job-location, .field--name-location"),
                    ("description", ".field--name-body, p"),
                    ("posted", ".date, .field--name-created")),
                notes: "UNVERIFIED SELECTORS - left disabled on purpose. No feed was located on the public listing pages. The CSS selectors here are a best guess against markup that changes often. " +
                        "Use Detect, look at the preview, then correct the selectors before enabling. Keep requests infrequent and read their terms first.")
        });

    public static readonly SeedPack GlobalRemote = new(
        "global-remote",
        "Global remote",
        "Remote-first boards that publish a real feed or API, with the terms that come attached.",
        new[]
        {
            Entry("remotive", "Remotive", "json",
                url: "https://remotive.com/api/remote-jobs?limit=100",
                regions: new[] { "global", "remote" },
                enabledByDefault: true,
                rateLimitMs: 60000,
                fieldMap: Map(
                    ("_root", "jobs"),
                    ("title", "title"),
                    ("company", "company_name"),
                    ("location", "candidate_required_location"),
                    ("url", "url"),
                    ("description", "description"),
                    ("posted", "publication_date")),
                notes: "Official public API, no key. VERIFIED " + VerifiedOn + ": the array key is 'jobs', not 'data'. TERMS: they block more than 2 requests per minute and advise " +
                        "at most 4 fetches per day, so the rate limit is 60s. Listings are delayed 24h. Link-back attribution to the original Remotive URL and naming Remotive as the source is " +
                        "required; access can be terminated otherwise, and republishing their jobs to other boards is prohibited."),

            Entry("remoteok", "RemoteOK", "json",
                url: "https://remoteok.com/api",
                regions: new[] { "global", "remote" },
                enabledByDefault: true,
                rateLimitMs: 10000,
                fieldMap: Map(
                    ("title", "position"),
                    ("company", "company"),
                    ("location", "location"),
                    ("url", "url"),
                    ("description", "description"),
                    ("posted", "date")),
                notes: "Free public JSON feed, no key, no signup. VERIFIED " + VerifiedOn + ": element 0 of the array is metadata (last_updated + a legal string), not a job; " +
                        "the parser skips it because it has no title. Only the newest 100 postings are exposed, there is no pagination and ?tags= is silently ignored. " +
                        "TERMS: link back with a normal followed link (they say explicitly without nofollow) and credit Remote OK, or access may be suspended. " +
                        "Their RSS twin /remote-jobs.rss returns HTTP 410 and is not configured here."),

            Entry("weworkremotely", "We Work Remotely", "rss",
                url: "https://weworkremotely.com/remote-jobs.rss",
                regions: new[] { "global", "remote" },
                enabledByDefault: true,
                rateLimitMs: 10000,
                notes: "Official RSS feed. UNVERIFIED since " + VerifiedOn + ": the board has reorganised its URL scheme before. Enabled because a feed is advertised and a bad URL fails safely " +
                        "with a clear error; use Test on the Sources screen to confirm, and disable it if it stops working."),

            Entry("jooble", "Jooble", "json",
                url: "https://jooble.org/api/{key}",
                regions: new[] { "global", "remote" },
                enabledByDefault: false,
                fieldMap: Map(
                    ("title", "title"),
                    ("company", "company"),
                    ("location", "location"),
                    ("url", "url"),
                    ("description", "description"),
                    ("posted", "date")),
                requiresKey: "JOOBLE_API_KEY",
                notes: "Needs your own Jooble API key and a paid or trial agreement, so it stays disabled. UNVERIFIED since " + VerifiedOn + ": the exact path and response shape were not confirmed " +
                        "against Jooble's current docs. Put JOOBLE_API_KEY in .env, confirm the path in their documentation, then fix the field map via Test before enabling.")
        });

    public static SeedPack[] All => new[] { Kenya, GlobalRemote };
}