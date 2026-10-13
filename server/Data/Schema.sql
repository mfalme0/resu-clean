-- resu-clean schema. Applied idempotently at startup (see Db.cs).
-- No ORM: plain ADO.NET over Microsoft.Data.Sqlite to keep the working set small.

CREATE TABLE IF NOT EXISTS settings (
  key        TEXT PRIMARY KEY,
  value      TEXT NOT NULL
);

-- A resume is the container; each edit becomes a version.
CREATE TABLE IF NOT EXISTS resumes (
  id           TEXT PRIMARY KEY,
  label        TEXT NOT NULL,
  created_at   TEXT NOT NULL,
  updated_at   TEXT NOT NULL,
  current_version_id TEXT
);

CREATE TABLE IF NOT EXISTS resume_versions (
  id            TEXT PRIMARY KEY,
  resume_id     TEXT NOT NULL REFERENCES resumes(id) ON DELETE CASCADE,
  label         TEXT NOT NULL,
  text          TEXT NOT NULL,
  source_kind   TEXT NOT NULL,              -- paste | txt | docx | pdf
  parent_id     TEXT,
  best_fit      INTEGER NOT NULL DEFAULT 0,
  created_at    TEXT NOT NULL,
  docx_path     TEXT,
  pdf_path      TEXT
);
CREATE INDEX IF NOT EXISTS ix_versions_resume ON resume_versions(resume_id, created_at DESC);

-- Character profile: the single source of truth other workflows may cite.
CREATE TABLE IF NOT EXISTS profile (
  id          INTEGER PRIMARY KEY CHECK (id = 1),
  voice       TEXT NOT NULL DEFAULT '',
  preferences TEXT NOT NULL DEFAULT '',
  contact     TEXT NOT NULL DEFAULT '{}',
  updated_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS profile_facts (
  id          TEXT PRIMARY KEY,
  kind        TEXT NOT NULL,                -- role | skill | achievement | education | cert | contact | other
  text        TEXT NOT NULL,
  meta        TEXT NOT NULL DEFAULT '{}',
  origin      TEXT NOT NULL DEFAULT 'manual',
  match_key   TEXT NOT NULL,                -- normalized text for exact dedupe
  verified_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_facts_kind ON profile_facts(kind);

-- Nothing new enters profile_facts without an explicit approval decision.
CREATE TABLE IF NOT EXISTS pending_facts (
  id          TEXT PRIMARY KEY,
  kind        TEXT NOT NULL,
  text        TEXT NOT NULL,
  meta        TEXT NOT NULL DEFAULT '{}',
  origin      TEXT NOT NULL,                -- profile_editor | resume_update | clean | ...
  context     TEXT NOT NULL DEFAULT '',
  match_key   TEXT NOT NULL,
  status      TEXT NOT NULL DEFAULT 'pending',   -- pending | approved | rejected
  created_at  TEXT NOT NULL,
  decided_at  TEXT
);
CREATE INDEX IF NOT EXISTS ix_pending_status ON pending_facts(status, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_pending_key ON pending_facts(match_key);

-- Job sources live in the DB, not in code. Packs are JSON seeds the user installs.
CREATE TABLE IF NOT EXISTS job_sources (
  id            TEXT PRIMARY KEY,
  name          TEXT NOT NULL,
  type          TEXT NOT NULL,              -- rss | json | html | link
  url           TEXT,
  url_template  TEXT,
  regions       TEXT NOT NULL DEFAULT '[]',
  enabled       INTEGER NOT NULL DEFAULT 1,
  rate_limit_ms INTEGER NOT NULL DEFAULT 2000,
  field_map     TEXT NOT NULL DEFAULT '{}',-- json type only
  selectors     TEXT NOT NULL DEFAULT '{}',-- json/html only
  requires_key  TEXT NOT NULL DEFAULT '',
  notes         TEXT NOT NULL DEFAULT '',
  -- Where this source's postings come from, and therefore who is authoritative about whether a role
  -- is still open: employer_ats (the company's own board, the only one that can 404), aggregator
  -- (a third-party index, a convenience and not evidence), board (a general listings site), unknown.
  provenance    TEXT NOT NULL DEFAULT 'unknown',
  created_at    TEXT NOT NULL,
  updated_at    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_sources_enabled ON job_sources(enabled);

CREATE TABLE IF NOT EXISTS source_packs (
  id           TEXT PRIMARY KEY,
  name         TEXT NOT NULL,
  description  TEXT NOT NULL DEFAULT '',
  installed_at TEXT
);

CREATE TABLE IF NOT EXISTS jobs (
  id            TEXT PRIMARY KEY,
  title         TEXT NOT NULL,
  company       TEXT NOT NULL DEFAULT '',
  location      TEXT NOT NULL DEFAULT '',
  url           TEXT NOT NULL DEFAULT '',
  posted_at     TEXT,
  description   TEXT NOT NULL DEFAULT '',
  source_id     TEXT,
  source_name   TEXT NOT NULL DEFAULT '',
  match_pct     INTEGER NOT NULL DEFAULT 0,
  remote        INTEGER NOT NULL DEFAULT 0,
  is_manual     INTEGER NOT NULL DEFAULT 0,
  best_version_id TEXT,
  raw           TEXT NOT NULL DEFAULT '{}',
  -- When this posting was last confirmed to still exist, as opposed to when we first saved it.
  -- A posting carried as live for three sessions is the failure this exists to prevent: nothing
  -- ever asked the employer's system again, so "we applied to this" quietly became "this is open".
  -- NULL means never verified, which is not the same as verified and never was.
  last_verified_at TEXT,
  -- The closing date the posting states, where one can be read out of the text. NULL means no
  -- deadline was stated, which is information rather than an absence.
  deadline_at   TEXT,
  -- open | closed | unverifiable, from a check against the employer's own system. "unverifiable"
  -- is stored rather than rounded up to open: a 403 or a wrong-company redirect confirmed nothing,
  -- and a tracker that calls those live is the false confidence this whole column set removes.
  status        TEXT NOT NULL DEFAULT '',
  created_at    TEXT NOT NULL,
  UNIQUE(url, title, company)
);
CREATE INDEX IF NOT EXISTS ix_jobs_created ON jobs(created_at DESC);
CREATE INDEX IF NOT EXISTS ix_jobs_match ON jobs(match_pct DESC);

CREATE TABLE IF NOT EXISTS search_runs (
  id          TEXT PRIMARY KEY,
  query       TEXT NOT NULL,
  location    TEXT NOT NULL DEFAULT '',
  remote_only INTEGER NOT NULL DEFAULT 0,
  version_id  TEXT,
  created_at  TEXT NOT NULL,
  stats       TEXT NOT NULL DEFAULT '{}'
);

CREATE TABLE IF NOT EXISTS kit_emails (
  id          TEXT PRIMARY KEY,
  job_id      TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
  version_id  TEXT NOT NULL,
  subject     TEXT NOT NULL,
  body        TEXT NOT NULL,
  cover_letter TEXT NOT NULL DEFAULT '',
  to_address  TEXT NOT NULL DEFAULT '',
  apply_url   TEXT NOT NULL DEFAULT '',
  generated_by TEXT NOT NULL DEFAULT 'template',
  created_at  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_kits_job ON kit_emails(job_id, created_at DESC);

CREATE TABLE IF NOT EXISTS applications (
  id          TEXT PRIMARY KEY,
  job_id      TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
  kit_id      TEXT,
  version_id  TEXT NOT NULL DEFAULT '',
  status      TEXT NOT NULL DEFAULT 'prepared', -- prepared | sent | replied | interview | rejected | closed
  notes       TEXT NOT NULL DEFAULT '',
  -- The date you actually applied, which is not the same as created_at. created_at is when the row
  -- was written, so an imported or hand-added entry always has today's date; reading that as "you
  -- applied today" invents an application you never made. Null means not known, and stays null.
  applied_at  TEXT,
  created_at  TEXT NOT NULL,
  updated_at  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_apps_status ON applications(status, updated_at DESC);

CREATE TABLE IF NOT EXISTS providers (
  id          TEXT PRIMARY KEY,
  name        TEXT NOT NULL,
  type        TEXT NOT NULL,              -- anthropic | openai
  base_url    TEXT NOT NULL,
  env_var     TEXT NOT NULL DEFAULT '',
  api_key_enc TEXT NOT NULL DEFAULT '',
  models      TEXT NOT NULL DEFAULT '[]',
  enabled     INTEGER NOT NULL DEFAULT 1,
  created_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS routes (
  workflow    TEXT NOT NULL,
  ordinal     INTEGER NOT NULL,
  provider_id TEXT NOT NULL REFERENCES providers(id) ON DELETE CASCADE,
  model       TEXT NOT NULL,
  PRIMARY KEY (workflow, ordinal)
);

CREATE TABLE IF NOT EXISTS ops (
  id          TEXT PRIMARY KEY,
  kind        TEXT NOT NULL,
  state       TEXT NOT NULL,              -- queued | running | done | failed
  payload     TEXT NOT NULL DEFAULT '{}',
  result      TEXT NOT NULL DEFAULT '',
  error       TEXT NOT NULL DEFAULT '',
  created_at  TEXT NOT NULL,
  finished_at TEXT
);

-- Cache for fetched feeds/pages so repeated searches stay cheap and polite.
CREATE TABLE IF NOT EXISTS http_cache (
  url        TEXT PRIMARY KEY,
  body       TEXT NOT NULL,
  status     INTEGER NOT NULL,
  fetched_at TEXT NOT NULL
);

-- Model replies keyed by content hash (workflow + system + user + provider + model). Same input,
-- same answer, no second call. On a local Ollama box this is about iteration speed, not money.
CREATE TABLE IF NOT EXISTS model_cache (
  key         TEXT PRIMARY KEY,
  workflow    TEXT NOT NULL,
  provider_id TEXT NOT NULL DEFAULT '',
  model       TEXT NOT NULL DEFAULT '',
  response    TEXT NOT NULL,
  created_at  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_model_cache_workflow ON model_cache(workflow, created_at DESC);

-- Token spend per model call. Memory is tracked meticulously in this app and models are the one
-- remaining unbounded resource, so they get the same discipline.
CREATE TABLE IF NOT EXISTS model_usage (
  id                TEXT PRIMARY KEY,
  workflow          TEXT NOT NULL,
  provider_id       TEXT NOT NULL DEFAULT '',
  provider_name     TEXT NOT NULL DEFAULT '',
  model             TEXT NOT NULL DEFAULT '',
  prompt_tokens     INTEGER NOT NULL DEFAULT 0,
  completion_tokens INTEGER NOT NULL DEFAULT 0,
  cached            INTEGER NOT NULL DEFAULT 0,  -- 0/1: served from model_cache
  elapsed_ms        INTEGER NOT NULL DEFAULT 0,
  created_at        TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_model_usage_created ON model_usage(created_at DESC);
CREATE INDEX IF NOT EXISTS ix_model_usage_workflow ON model_usage(workflow, created_at DESC);

-- Every model rewrite, judged. Accepting or rejecting a rewrite on a measured score rather than on
-- taste is the point; recording it is what makes "which model rewrites actually helped" answerable.
CREATE TABLE IF NOT EXISTS rewrite_evaluations (
  id               TEXT PRIMARY KEY,
  workflow         TEXT NOT NULL,
  provider_id      TEXT NOT NULL DEFAULT '',
  provider_name    TEXT NOT NULL DEFAULT '',
  model            TEXT NOT NULL DEFAULT '',
  version_id       TEXT NOT NULL DEFAULT '',
  baseline_score   INTEGER NOT NULL DEFAULT 0,
  candidate_score  INTEGER NOT NULL DEFAULT 0,
  baseline_missing INTEGER NOT NULL DEFAULT 0,
  candidate_missing INTEGER NOT NULL DEFAULT 0,
  accepted         INTEGER NOT NULL DEFAULT 0,   -- 0/1
  reason           TEXT NOT NULL DEFAULT '',
  created_at       TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_rewrites_created ON rewrite_evaluations(created_at DESC);
CREATE INDEX IF NOT EXISTS ix_rewrites_workflow ON rewrite_evaluations(workflow, created_at DESC);

-- Grounding reports. Kept so a claim that was flagged once can be shown again later, and so a
-- resume can be re-checked after the profile grows rather than only at the moment it was written.
CREATE TABLE IF NOT EXISTS fact_check_reports (
  id           TEXT PRIMARY KEY,
  version_id   TEXT NOT NULL DEFAULT '',
  model        TEXT NOT NULL DEFAULT '',
  method       TEXT NOT NULL DEFAULT '',       -- model | deterministic
  checked      INTEGER NOT NULL DEFAULT 0,
  ungrounded   INTEGER NOT NULL DEFAULT 0,
  claims       TEXT NOT NULL DEFAULT '[]',
  created_at   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_factcheck_created ON fact_check_reports(created_at DESC);
CREATE INDEX IF NOT EXISTS ix_factcheck_version ON fact_check_reports(version_id, created_at DESC);

-- What a posting actually demands, split by how hard it is to close. This is the table that lets
-- the job list say "you cannot win this one" instead of "you are missing seven keywords".
CREATE TABLE IF NOT EXISTS job_requirements (
  id         TEXT PRIMARY KEY,
  job_id     TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
  text       TEXT NOT NULL,
  class      TEXT NOT NULL,              -- blocker | required | preferred | implicit
  met        INTEGER NOT NULL DEFAULT 0, -- 0/1 against the verified profile and resume
  missing    TEXT NOT NULL DEFAULT '[]',
  evidence   TEXT NOT NULL DEFAULT '',   -- the posting's own wording, quoted
  created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_requirements_job ON job_requirements(job_id, class, met);

-- Terms you use that the built-in keyword dictionary does not know about. Approved rows join the
-- dictionary at match time. Kept in a table rather than edited into the C# array on purpose: a
-- scoring-critical list must never change without an explicit approval.
CREATE TABLE IF NOT EXISTS skill_dictionary (
  id          TEXT PRIMARY KEY,
  term        TEXT NOT NULL,
  normalized  TEXT NOT NULL,
  occurrences INTEGER NOT NULL DEFAULT 0,
  origin      TEXT NOT NULL DEFAULT 'manual',
  created_at  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_skilldict_norm ON skill_dictionary(normalized);