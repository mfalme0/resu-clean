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
///
/// A search URL template is only marked VERIFIED when it was fetched and observed to work. Hosts
/// whose robots.txt disallows everything are never fetched to check, so their templates stay
/// UNVERIFIED and say so. The liveness test in SeedLivenessTests only resolves hosts; it never
/// requests a page.
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
    public const string VerifiedOn = "2026-10-10";

    /// <summary>Politeness floor between two requests to the same source host.</summary>
    private const int DefaultRateLimitMs = 2000;

    /// <summary>Floor for a public JSON API that is meant to be called programmatically.</summary>
    private const int ApiRateLimitMs = 10000;

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
                urlTemplate: "https://www.brightermonday.co.ke/?q={query}",
                regions: new[] { "KE" },
                notes: "VERIFIED " + VerifiedOn + ": the old /jobs?query= template returned HTTP 404; the search lives at the site root as /?q=. No public feed or documented API located, and their robots.txt " +
                        "disallows /*q=*, so this stays link-only: resu-clean builds the URL and you open it. Check the site's own terms before you rely on this."),

            Entry("myjobmag-ke", "MyJobMag Kenya", "link",
                urlTemplate: "https://www.myjobmag.com/jobs?job_query={query}&location={location}",
                regions: new[] { "KE" },
                notes: "VERIFIED " + VerifiedOn + ": the search returns HTTP 200 with results. The .co.ke domain also resolves, but .com is the one serving the job search, so .com is what ships here. " +
                        "No documented public API or feed located. Link only."),

            Entry("fuzu", "Fuzu", "link",
                urlTemplate: "https://fuzu.com/?query={query}&location={location}",
                regions: new[] { "KE", "remote" },
                notes: "No documented API located for the current site. The host answers a scripted request with HTTP 403 behind a bot wall, which is fine because this is link-only and you open it in a browser."),

            Entry("jobweb-ke", "JobWeb Kenya", "link",
                urlTemplate: "https://www.jobwebkenya.com/jobs?q={query}",
                regions: new[] { "KE" },
                notes: "CORRECTED " + VerifiedOn + ": the previously seeded jobweb.co.ke no longer resolves at all. jobwebkenya.com is the live site. UNVERIFIED TEMPLATE - this host's robots.txt is Disallow: /, " +
                        "so it was deliberately not fetched to confirm the search path. Open the site once, fix the path here if it is wrong, and save your own version."),

            Entry("careerjet-ke", "CareerJet Kenya", "link",
                urlTemplate: "https://www.careerjet.co.ke/search-jobs.html?keywords={query}&location={location}",
                regions: new[] { "KE" },
                notes: "Aggregator: expect duplicates against the general boards, which the cross-source dedupe should absorb. UNVERIFIED TEMPLATE - robots.txt is Disallow: / so the path was not fetched. " +
                        "CareerJet runs the same URL scheme across its country sites, but confirm the path on the real site once before relying on it."),

            Entry("unjobs", "UNJobs", "link",
                urlTemplate: "https://unjobs.org/jobs?query={query}",
                regions: new[] { "global", "remote", "humanitarian" },
                notes: "UN agencies and international organisations. UNVERIFIED TEMPLATE - robots.txt is Disallow: /, so the path was not fetched. Read their terms before automating anything; link-only for now."),

            Entry("progigfinder", "ProGigFinder", "link",
                urlTemplate: "https://www.progigfinder.com/jobs?q={query}",
                regions: new[] { "KE", "remote", "global" },
                notes: "VERIFIED " + VerifiedOn + ": robots.txt explicitly allows /jobs/, and the query was observed to filter (5 matches for 'developer' vs 1 for a nonsense term). Newer and less established than the " +
                        "major boards, so treat it as supplementary. Its /api/ path is disallowed by robots, so only the HTML listing is used and this remains link-only."),

            Entry("corporate-staffing-ke", "Corporate Staffing Kenya", "link",
                urlTemplate: "https://corporatestaffing.co.ke/jobs/?q={query}",
                regions: new[] { "KE" },
                notes: "VERIFIED " + VerifiedOn + ": the search returns HTTP 200 with results. Recruitment firm, licensed by IHRM Kenya. No public feed located. Link only."),

            Entry("psc-ke", "Public Service Commission Kenya", "link",
                urlTemplate: "https://www.publicservice.go.ke/?s={query}",
                regions: new[] { "KE" },
                notes: "VERIFIED " + VerifiedOn + ": this is a WordPress site, so /?s= is its real search and returns HTTP 200 with vacancies. Authoritative for public-sector roles. No feed; link only."),

            Entry("indeed-ke", "Indeed Kenya", "link",
                urlTemplate: "https://www.indeed.com/jobs?q={query}&l={location}",
                regions: new[] { "KE", "global" },
                notes: "Note ke.indeed.com does NOT resolve - Indeed only serves the global www host, which is what ships here. Indeed's robots.txt disallows its job paths and its terms forbid automated collection. " +
                        "Never fetched. Link only; save postings by hand if you want them in the pipeline."),

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
                        "Confirmed live that an unapproved appname returns HTTP 403 - the source screen now says that explicitly instead of showing a bare status code. " +
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
                notes: "UNVERIFIED SELECTORS - left disabled on purpose. No feed was located on the public listing pages, and no URL is configured because the public listing path could not be confirmed without fetching a host " +
                        "that forbids it. The CSS selectors here are a best guess. Add the URL, use Detect, look at the preview, then correct the selectors before enabling.")
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
                notes: "Official public API, no key. VERIFIED " + VerifiedOn + ": the array key is 'jobs', not 'data'. Also observed that the ?limit= parameter is IGNORED - ?limit=1 still returns the whole catalogue - " +
                        "so every search pulls the full payload and the rate limit below is the only thing limiting you. TERMS: they block more than 2 requests per minute and advise at most 4 fetches per day, " +
                        "hence the 60s floor. Listings are delayed 24h. Link-back attribution to the original Remotive URL and naming Remotive as the source is required; access can be terminated otherwise, " +
                        "and republishing their jobs to other boards is prohibited."),

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
                notes: "Free public JSON feed, no key, no signup, no OAuth. VERIFIED " + VerifiedOn + ": element 0 of the array is metadata (last_updated + a legal string), not a job; " +
                        "the parser skips it because it has no title. Only the newest 100 postings are exposed, there is no pagination and ?tags= is silently ignored. " +
                        "TERMS: link back with a normal followed link (they say explicitly without nofollow) and credit Remote OK, or access may be suspended. " +
                        "Their RSS twin /remote-jobs.rss returns HTTP 410 and is not configured here."),

            Entry("weworkremotely", "We Work Remotely", "rss",
                url: "https://weworkremotely.com/remote-jobs.rss",
                regions: new[] { "global", "remote" },
                enabledByDefault: true,
                rateLimitMs: 10000,
                notes: "Official RSS feed. VERIFIED " + VerifiedOn + ": returns HTTP 200 and a real rss+xml body. The board has reorganised its URL scheme before, so if it ever stops working it fails safely " +
                        "with a clear error; use Test on the Sources screen to confirm, and disable it if it does."),

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
                        "against Jooble's current docs. The {key} placeholder in the URL is now substituted from JOOBLE_API_KEY when you save a key, so the URL is usable once configured. " +
                        "Put the key in .env, confirm the path in their documentation, then fix the field map via Test before enabling.")
        });

    /// <summary>
    /// Free, keyless, public job APIs that returned real data when checked. Unlike the link packs
    /// these are genuinely fetched, so each one carries its verified robots position in its note.
    /// </summary>
    public static readonly SeedPack OpenFeeds = new(
        "open-feeds",
        "Open job APIs",
        "Free public JSON APIs with no key and no signup. These are fetched, so each one states its robots position.",
        new[]
        {
            Entry("arbeitnow", "Arbeitnow", "json",
                url: "https://www.arbeitnow.com/api/job-board-api",
                regions: new[] { "global", "remote" },
                enabledByDefault: true,
                rateLimitMs: ApiRateLimitMs,
                fieldMap: Map(
                    ("_root", "data"),
                    ("title", "title"),
                    ("company", "company_name"),
                    ("location", "location"),
                    ("url", "url"),
                    ("description", "description"),
                    ("posted", "created_at")),
                notes: "Official public API, no key. VERIFIED " + VerifiedOn + ": returns HTTP 200 with roughly 325 postings across Europe and remote. robots.txt allows everything except a HubSpot tracking " +
                        "parameter, so this endpoint is permitted. Newer aggregator, so expect some duplicate roles against the other boards."),

            Entry("jobicy", "Jobicy", "json",
                url: "https://jobicy.com/api/v2/remote-jobs?count=100",
                regions: new[] { "global", "remote" },
                enabledByDefault: true,
                rateLimitMs: ApiRateLimitMs,
                fieldMap: Map(
                    ("_root", "jobs"),
                    ("title", "jobTitle"),
                    ("company", "companyName"),
                    ("location", "jobGeo"),
                    ("url", "url"),
                    ("description", "jobDescription"),
                    ("posted", "pubDate")),
                notes: "Official public API, no key, documented at jobicy.com/apidocs. VERIFIED " + VerifiedOn + ": returns HTTP 200 with a jobs array. Its robots.txt answers 403, which under RFC 9309 means no " +
                        "restrictions, and the API is published for third-party use in any case. The count parameter caps the response; the free tier publishes a limited window of recent roles."),

            Entry("himalayas", "Himalayas", "json",
                url: "https://himalayas.app/jobs/api?limit=100",
                regions: new[] { "global", "remote" },
                enabledByDefault: true,
                rateLimitMs: ApiRateLimitMs,
                fieldMap: Map(
                    ("_root", "jobs"),
                    ("title", "title"),
                    ("company", "companyName"),
                    ("location", "locationRestrictions"),
                    ("url", "applicationLink"),
                    ("description", "description"),
                    ("posted", "pubDate")),
                notes: "Official public API, no key. VERIFIED " + VerifiedOn + ": returns HTTP 200 with a jobs array; the response advertises cursor pagination, which this source does not yet follow, so you get the " +
                        "first page only. Its robots.txt returns 404, which under RFC 9309 means no restrictions. Salaries and seniority are present in the payload if you want to surface them later.")
        });

    /// <summary>
    /// Greenhouse publishes a per-company public JSON board. Every company running a Greenhouse
    /// board exposes one, which makes this the highest-leverage source for targeting employers.
    /// Slugs here were each fetched and confirmed live; add more by editing a URL to
    /// https://boards-api.greenhouse.io/v1/boards/{company}/jobs
    /// </summary>
    public static readonly SeedPack GreenhouseBoards = new(
        "greenhouse-boards",
        "Greenhouse company boards",
        "Per-company public job boards from Greenhouse. Verified live; add any employer by putting its slug in the URL.",
        new[]
        {
            Greenhouse("gh-cloudflare", "Cloudflare", "cloudflare", 424),
            Greenhouse("gh-databricks", "Databricks", "databricks", 890),
            Greenhouse("gh-stripe", "Stripe", "stripe", 735),
            Greenhouse("gh-anthropic", "Anthropic", "anthropic", 647),
            Greenhouse("gh-gitlab", "GitLab", "gitlab", 219),
            Greenhouse("gh-figma", "Figma", "figma", 151),
            Greenhouse("gh-vercel", "Vercel", "vercel", 89)
        });

    private static SeedPackEntry Greenhouse(string key, string name, string slug, int openRoles) =>
        Entry(key, $"Greenhouse - {name}", "json",
            url: $"https://boards-api.greenhouse.io/v1/boards/{slug}/jobs",
            regions: new[] { "global", "remote" },
            enabledByDefault: true,
            rateLimitMs: ApiRateLimitMs,
            fieldMap: Map(
                ("_root", "jobs"),
                ("title", "title"),
                ("company", "company_name"),
                ("location", "location"),
                ("url", "absolute_url"),
                ("posted", "first_published")),
            notes: $"Greenhouse's public per-company board API, no key. VERIFIED {VerifiedOn}: board '{slug}' returned HTTP 200 with {openRoles} open roles. robots.txt for boards-api.greenhouse.io disallows only /embed/, " +
                    "so this is permitted. The list endpoint carries no job description, so descriptions come back empty until you open the posting; that is inherent to the board API, not a mapping fault. " +
                    $"Add another employer by editing this URL to https://boards-api.greenhouse.io/v1/boards/{{company}}/jobs with their Greenhouse slug.");

    public static SeedPack[] All => new[] { Kenya, GlobalRemote, OpenFeeds, GreenhouseBoards };
}