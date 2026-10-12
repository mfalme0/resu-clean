# Architecture

## Shape

One .NET 8 process serves the API and the built SPA. There is no separate frontend server, no
database server, no background worker service and no message queue.

```
browser ──▶  ASP.NET Core (Kestrel, :5177)
                 ├── /api/*          66 JSON endpoints
                 ├── /               Svelte SPA (static files)
                 ├── long ops        OpRunner + bounded Semaphore
                 │
                 ├── Services        all the logic, testable without HTTP
                 ├── Data            raw ADO.NET over SQLite (WAL)
                 └── data/           SQLite file, uploads, generated .docx/.pdf/.eml/.zip
```

In `npm run dev` there is a second process (Vite) purely for hot reload, proxying `/api` to the
backend. `npm start` runs only the backend.

## Layering

| Layer | Location | Rule |
|---|---|---|
| Endpoints | `server/Endpoints/` | Map routes, bind parameters, delegate to a service. No business logic. |
| Services | `server/Services/` | All the logic. Depends on `Data`, never on `Endpoints`. |
| Data | `server/Data/` | Schema and thin query helpers. |
| Models | `server/Models/` | DTOs, the single source of the API contract. |
| Api | `server/Api.cs` | JSON options, the error envelope, pagination helpers, streamed file results. |

Two rules keep this honest:

- **One error shape.** Every handler body runs inside `Api.Guard`, which maps exceptions to
  `{ "error": { "code", "message", "details" } }` with sensible status codes. Handlers return
  results, never throw for expected outcomes.
- **Endpoint lambdas are not `async`.** An `async (…) => Api.Guard(…)` handler returns a
  `Task<IResult>` that ASP.NET serialises as a serialised `Task` — HTTP 200, plausible body, no data.
  It shipped in 13 endpoints before `scripts/smoke-api.mjs` caught it. Handlers are plain lambdas
  returning `Api.Guard(...)`; only the inner lambda is `async`.

## Why raw ADO.NET instead of EF Core

The memory budget is the reason, and it is a real one:

- EF's change tracker retains every tracked entity for the request lifetime and keeps an identity map
  across a context. For a resume tool that reads a resume, scores it, writes one row and returns,
  that is pure overhead.
- Expression trees and compiled query plans allocate on first use per query shape, and the JIT keeps
  them alive.
- Every query here is parameterised and simple. `Microsoft.Data.Sqlite` plus a few helpers
  (`Db.Query`, `Db.QueryOne`, `Db.Execute`, `Db.Scalar`) covers it in about 200 lines.

SQLite runs in WAL mode with `synchronous=NORMAL`, `foreign_keys=ON`, `temp_store=MEMORY` and
`mmap_size=0`. Short-lived pooled connections, one per operation.

## Long operations

Clean, ATS, tailoring, job search and kit generation can take seconds. They return `202` with an
`opId`; the client polls `/api/ops/{id}`.

`OpRunner` writes the op row, then runs the work on a `Task.Run` behind a `SemaphoreSlim` sized by
`RESUCLEAN_MAX_CONCURRENCY` (default 2). **No background timers, no hosted services, no polling
loop** — nothing runs when no request is in flight. Completed ops older than a day are prunable.

Operation results are serialised with the same camelCase `JsonSerializerOptions` as the rest of the
API. Using default options here serialises PascalCase and the client silently receives objects with
no matching fields, which is exactly the bug `npm run smoke` now guards.

## Memory strategy

Target: under 60 MB idle, under 120 MB while processing. Measured numbers are in the README.

| Technique | Effect |
|---|---|
| `ServerGarbageCollection=false` | Workstation GC. Server GC adds per-core heaps and segments, and this app is single-request-at-a-time. |
| `ConcurrentGarbageCollection=false` | No background GC thread; reclaims synchronously. |
| `RetainVMGarbageCollection=true` | Returns freed pages to the OS instead of keeping them committed. |
| `TieredCompilationQuickJitForLoops` | Quick JIT on loops; less startup work and less retained JIT memory. |
| `EventSourceSupport=false` | No EventPipe subsystem resident. |
| `UseSystemResourceKeys` | No satellite resource lookup tables. |
| No ORM | No change tracker, no identity map, no compiled query plans. |
| Streamed I/O | Uploads go to disk via a stream; downloads use `Results.File` over a `FileStream` with range support. Nothing large is buffered twice. |
| No response caching of large objects | `AddResponseCaching` is not registered for API responses; static assets get long-lived `Cache-Control`, `index.html` gets `no-cache`. |
| One `HttpClient` per service | `SourceHttp` and `ProviderService` each own a single `SocketsHttpHandler` with pooled connections and `MaxConnectionsPerServer = 4`. |
| SQLite response cache, not memory | Fetched feeds are cached in a table, so no unbounded in-memory growth. |
| Paginated lists | Every list endpoint is capped at 100 per page. |
| Bounded ops semaphore | Concurrency cannot multiply peak memory. |

The biggest single cost is **PDF rendering**: QuestPDF loads font resources on first use, which is
most of the ~40 MB jump when generating the first kit. It is a one-off cost, and RSS does not grow
across subsequent kits.

## Where fabrication is prevented

This is the most important invariant in the codebase, enforced in four places:

1. **Prompt** (`ModelRouter.SystemPrompt`) — one system prompt per workflow, stating that the verified
   profile is the only source of truth, with explicit prohibitions on inventing employers, titles,
   dates, metrics and skills, and a per-workflow output format that forbids placeholders.
2. **Validation** (`ResumeWorkflows.Validate`) — before any model output reaches a caller, numbers,
   dates and unknown proper nouns are compared against the input. Invented numbers or dates discard
   the output entirely and the deterministic version is used instead. Unknown proper nouns are
   flagged for the user to check.
3. **Approval** (`ProfileService`) — information the model (or the user) claims as new is written to
   `pending_facts`, never to `profile_facts`. Only `ApproveFact` writes verified facts. Exact
   duplicates are caught by a normalised key; near-duplicates are fuzzy-matched (Jaccard over
   tokens, threshold 0.70) and flagged for review.
4. **Blocking** (`ResumeWorkflows.UpdateAsync`) — if a tailoring instruction contains unapproved
   information, the run stops and returns the pending items instead of writing a resume.

The deterministic paths (`Cleaner`, `AtsScorer`) only ever transform existing text: they normalise
   whitespace, strip icons, uppercase detected headings. They cannot add a fact because they never
   generate prose.

### The same rule, in the services added later

Fabrication prevention is not one guard, it is a shape that each new feature has to keep:

| Service | Rule | Why it exists |
|---|---|---|
| `RewriteGate` / `RewriteLedger` | A model rewrite is re-scored against the deterministic baseline and discarded if it does not improve; every decision is recorded | Otherwise "the model rewrote my resume" is unfalsifiable |
| `FactSupportService` | A claim with no verified fact behind it is queued for approval, never kept | The resume is the thing you get asked to defend |
| `GapRampService` | A suggestion with no `buildsOnFactId` naming a verified fact is dropped | "What should I learn" is the easiest prompt to answer with fiction |
| `JobQualityService` | A flag that is judgement about someone else's posting must carry a verbatim quote, or it is discarded | A caveat with no quote is a guess about another company's hiring policy |
| `SkillDictionaryService` | The proposer writes only to `pending_facts`; only approval touches the scoring dictionary | A scoring list is not the tool's to decide |

Every one of these has a test that asserts the *discard* path, not just the accept path. The accept
path is the easy half; the rule only means something if the other half is enforced.

**Where this deliberately does not apply: web search.** `WebSearchService` is the one place allowed to
emit numbers, dates and outside proper nouns, because it is quoting the web rather than describing
the user. Routing it through `Validate` would discard essentially every real result. So search output
is handled by a different rule: it is **quoted with its source URLs and structurally cannot write to
the profile** — `WebSearchService` holds no reference to `ProfileService` and no path to a fact write.
Two tests assert that absence rather than trusting the comment.

## Source fetching

`SourceHttp` owns robots.txt evaluation, per-host rate limiting, a SQLite response cache with TTL, and
one shared `HttpClient`. Evaluation is a pure function, `SourceHttp.Evaluate`, so the precedence rules
are testable offline: `Allow` and `Disallow` are matched as prefix patterns with `*` wildcards and a
trailing `$` anchor, and **the longest match across both lists wins, with `Allow` winning ties**. An
earlier version let any single matching `Allow` unlock the whole host, so `Allow: /public` next to
`Disallow: /public/secret` wrongly marked the secret path fetchable.
`SourceParser` normalises four source types into one `RawJob` shape, with a lenient XML fallback for
feeds that are not well-formed — which is most of them. `SourceDetector` probes a pasted URL and
classifies it. `Dedupe` collapses the same posting across boards.

`link` sources are never fetched. That is enforced in `JobSearchService`, not only in the UI.

Seeded URLs rot, so `SeedLivenessTests` resolves every seeded host under
`RESUCLEAN_LIVE_SOURCE_CHECK=1`. It is DNS-level only and never requests a page, which is what makes
it safe to run against hosts whose `robots.txt` forbids crawling.

## Frontend

Svelte 4 + Vite + TypeScript, plain CSS, no UI framework, no router library, no icon package,
no external fonts or CDNs.

- **Lazy routes.** Each screen is a dynamic `import()` in `lib/router.ts`, so the initial bundle
  carries only the shell and the Resume screen. Total output is 78.8 KB gzipped.
- **Design tokens** are CSS custom properties in `styles/tokens.css`; `styles/base.css` holds the
  Newsprint system (zero radius, ink-on-paper, collapsed grid borders, hard offset shadows, a
  dot-grid paper texture, a marquee ticker, drop caps). Dark mode is a `prefers-color-scheme`
  inversion of the same palette with no blur or gradients.
- **Accessibility.** Semantic landmarks, visible `:focus-visible` rings, `aria-selected` on tabs,
  `aria-live` status text for progress, 44 px minimum touch targets, and `prefers-reduced-motion`
  disabling the ticker and hover lifts.

## Testing

| Layer | What it covers | Why it exists |
|---|---|---|
| `tests/ResuClean.Tests` (315) | Cleaner, ATS weighting, no-fabrication guard, profile pending/dedupe, router fallback with mock providers, source parsing from fixtures, dedupe, `.eml`/`.zip`, key encryption, requirement classification, posting quality, gap ramps, interview grounding, the rewrite gate and ledger, tracker transfer, schema migration from an older database | The invariants above are worth pinning |
| `web/src/tests` (11) | Router, link interception, error envelope, op polling | Cheap confidence in the client contract |
| `scripts/smoke-api.mjs` (62) | Live HTTP against a throwaway database, asserting payloads are our JSON shapes | Caught the serialised-`Task` bug that 13 endpoints shipped with |

Source tests use **saved fixtures only** — no test touches the network. `TestHost` creates a real
SQLite database in a temp directory so services run exactly as they do in production.

The suite runs **serially** (`AssemblyInfo.cs` sets `DisableTestParallelization`). `Paths.DataDir` and
the approved skill dictionary are process-level state by design — one data folder for the whole app,
and a scoring list every scorer reads without a list threaded through it — and xUnit's default
parallelism had two `TestHost`s in flight at once pointing at different temp databases. It produced a
test that passed in isolation and failed in a full run, which is worse than a slow suite: it teaches
people to retry.