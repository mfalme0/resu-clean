# progress.md

Handoff, 12 Oct 2026. Read this first.

```
git log --oneline -5
969e8a4  Track when a posting was confirmed open, not when we saved it   <- pushed
213b6e5  Test the upgrade path, not just the fresh install                <- pushed
f2870b5  Stop matching requirement words inside longer words               <- pushed
d9d67c3  Add posting quality, gap ramps, interview prep and the gate     <- pushed
11ccafb  Add v1.0.0 release notes
```

**State right now:** builds clean (0 warnings), **404 backend tests pass**, 32 frontend,
67 smoke checks. Everything is committed.

---

## Where the project is

A self-hosted resume toolkit. Seven screens (Resume, Profile, Jobs, Search, Kits, Tracker,
Models), 13 model workflows, one SQLite file, no accounts, no telemetry, no email integration.

Two invariants hold the thing up:

1. **Nothing enters your verified profile without your approval.**
2. **Nothing a model says is taken on trust.** Rewrites are measured before you see them;
   a claim nothing backs is queued, not kept; a judgement about someone else's posting must
   quote that posting.

### Verified this session

- `npm run build` — clean
- `npm test` — 371 backend, 21 frontend, all green
- `npm run smoke` — 65 checks, all green
- Test suite is **serial** on purpose (`AssemblyInfo.cs`). `Paths.DataDir` and the approved
  skill dictionary are process-level state; xUnit's parallelism produced tests that passed alone
  and failed in a full run.

---

## Shipped and pushed

| Commit | What |
|---|---|
| `d9d67c3` | Phase-5 features wired end to end. Job quality, requirements, gap ramps, interview prep, rewrite gate + ledger, skill dictionary, tracker transfer, web search. Plus three failing-test fixes: staleness computed in local time (520-day posting read 519), eligibility missing "authorised", quotes returning matched words instead of the sentence. |
| `f2870b5` | `api` matched inside `capital`, so a finance CV satisfied "Strong API design". One boundary-aware matcher in `Keywords.ContainsTerm`, used by three scorers. |
| `213b6e5` | Migration test booting the real services against a pre-phase-5 database. |
| `969e8a4` | Verification and deadlines (Priority 1). |

### Priority 1 — verification (done, `969e8a4`)

Two facts that used to be conflated: **when a posting was saved** (your action) versus
**when it was last confirmed open** (the employer's system).

- `jobs.last_verified_at`, `deadline_at`, `status`
- `POST /api/jobs/{id}/verification`, badge on the job list
- **`unverifiable` is stored as its own status, never rounded up to `open`.** A 403 or a
  wrong-company redirect confirmed nothing.
- **A failed check does not advance the timestamp.** "We checked and could not tell" is not
  "we checked and it was there." `COALESCE` keeps the last good answer.
- `DeadlineReader` reads a stated closing date and a page saying it's closed. Found two bugs in
  itself: a blind `Replace("th","")` turning **March → Marc**, and `Replace("Sept","Sep")`
  turning **September → Sepember**.

**`Db.ApplyMigrations` now exists and matters.** `CREATE TABLE IF NOT EXISTS` is enough while
releases only add *tables*. The moment one adds a *column*, an existing database silently keeps
its old shape, the CREATE does nothing, and the query fails later on a user's machine with no
startup error to notice. One line per added column. `SchemaMigrationTests` asserts a new column
reaches an existing database.

---

## In flight — uncommitted, compiles, tests pass

Two pieces, both now **finished and ready to commit**.

### A. Tracker analysis tier — done

Your item 3.1. Hand-verified analysis now round-trips.

- `TrackerExportEntry` typed record: stage/note/applied/updated always, plus optional
  `url`, `posted`, `score` (number, not string), `verified`, `checked`, `deadline`, `gaps`
- Nulls omitted, not written as null — an absent key means "never determined", which must not
  look like a check that found nothing
- Import restores a recorded verdict **as-is, never re-derived**. An older file without the tier
  leaves an existing verdict alone rather than erasing it
- Unknown verdict → reported in `Skipped`, not stored
- Gaps fold into the note (prose belongs in prose)
- `GET /api/tracker/export?analysis=false` gives the old narrow shape
- **File format preserved**: lowercase camelCase keys, same shape. The pre-existing tests caught
  me when I broke all 15 of them mid-way.

### B. Source provenance — done

Your item 2.3. An aggregator is a convenience, not evidence.

- `SourceProvenance` — a pure, offline-testable classifier by host. Recognises ~22 ATS hosts
  (Greenhouse in all three forms, Lever, Ashby, Workday, Workable, SmartRecruiters), ~17
  aggregators, ~11 boards. **Suffix match respects the dot**, so `evil-greenhouse.io` is not
  Greenhouse — with a test pinning both halves of that pair.
- Unrecognised host → `unknown`, never guessed. A default that reads as confidence is what
  produced the original problem.
- `job_sources.provenance` column + migration; `SourceDto`, `UpsertSourceRequest`,
  `DetectSourceResponse` all carry it
- Detection infers it **before fetching anything**, so the user sees it at decision time
- Seed packs stamp it automatically via `Entry()` — the Greenhouse pack is `employer_ats` with no
  hand-written annotation, which means it cannot drift from the rule
- **Drift guard test**: any seeded URL whose host the classifier recognises must carry the matching
  verdict. A new pack entry added without one fails the suite.
- UI: "Source of truth" column on Sources, badge at detection time, sentence on hover
- Frontend narrows the untrusted server value with `asProvenance()` rather than casting it

Note: the Kenya pack is mostly `unknown`. Those are `link` sources — search URLs only, never
fetched — so there is no fetchable host to judge. That is the honest answer.

---

## Not started

- **Skill dictionary seeding (your 3.2) — operational, yours, ten minutes.** Proxmox, TrueNAS,
  Business Central, Power Automate and Grafana are all absent from the built-in list and all
  genuinely yours. Until approved, scoring surfaces "HVAC" as missing and ignores real skills.
  Profile → Scoring dictionary → Find tools I keep using.
- **ARM64 — deliberately untouched, and the best contribution point.** Add the RID to `RIDS`
  in `scripts/publish.mjs`, run it on the hardware, report what breaks. It needs real hardware,
  so I left it rather than ship untested binaries.

## Still on the v2 list (scope, not bugs)

Per-company cover letter tone · application analytics by source (response rates — the ledger
already counts *model* accept rates; the tracker has the timestamps for board rates) ·
interview prep that rehearses rather than just listing · resume variants auto-tagged by
seniority · `--watch` daemon · encrypted export of the whole data dir · iCalendar export ·
Tauri launcher.

---

## Lessons from this stretch, worth keeping

- **`CreatedRegex` cannot reference a `const`** referenced from the attribute, and `@"..." + @"..."`
  does not concatenate inside an attribute. Inline one long verbatim string. I lost time on this
  three times; the symptom is `CS8795: must have an implementation part`.
- **I wrote a test asserting headings cannot raise an ATS score, then started "fixing" the
  scorer.** The scorer was right — adding a real `SKILLS` section is a genuine improvement.
  Reverted `AtsScorer.cs`; fixed the test instead. When a test fails, check which of the two
  is wrong before editing the source.
- **`AtsScorer.Score("v", "")` returns 17, not 0.** Deliberate. The test now asserts a range.
- The headings check is binary (passes at one missing heading). Adding the second of three
  missing sections does not move the total — assert on the check's detail, not the score.
- `TestHost` generates job ids; never hardcode `job_1` after `SaveManualJob`.

## Two boundaries that must not be relaxed

1. **Search output never reaches the verified profile.** `WebSearchService` holds no reference
   to `ProfileService` and a test asserts that absence from the source text.
2. **A cover letter is written only after three qualifying questions are answered.** No scorer
   in this repo will notice a posting is a junior role wearing a senior title.

---

## First three things to do

```bash
npm run build && npm test        # expect 0 warnings, 404 passing
```

1. Seed your own skill dictionary terms — the highest-leverage ten minutes left.
2. Audit `SkillDictionaryService` and `WebSearchService` for the same silent-wrongness class as
   the matcher bug in `f2870b5`. Both are new, both decide things quietly, neither has been
   reviewed against real input.
3. `RESUCLEAN_LIVE_SOURCE_CHECK=1 npm test -- --only=api` before any release — board URLs rot
   silently, which is how `jobweb.co.ke` and `cpoint.co.ke` died unnoticed.