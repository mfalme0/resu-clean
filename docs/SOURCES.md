# Job sources

Every source `resu-clean` can fetch, what was actually checked, and when.

**Last reviewed: 2026-10-06.** These change. Re-check before relying on anything here.

## How to read this

- **VERIFIED** — the endpoint, auth model and response shape were read from the provider's own
  current documentation on the date shown.
- **UNVERIFIED** — the entry is a best guess against markup or URLs that change often. It ships
  **disabled**, and the note tells you what to check.
- **link** — `resu-clean` will **never** fetch this source. It builds the search URL, you open it in
  your own browser, and you paste the posting in with *Add a job*. This is the honest default when a
  board publishes no feed/API or forbids automated access.

Sources live in the database (`job_sources` table), not in code. The tables below are the seed
packs in `server/Services/Sources/SeedPacks.cs`; you can edit, disable or delete any of them in the
UI and the registry is what actually governs behaviour.

## Global rules, applied to every source

| Rule | Implementation |
|---|---|
| `robots.txt` is respected | Checked before every fetch; a disallowed path is never requested |
| Honest identification | `RESUCLEAN_USER_AGENT` names the tool and version |
| Rate limiting | Per-source minimum interval, persisted in the registry |
| Caching | Feed/page responses reused for `RESUCLEAN_CACHE_TTL_SECONDS` (default 15 min) |
| No bypass | Never logs in, never solves captchas, never circumvents bot protection |
| Bounded parallelism | At most 4 sources in flight, with a global cap on concurrent searches |
| Failure is visible | A source that cannot be used is reported by name and reason in the search result |

---

## Kenya pack

### BrighterMonday Kenya — `link`
`https://www.brightermonday.co.ke/jobs?query={query}&location={location}`

No public feed or documented API was located. Their terms restrict automated collection, so this is
link-only: resu-clean builds the URL and you open it. Check their current terms yourself.

### MyJobMag Kenya — `link`
`https://www.myjobmag.com/jobs?job_query={query}&location={location}`

No documented public API or feed located. Link only.

### Fuzu — `link`
`https://fuzu.com/?query={query}&location={location}`

No documented API located for the current site. Link only.

### JobWeb Kenya — `link`
`https://www.jobweb.co.ke/jobs/{location}?search={query}`

No public feed located. Link only.

### CareerPoint Kenya — `link`
`https://cpoint.co.ke/jobs?query={query}`

No public feed or API located. Link only.

### Corporate Staffing Kenya — `link`
`https://corporatestaffing.co.ke/jobs/?q={query}`

No public feed located. Link only.

### Public Service Commission (Kenya) — `link`
`https://www.publicservice.go.ke/?s={query}`

Advertised vacancies live on `publicservice.go.ke` with no feed. **The URL above is a search-URL
guess** — fix the path once on the site and save your own version.

### Indeed Kenya — `link`, never fetched
`https://www.indeed.com/jobs?q={query}&l={location}`

Indeed's `robots.txt` disallows its job paths and its terms forbid automated collection. This source
is **never requested**. Link only; if a posting interests you, save it by hand.

### LinkedIn Jobs (Kenya) — `link`, never fetched
`https://www.linkedin.com/jobs/search?keywords={query}&location={location}`

LinkedIn's User Agreement forbids automated access and its `robots.txt` disallows `/jobs`. **Never
requested.** Link only.

### ReliefWeb — `json`, **disabled by default**
`https://api.reliefweb.int/v2/jobs?appname=RELIEFWEB_APPNAME&limit=100`

**VERIFIED 2026-10-06** — [apidoc.reliefweb.int](https://apidoc.reliefweb.int/):

- **v1 is decommissioned.** Only **v2** exists; `V2` is wire-compatible with the old v1.
- Since **1 November 2025** the `appname` parameter must be **pre-approved**. You request one via a
  short form and ReliefWeb emails it to you after review. Until then, requests are not served.
- Read-only API. No key in the traditional sense, but the approved appname is effectively a gate.

**Why it ships disabled:** the URL above still contains the `RELIEFWEB_APPNAME` placeholder. Get an
appname, replace the placeholder, then use *Test* on the Sources screen and enable it.

Field mapping confirmed against the ReliefWeb field tables. The list response is rooted at `data`:

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
```
item        div.joblist-item, div.card-body, li.job
title       a.job-title, h3 a, .job-title
company     .job-org, .field--name-organization
location    .job-location, .field--name-location
description .field--name-body, p
posted      .date, .field--name-created
```

**UNVERIFIED — no feed was located on the public listing pages.** The CSS selectors above are a
best guess against markup that changes regularly. Use *Detect* to preview the parsed rows, correct
the selectors, then enable it. Keep requests infrequent and read their terms before automating
anything. Disabled by default for that reason.

---

## Global remote pack

### Remotive — `json`, enabled
`https://remotive.com/api/remote-jobs?limit=100` · minimum interval **60 s**

**VERIFIED 2026-10-06** — [remotive.com/remote-jobs/api](https://remotive.com/remote-jobs/api) and
[github.com/remotive-com/remote-jobs-api](https://github.com/remotive-com/remote-jobs-api):

- Free, public, **no API key** or signup. One endpoint: `GET /remote-jobs`.
- **The array key is `jobs`, not `data`.** (An earlier version of this file had it wrong.)
- Optional filters: `limit`, `category`, `company_name`, `search`.

**Terms — these are conditions, not suggestions:**

- They block **more than 2 requests per minute** and advise **at most 4 fetches per day**. The seed
  therefore sets a 60-second minimum interval. resu-clean's default 2-second floor would get you
  blocked.
- Listings are **delayed 24 hours**.
- **Link back** to the original Remotive URL and **name Remotive as the source**, or access is
  terminated. resu-clean keeps each job's original URL and shows the source name, which satisfies
  this for jobs you view in the UI.
- **Do not republish** their jobs to other boards such as LinkedIn, Google Jobs or Jooble.
- The response contains a `"0-legal-notice"` key carrying the same terms.

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

**VERIFIED 2026-10-06** — [remoteok.com/legal](https://remoteok.com/legal) and their
[featurebase help article](https://remoteok.featurebase.app/help/articles/3140840):

- Free public feed, **no key, no signup, no OAuth**. `GET /api` returns a JSON array.
- **Element 0 of that array is not a job.** It is `{ "last_updated": …, "legal": "…" }`. Jobs start
  at index 1. resu-clean skips it naturally because it has no `position` field; there is a
  regression test pinning that behaviour.
- **Only the newest 100 postings are exposed.** No pagination, no search endpoint. The `?tags=`
  parameter mentioned in older wrapper libraries is silently ignored — it returns the identical
  list. Anything that scrolls out of that window is gone, so poll on a schedule rather than assuming
  history is queryable.
- Listings are delayed 24 hours.
- `/remote-jobs.rss` returns **HTTP 410 Gone** and is therefore not configured here. Only the JSON
  feed is used.

**Terms:** link back to the posting's URL on RemoteOK with a normal followed link — they say
explicitly *without `nofollow`* — and credit Remote OK as the source. Failing to attribute may
suspend API access. The logo is a registered trademark; the name is fine.

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

**UNVERIFIED since 2026-10-06** — the board has reorganised its URL scheme before, and older
`/remote-jobs.rss` paths have moved. It ships enabled because a feed is advertised and a wrong URL
fails safely with a clear error rather than doing anything harmful. Press *Test* on the Sources
screen to confirm; disable it if it stops working.

### Jooble — `json`, **disabled**
`https://jooble.org/api/{key}` · requires `JOOBLE_API_KEY`

**UNVERIFIED since 2026-10-06** — Jooble's API needs your own key under a paid or trial agreement,
so this ships disabled regardless. The exact path and response shape were not confirmed against their
current documentation. Put `JOOBLE_API_KEY` in `.env`, confirm the path in their docs, then fix the
field map via *Test* before enabling.

---

## What a search does with these

1. Resolves the URL for each enabled, non-`link` source.
2. Skips sources whose `requiresKey` env var is unset (reported by name).
3. Applies the per-source minimum interval.
4. Checks `robots.txt`; a disallow is reported, never fetched.
5. Serves from cache when fresh, otherwise fetches once and caches.
6. Parses per type into a common shape: title, company, location, url, posted date, description,
   remote flag.
7. **Deduplicates across sources** on canonical URL (tracking parameters stripped) then on
   normalised title + company, with fuzzy matching for near-duplicates and legal-suffix-insensitive
   company comparison. Seniority prefixes and bracketed locations are stripped so "Senior Data
   Analyst" and "Data Analyst (Nairobi)" collide correctly.
8. **Ranks** the survivors by keyword overlap against the resume version you selected, and keeps
   the richer record when two boards describe the same job.
9. Reports fetched / after-dedupe / saved counts, plus every source that was skipped or failed and
   why.

## Attribution is your obligation when you publish

resu-clean keeps each job's original URL and shows the source name, which satisfies the attribution
requirements of Remotive and RemoteOK for jobs you view in the UI. If you ever publish aggregated
output from those feeds, **complying with their terms is your responsibility, not the tool's** — in
particular the link-back requirement.