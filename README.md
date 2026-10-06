# resu-clean

A self-hosted resume toolkit. It cleans your resume, scores it against ATS rules, keeps a verified
profile of facts you can vouch for, tailors versions to real job postings, searches job boards, and
assembles application kits you send yourself.

Everything runs on your machine. There is no account, no telemetry, and **no email integration** —
the tool produces files, you press send.

```
┌─ Resume ──────────┬─ Profile ─────────┬─ Jobs ───────────┬─ Kits ──────────┬─ Models ─────────┐
│ paste / upload    │ verified facts    │ search sources   │ .eml + .zip     │ 9 presets        │
│ clean             │ pending approvals  │ dedupe + rank    │ mailto + copy   │ fetch models     │
│ ATS score + tips  │ voice & contact    │ packs            │ tracker         │ route + fallback │
└───────────────────┴───────────────────┴──────────────────┴──────────────────┴──────────────────┘
```

---

## Contents

- [Requirements](#requirements)
- [Setup](#setup)
- [The three run modes](#the-three-run-modes)
- [Memory](#memory)
- [Works with no model](#works-with-no-model)
- [Adding a source](#adding-a-source)
- [Adding a model](#adding-a-model)
- [The API](#the-api)
- [Configuration](#configuration)
- [Tests](#tests)
- [Security note](#security-note)
- [Troubleshooting](#troubleshooting)
- [What's left for v2](#whats-left-for-v2)

---

## Requirements

| | Version | Notes |
|---|---|---|
| Node.js | **20+** | Only needed for the npm scripts and the UI build |
| .NET SDK | **8.0** | Any later SDK works; the project targets `net8.0` |

Nothing else. No database server, no message queue, no Redis.

`npm run preflight` checks both and tells you exactly what is missing:

```
$ npm run preflight
[preflight] Node 24.18.0 (ok, need >=20)
[preflight] .NET SDK 8.0 available (targets: 8.0, 10.0)
[preflight] All requirements satisfied.
```

## Setup

```bash
npm install          # root: no dependencies, just scripts
cd web && npm install && cd ..   # UI dependencies, first run only
npm run build        # builds the UI into server/wwwroot and compiles the backend
npm start            # http://127.0.0.1:5177
```

The first run creates `data/`, the SQLite database, and a `.env` copied from `.env.example`.
`.env` is gitignored and never committed.

## The three run modes

### `npm run dev` — hot reload, for working on it

Starts `dotnet watch` and the Vite dev server together with an `/api` proxy. Ctrl+C stops both.

```
[api] watch : Started
[web] VITE ready in 312 ms
→ Local: http://localhost:5173/
```

Use the Vite URL. Changes to the UI reload instantly; changes to C# restart the backend.

### `npm start` — everyday mode, lowest RAM

Builds the UI **once**, then runs only the backend, which serves both the API and the UI from a
single port (5177 by default, set `RESUCLEAN_PORT` to change it). This is the mode you want day
to day: no file watchers, no second process, no dev-server overhead.

```bash
npm run build    # once
npm start
```

### `npm run publish` — a single self-contained executable

Publishes a framework-dependent-free build into `artifacts/resu-clean-<rid>/`. Copy that folder
anywhere and run the `ResuClean` executable — **the target machine does not need .NET installed**.

```bash
npm run publish
# → artifacts/resu-clean-win-x64/
```

Useful for a USB stick, or running it on a machine you do not want to install anything on.

## Memory

Measured on Windows 11, .NET 8, workstation GC, no model configured, via
`npm run mem:measure` (which starts its own backend on a spare port against a throwaway data dir):

| Stage | RSS | Managed heap |
|---|---|---|
| idle (just started) | **57.1 MB** | 17.4 MB |
| after resume intake | 57.9 MB | 17.4 MB |
| after a resume clean | 60.6 MB | 19.1 MB |
| after an ATS scan | 61.8 MB | 19.6 MB |
| after a job search | 63.4 MB | 19.9 MB |
| after an application kit | **97.4 MB** | 38.8 MB |

Budget was <60 MB idle and <120 MB while processing: met. The honest note is that **rendering a
PDF is what costs the memory** — QuestPDF loads font resources on first use, which is most of that
40 MB jump. After the first kit the process settles, and RSS does not grow further across repeated
kits. Check the live figure any time with:

```bash
npm run mem
```

```
  backend          port 5177
  RSS              57.1 MB   95% of the 60 MB idle target
  managed heap     17.4 MB
  peak RSS         57.1 MB
  requests         12
  ops running      0
```

**Frontend bundle: 78.8 KB gzipped** across 19 files (budget was 150 KB). Achieved with lazy-loaded
routes, no UI framework, system fonts, hand-rolled inline SVG icons, and plain CSS.

How it stays small: workstation GC (not server GC), no ORM or change tracker, raw parameterised ADO.NET,
streamed file I/O instead of buffering, a single shared `HttpClient` per service, responses for large
documents streamed from disk with range support, every list endpoint paginated, and long operations
run under a bounded semaphore so concurrency cannot multiply memory.

## Works with no model

This is a design constraint, not a limitation. Out of the box, with zero configuration:

| Feature | Needs a model? |
|---|---|
| Deterministic clean (unicode, bullets, icons, whitespace, dates) | **no** |
| ATS rule scan, score, per-check tips | **no** |
| Keyword matching against a job description | **no** |
| Job search, dedupe, ranking | **no** |
| Template application email, .eml, .zip | **no** |
| Model rewrite for clarity | yes |
| Prioritised ATS fixes written out | yes |
| Tailoring a resume to a posting | yes |
| Written application email / cover letter | yes |

`GET /api/capabilities` reports which is which, and the UI says so on screen rather than failing.

## Adding a source

Sources live in a **registry in the database**, not in code. Nothing is fetched until you enable it.

**Easiest: paste a URL.** Jobs → Sources → *Add a source by URL* → **Detect**. The tool will:

1. check `robots.txt` first and refuse to fetch anything disallowed,
2. fetch the page and classify it as `rss`, `json`, `html` or `link`,
3. if it is HTML, look for `<link rel="alternate" type="application/rss+xml">` and probe common
   paths like `/feed`, `/rss.xml`, `/jobs/feed`,
4. parse the result and show you a **preview of real rows** before anything is saved.

Then press *Save this source*. If detection cannot find a feed, you are told what to do next:
supply CSS selectors, or save it as a `link` source.

**The four types:**

| Type | What it does | When |
|---|---|---|
| `rss` | Parses RSS/Atom | The board publishes a feed |
| `json` | Maps named JSON fields | There is a documented API |
| `html` | Uses your CSS selectors | There is a listing page you are allowed to read |
| `link` | **Never fetched.** Builds the search URL, you open it | No feed/API, or automated access is restricted |

**Seed packs** (Jobs → Packs) install a set of starting points you can enable, disable or delete. The
Kenya pack is mostly `link` by design — those boards do not document an API and several forbid
automated collection. See [docs/SOURCES.md](docs/SOURCES.md) for the researched status of every
single source, with the date checked.

**Field mapping** for a `json` source maps dotted paths to fields:

```json
{
  "_root": "results",
  "title": "title",
  "company": "organization.name",
  "location": "location.name",
  "url": "url_alias",
  "description": "body",
  "posted": "date.created"
}
```

`_root` is the array to iterate. Leave it out if the response root is already an array.

**CSS selectors** for an `html` source:

```json
{
  "item": "li.job-card",
  "title": "h3.title a",
  "company": ".company",
  "location": ".location",
  "posted": ".posted",
  "description": ".summary"
}
```

**Be a good guest.** Every source carries its own minimum interval between requests (a seed pack
will set this to 60s for a board that blocks aggressive clients), responses are cached for
`RESUCLEAN_CACHE_TTL_SECONDS`, and the User-Agent identifies the tool honestly. The tool never logs
in, never bypasses a paywall or bot protection, and never sends your resume anywhere.

**Saving a job by hand** (Jobs → Add a job) is a first-class path, not a workaround: paste any
posting's text and it enters the same pipeline for kits and tracking.

## Adding a model

Models → *Add a provider* → pick a **preset**. Nine are built in:

| Preset | Type | Base URL |
|---|---|---|
| OpenAI | openai | `https://api.openai.com/v1` |
| Anthropic | anthropic | `https://api.anthropic.com` |
| OpenRouter | openai | `https://openrouter.ai/api/v1` |
| Groq | openai | `https://api.groq.com/openai/v1` |
| Google Gemini | openai | `https://generativelanguage.googleapis.com/v1beta/openai` |
| NVIDIA NIM | openai | `https://integrate.api.nvidia.com/v1` |
| Clean APIs | openai | `https://cleanapis.com/v1` |
| Ollama (local) | openai | `http://127.0.0.1:11434/v1` |
| LM Studio (local) | openai | `http://127.0.0.1:1234/v1` |

Selecting one fills in the type, base URL, environment variable and a link to where you get a key.
Then either paste a key or point at an environment variable.

**Press "Fetch models from the provider"** and it calls the provider's own model-list endpoint and
lists the real ids so you can tick the ones you want instead of guessing. This handles the three
response shapes in the wild (OpenAI-style `data[].id`, Gemini's `models[].name`, Ollama's
`data[].name`), strips Gemini's `models/` prefix, and hides non-chat models like embeddings and
image generators, which cannot be routed. If you save with no models listed, it tries once on your
behalf; if that fails it says so and you can type ids by hand.

**Route each workflow.** Six workflows (`clean`, `ats`, `profile`, `update_resume`, `find_jobs`,
`apply_email`) each take an **ordered list** of provider/model pairs. The first is tried; if the key
is wrong, the model name is wrong or the provider errors, the router moves to the next and tells you
why. With no working entry the workflow returns its deterministic result instead of failing.

Keys are encrypted at rest (DPAPI on Windows, AES-256 with a machine-local key file elsewhere) and
**no endpoint ever returns one** — not even the list of providers, which only shows `hasKey` and
where the key came from. Prefer an environment variable in `.env`: then the key never touches the
database.

Full details in [docs/MODELS.md](docs/MODELS.md).

## The API

Everything lives under `/api`, JSON in and out. 66 endpoints:

| Group | Base | What it does |
|---|---|---|
| Resumes | `/api/resumes` | Intake (paste, `.txt`, `.docx`, `.pdf`), versions, clean, ATS, tailor, export |
| Profile | `/api/profile` | Facts, pending approvals, voice, contact |
| Sources | `/api/sources` | Registry, URL detection, dry-run test, seed packs |
| Jobs | `/api/jobs` | Search, saved jobs, manual save, search-URL builder |
| Kits | `/api/kits` | Generate a kit; download `.eml` / `.zip` / `.txt` / cover `.pdf`; `mailto:` |
| Tracker | `/api/tracker` | Application statuses and notes |
| Models | `/api/providers`, `/api/routes` | Providers, presets, model discovery, per-workflow routing |
| System | `/api/health`, `/api/capabilities`, `/api/metrics/mem`, `/api/ops/{id}` | Health, capabilities, memory, operation status |

**Long operations return `202` with an `opId`.** Cleaning, ATS scans, tailoring, job searches and kit
generation can take seconds, so they do not block the request:

```bash
curl -X POST localhost:5177/api/resumes/versions/$VER/clean \
     -H 'Content-Type: application/json' -d '{"useModel":false}'
# 202 {"opId":"op_0bf09281ef8e","status":"queued","kind":"clean"}

curl localhost:5177/api/ops/op_0bf09281ef8e
# 200 {"state":"done","progress":100,"result":{...}}
```

Poll `/api/ops/{id}` until `state` is `done` or `failed`. The UI does this for you.

Errors always use the same envelope, so you can rely on the shape:

```json
{ "error": { "code": "not_found", "message": "No resume called 'res_x'." } }
```

Every list endpoint is paginated with `?page=&size=` and returns
`{ items, page, size, total, pages, hasMore }`.

Dev-only `GET /api/openapi.json` lists every route. Swagger UI is deliberately absent: it would need
a CDN, and this app works fully offline.

## Configuration

All via `.env` (see `.env.example`):

| Variable | Default | Meaning |
|---|---|---|
| `RESUCLEAN_PORT` | `5177` | Single port for API + UI in `npm start` |
| `RESUCLEAN_HOST` | `127.0.0.1` | Bind address |
| `RESUCLEAN_API_KEY` | *(unset)* | If set, every `/api` call needs header `X-API-Key` |
| `RESUCLEAN_CORS` | *(unset)* | Extra allowed origins, comma-separated |
| `RESUCLEAN_MAX_UPLOAD_MB` | `10` | Upload cap |
| `RESUCLEAN_MAX_CONCURRENCY` | `2` | Long operations running at once |
| `RESUCLEAN_CACHE_TTL_SECONDS` | `900` | How long a fetched feed is reused |
| `RESUCLEAN_SOURCE_MIN_INTERVAL_MS` | `2000` | Floor between requests to one host |
| `RESUCLEAN_USER_AGENT` | identifies the tool | Sent to job sources |
| `RESUCLEAN_DATA_DIR` | `data` | SQLite DB, generated documents, uploads |

## Tests

```bash
npm test              # backend + frontend
npm test -- --only=api
npm run smoke         # live API smoke test against a throwaway database
npm run mem:measure   # regenerate the memory table above
```

- **165 backend tests** — cleaner, ATS scoring and the 0–100 weighting, the no-fabrication guard,
  profile pending/dedupe/approval, router fallback with mock models, source parsing and field
  mapping from saved fixtures (no network), cross-source dedupe, `.eml`/`.zip` generation, key
  encryption at rest.
- **11 frontend tests** — router, link interception, error envelope, op polling.
- **40 API smoke checks** — real HTTP against a running backend, asserting payloads are actually our
  JSON shapes. This exists because it caught a bug where 13 endpoints returned HTTP 200 with a
  serialised `Task` instead of their data.

## Security note

- **Binds to `127.0.0.1` and is open.** Anyone with access to your machine's loopback can use it.
  Before exposing the port to a network, set `RESUCLEAN_API_KEY` — then every `/api` request must
  carry `X-API-Key`. The UI stores the key in `localStorage` and prompts once.
- **API keys are never returned by any endpoint**, are encrypted at rest, and never appear in logs.
  Prefer an environment variable over storing one.
- **Resume content is never logged.** Errors log a type and message, never your data.
- **Nothing is ever sent on your behalf.** There is no email integration, no SMTP, no Gmail, no
  OAuth. A kit is a set of files on disk plus a `mailto:` link; pressing send is your action.
- **Your resume does not leave your machine** except to the job boards you choose to search.
- **No fabricated facts, ever.** Model output is validated before you see it: any number, date or
  unknown proper noun not present in your input causes the output to be discarded and the
  deterministic version used instead. New information is queued for approval and never silently
  added to your profile.
- **`.env` is gitignored.** If you set `RESUCLEAN_API_KEY`, keep it there, never in a committed file.

## Troubleshooting

**Port already in use** — change `RESUCLEAN_PORT` in `.env`. It takes effect for both `npm start` and
`npm run dev` (Vite's own port is set in `web/vite.config.ts`).

**"The .NET SDK was not found"** — install .NET 8 from <https://dotnet.microsoft.com/download/dotnet/8.0>
and open a new terminal so PATH updates.

**A source returns nothing** — press *Test* on the Sources screen. It tells you which of the four
failure modes you hit: robots.txt disallow, HTTP error, empty body, or "fetched fine but parsed
nothing" (which means your field mapping or selectors need fixing).

**A model workflow says no model was available** — check the Routes screen for that workflow. Either
no route is set, the provider has no key, or every entry failed. The reason is listed per entry.

**PDF export looks wrong** — the resume text is the source of truth, and `.docx`/`.pdf` are
rendered from it. If the source has odd spacing, clean it first.

**Everything is slow** — you are probably on `npm run dev` (watchers + HMR). `npm start` is the
low-RAM mode.

## What's left for v2

- Cover letter and email tone as first-class, per-company preferences rather than one global note
- Interview prep: questions generated from a job description, grounded in your profile
- Application analytics: response rates per source, so you can see which boards actually work for you
- Import from LinkedIn/Indeed via manual paste with better structuring
- Multiple resume variants auto-tagged by seniority and role family
- A `--watch` daemon mode that polls enabled feeds on a schedule and diffs for new postings
- Encrypted-at-rest export/import of the whole data directory, for backup and machine moves
- iCalendar export for interview stages
- Optional Windows/macOS launcher (Tauri shell) around the existing executable

---

## Licence

MIT. See [LICENSE](LICENSE).

## Documentation

- [docs/SOURCES.md](docs/SOURCES.md) — every job source, researched, with robots/terms verdicts
- [docs/MODELS.md](docs/MODELS.md) — provider presets, model discovery, routing and fallback
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — layering, memory strategy, no-fabrication enforcement