# Job sources

Every source `resu-clean` can fetch, what was actually checked, and when.

**Last reviewed: 2026-10-10.** These change. Re-check before relying on anything here.

There is a test that does part of this for you:

```bash
RESUCLEAN_LIVE_SOURCE_CHECK=1 npm test -- --only=api
```

It resolves every seeded host and names the ones that have stopped answering. It never requests a
page, so it is safe to run even against the link sources whose `robots.txt` forbids crawling.

## How to read this

- **VERIFIED** — the URL was fetched and observed to work on the date shown. For a search URL, that
  means it was fetched *and* observed to filter; for an API, that it returned real data.
- **UNVERIFIED** — a best guess against markup or URLs that change often. It ships **disabled**, or
  as `link`, and the note tells you what to check.
- **link** — `resu-clean` will **never** fetch this source. It builds the search URL, you open it in
  your own browser, and you paste the posting in with *Add a job*. This is the honest default when a
  board publishes no feed/API or forbids automated access.

Sources live in the database (`job_sources` table), not in code. The tables below mirror
`server/Sources/SeedPacks.cs`; you can edit, disable or delete any of them in the UI and the
registry is what actually governs behaviour.

## A limit worth stating plainly

Where a host's `robots.txt` disallows everything, we **did not fetch it to check whether the search
path works**. Doing so is the exact behaviour this tool declines to do. Those entries ship marked
UNVERIFIED and you confirm the path once by hand. That is a known gap, not a solved problem.

The liveness test above is host-level DNS only, which is why it can cover them safely.

## Global rules, applied to every source

| Rule | Implementation |
|---|---|
| `robots.txt` is respected | Checked before every fetch; a disallowed path is never requested |
| Longest match wins | `Allow`/`Disallow` compete by path length; the more specific rule decides, `Allow` wins ties |
| Honest identification | `RESUCLEAN_USER_AGENT` names the tool and version |
| Rate limiting | Per-source minimum interval, persisted in the registry |
| Caching | Feed/page responses reused for `RESUCLEAN_CACHE_TTL_SECONDS` (default 15 min) |
| No bypass | Never logs in, never solves captchas, never circumvents bot protection |
| Bounded parallelism | At most 4 sources in flight, with a global cap on concurrent searches |
| Failure is visible | A source that cannot be used is reported by name and reason in the search result |

---

## Kenya pack — `kenya`

Mostly `link`, by design. None of these boards publishes a public feed or API.

### BrighterMonday Kenya — `link`
`https://www.brightermonday.co.ke/?q={query}`

**VERIFIED 2026-10-10.** The previously seeded `/jobs?query=` path returned **HTTP 404**; the search
lives at the site root. Their `robots.txt` disallows `/*q=*` and `/*keywords=*`, so this stays
link-only.

### MyJobMag Kenya — `link`
`https://www.myjobmag.com/jobs?job_query={query}&location={location}`

**VERIFIED 2026-10-10.** Returns 200 with results. `myjobmag.co.ke` also resolves, but `.com` is the
host serving the job search, so `.com` is what ships.

### Fuzu — `link`
`https://fuzu.com/?query={query}&location={location}`

Answers scripted requests with **HTTP 403** behind a bot wall. Fine, because you open it in a
browser. No documented API.

### JobWeb Kenya — `link`
`https://www.jobwebkenya.com/jobs?q={query}`

**CORRECTED 2026-10-10.** The previously seeded `jobweb.co.ke` **no longer resolves at all**.
`jobwebkenya.com` is the live site. **UNVERIFIED TEMPLATE** — its `robots.txt` is `Disallow: /`, so
the path was not fetched. Confirm it once and save your own version.

### CareerJet Kenya — `link`, added 2026-10-10
`https://www.careerjet.co.ke/search-jobs.html?keywords={query}&location={location}`

An aggregator, so expect duplicates against the general boards; the cross-source dedupe should
absorb most of them. **UNVERIFIED TEMPLATE** — `robots.txt` is `Disallow: /`, so the path was not
fetched. CareerJet runs one URL scheme across all its country sites, but confirm it on the real
site.

### UNJobs — `link`, added 2026-10-10
`https://unjobs.org/jobs?query={query}`

UN agencies and international organisations. **UNVERIFIED TEMPLATE** — `robots.txt` is
`Disallow: /`. Read their terms before automating anything.

### ProGigFinder — `link`, added 2026-10-10
`https://www.progigfinder.com/jobs?q={query}`

**VERIFIED 2026-10-10.** Its `robots.txt` explicitly allows `/jobs/`, so this one *could* be checked:
`?q=developer` returned 5 matches against 1 for a nonsense term, so filtering genuinely works. Its
`/api/` path is disallowed by robots, so only the HTML listing is used. Newer and less established
than the major boards — treat as supplementary.

### Corporate Staffing Kenya — `link`
`https://corporatestaffing.co.ke/jobs/?q={query}`

**VERIFIED 2026-10-10**, returns 200 with results. Recruitment firm licensed by IHRM Kenya.

### Public Service Commission (Kenya) — `link`
`https://www.publicservice.go.ke/?s={query}`

**VERIFIED 2026-10-10.** This is a WordPress site, so `/?s=` is its real search and returns
vacancies. Authoritative for public-sector roles. No feed.

### Indeed Kenya — `link`, never fetched
`https://www.indeed.com/jobs?q={query}&l={location}`

Note **`ke.indeed.com` does not resolve** — Indeed only serves the global `www` host, which is what
ships here. Indeed's `robots.txt` disallows its job paths and its terms forbid automated collection.
**Never fetched.**

### LinkedIn Jobs (Kenya) — `link`, never fetched
`https://www.linkedin.com/jobs/search?keywords={query}&location={location}`

LinkedIn's User Agreement forbids automated access and its `robots.txt` disallows `/jobs`.
**Never fetched.**

### ReliefWeb — `json`, **disabled by default**
`https://api.reliefweb.int/v2/jobs?appname=RELIEFWEB_APPNAME&limit=100`

**VERIFIED 2026-10-10** — [apidoc.reliefweb.int](https://apidoc.reliefweb.int/):

- **v1 is decommissioned.** Only **v2** exists.
- Since **1 November 2025** the `appname` must be **pre-approved** by request form.
- Confirmed live: an unapproved `appname` returns **HTTP 403**. The Sources screen now says exactly
  that instead of showing a bare status code.

Field mapping, confirmed against the ReliefWeb field tables. The list response is rooted at `data`:

| Our field | ReliefWeb field |
|---|---|
| `_root` | `data` |
| `title` | `title` |
| `company` | `source.name` |
| `location` | `city.name` |
| `description` | `body` |
| `url` | `url_alias` |
| `posted` | `date.created` |

### UN Careers — `html`, **disabled, selectors unverified**
No URL is configured, because the public listing path could not be confirmed without fetching a host
that forbids it. Add the URL, then use *Detect* and correct the selectors.

```
item        div.joblist-item, div.card-body, li.job
title       a.job-title, h3 a, .job-title
company     .job-org, .field--name-organization
location    .job-location, .field--name-location
description .field--name-body, p
posted      .date, .field--name-created
```

---

## Global remote pack — `global-remote`

### Remotive — `json`, enabled
`https://remotive.com/api/remote-jobs?limit=100` · minimum interval **60 s**

**VERIFIED 2026-10-10.** Returns 200. The array key is `jobs`, not `data`.

- **`?limit=` is ignored.** `?limit=1` still returned the whole catalogue, so every search pulls the
  full payload and the 60-second floor is the only thing limiting you.
- **Terms:** they block more than 2 requests per minute and advise **at most 4 fetches per day**.
- Listings are delayed 24 hours.
- **Link back** and **name Remotive as the source**, or access is terminated. Republishing their
  jobs to other boards is prohibited.

| Our field | Remotive field |
|---|---|
| `_root` | `jobs` |
| `title` | `title` |
| `company` | `company_name` |
| `location` | `candidate_required_location` |
| `url` | `url` |
| `description` | `description` |
| `posted` | `publication_date` |

### RemoteOK — `json`, enabled
`https://remoteok.com/api` · minimum interval 10 s

**VERIFIED 2026-10-10.** Returns 200.

- **Element 0 is not a job** — it is `{ last_updated, legal }`. It has no `position` field, so the
  parser skips it. There is a regression test pinning that.
- **Only the newest 100 postings exist.** No pagination; `?tags=` is silently ignored.
- `/remote-jobs.rss` returns **HTTP 410 Gone** and is not configured.
- **Terms:** link back with a normal followed link — they say explicitly *without `nofollow`* — and
  credit Remote OK.

| Our field | RemoteOK field |
|---|---|
| `title` | `position` |
| `company` | `company` |
| `location` | `location` |
| `url` | `url` |
| `description` | `description` |
| `posted` | `date` |

### We Work Remotely — `rss`, enabled
`https://weworkremotely.com/remote-jobs.rss` · minimum interval 10 s

**VERIFIED 2026-10-10:** returns 200 and a real `rss+xml` body. The board has reorganised its URL
scheme before, so if it ever stops it fails safely with a clear error.

### Jooble — `json`, **disabled**
`https://jooble.org/api/{key}` · requires `JOOBLE_API_KEY`

Needs your own key under a paid or trial agreement. **UNVERIFIED 2026-10-10** — the exact path and
response shape were not confirmed against their current docs. The `{key}` placeholder is now
substituted from `JOOBLE_API_KEY` when you set it, so the URL is usable once configured. Confirm
the path, then fix the field map via *Test* before enabling.

---

## Open job APIs pack — `open-feeds`, added 2026-10-10

Free, keyless, public JSON APIs that returned real data when checked. **These are genuinely
fetched**, so each entry records its verified robots position.

### Arbeitnow — `json`, enabled
`https://www.arbeitnow.com/api/job-board-api` · minimum interval 10 s

**VERIFIED 2026-10-10:** HTTP 200, roughly **325 postings**. `robots.txt` allows everything except a
HubSpot tracking parameter, so this endpoint is permitted.

| Our field | Arbeitnow field |
|---|---|
| `_root` | `data` |
| `title` | `title` |
| `company` | `company_name` |
| `location` | `location` |
| `url` | `url` |
| `description` | `description` |
| `posted` | `created_at` |

### Jobicy — `json`, enabled
`https://jobicy.com/api/v2/remote-jobs?count=100` · minimum interval 10 s

**VERIFIED 2026-10-10:** HTTP 200 with a `jobs` array. Documented at jobicy.com/apidocs. Its
`robots.txt` answers **403**, which under RFC 9309 means no restrictions; the API is published for
third-party use in any case.

| Our field | Jobicy field |
|---|---|
| `_root` | `jobs` |
| `title` | `jobTitle` |
| `company` | `companyName` |
| `location` | `jobGeo` |
| `url` | `url` |
| `description` | `jobDescription` |
| `posted` | `pubDate` |

### Himalayas — `json`, enabled
`https://himalayas.app/jobs/api?limit=100` · minimum interval 10 s

**VERIFIED 2026-10-10:** HTTP 200 with a `jobs` array. The response advertises cursor pagination,
which this source does not yet follow, so you get the first page only. Its `robots.txt` returns
**404**, which under RFC 9309 means no restrictions.

| Our field | Himalayas field |
|---|---|
| `_root` | `jobs` |
| `title` | `title` |
| `company` | `companyName` |
| `location` | `locationRestrictions` |
| `url` | `applicationLink` |
| `description` | `description` |
| `posted` | `pubDate` |

---

## Greenhouse company boards — `greenhouse-boards`, added 2026-10-10

Greenhouse publishes a **per-company public JSON board**. Every company running a Greenhouse board
exposes one, which makes this the highest-leverage source for targeting employers rather than
browsing shared boards.

`https://boards-api.greenhouse.io/v1/boards/{company}/jobs`

`robots.txt` for `boards-api.greenhouse.io` disallows only `/embed/`, so this is permitted.

Each seeded board was fetched and confirmed on 2026-10-10:

| Company | Slug | Open roles |
|---|---|---|
| Cloudflare | `cloudflare` | 424 |
| Databricks | `databricks` | 890 |
| Stripe | `stripe` | 735 |
| Anthropic | `anthropic` | 647 |
| GitLab | `gitlab` | 219 |
| Figma | `figma` | 151 |
| Vercel | `vercel` | 89 |

**Adding an employer:** edit a source's URL to
`https://boards-api.greenhouse.io/v1/boards/{company}/jobs` with their Greenhouse slug. Press
*Test* to confirm the slug before enabling.

| Our field | Greenhouse field |
|---|---|
| `_root` | `jobs` |
| `title` | `title` |
| `company` | `company_name` |
| `location` | `location` |
| `url` | `absolute_url` |
| `posted` | `first_published` |

**No description.** The Greenhouse list endpoint does not carry job descriptions; they are on the
per-job detail endpoint. Descriptions come back empty. That is inherent to the board API, not a
mapping fault.

---

## What a search does with these

1. Skips `link` sources outright — they are never fetched.
2. Resolves the URL for each enabled, non-`link` source, substituting `{query}`, `{location}` and,
   where the source declares one, `{key}`.
3. Skips sources whose `requiresKey` env var is unset (reported by name).
4. Applies the per-source minimum interval.
5. Checks `robots.txt`; a disallow is reported, never fetched.
6. Serves from cache when fresh, otherwise fetches once and caches.
7. Parses per type into a common shape: title, company, location, url, posted date, description,
   remote flag.
8. **Deduplicates across sources** on canonical URL (tracking parameters stripped) then on
   normalised title + company, with fuzzy matching and legal-suffix-insensitive company comparison.
9. **Ranks** the survivors by keyword overlap against the selected resume version.
10. Reports fetched / after-dedupe / saved counts, plus every source skipped or failed and why.

## Attribution is your obligation when you publish

`resu-clean` keeps each job's original URL and shows the source name, which satisfies the attribution
requirements of Remotive and RemoteOK for jobs you view in the UI. If you ever publish aggregated
output from those feeds, **complying with their terms is your responsibility, not the tool's** — in
particular the link-back requirement.