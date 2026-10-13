// API smoke test: starts its own backend on a spare port against a throwaway data dir,
// then calls the main endpoints and checks the payloads really are our JSON shapes.
// This catches whole classes of silent breakage, such as an endpoint returning a serialised
// Task (which looks like a 200 with a plausible body but is not the DTO).
// Usage: npm run smoke
import { spawn } from 'node:child_process';
import { existsSync, rmSync } from 'node:fs';
import { p, log, loadEnv, dotnetCmd } from './lib.mjs';

loadEnv();

const port = Number(process.env.RESUCLEAN_SMOKE_PORT || 5205);
const dataDir = p('data-smoke-run');
const dll = p('server', 'bin', 'Release', 'net8.0', 'ResuClean.dll');

if (!existsSync(dll)) {
  console.error('Build first: npm run build');
  process.exit(1);
}
if (existsSync(dataDir)) rmSync(dataDir, { recursive: true, force: true });

const base = `http://127.0.0.1:${port}/api`;
const child = spawn(dotnetCmd, [dll], {
  cwd: p('server'),
  env: { ...process.env, RESUCLEAN_PORT: String(port), RESUCLEAN_DATA_DIR: dataDir, RESUCLEAN_HOST: '127.0.0.1' },
  stdio: ['ignore', 'pipe', 'pipe']
});
let serverLog = '';
child.stdout.on('data', (d) => (serverLog += d));
child.stderr.on('data', (d) => (serverLog += d));

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let failures = 0;

/** A serialised Task leaks these fields; a real payload never has them. */
function assertNotTask(label, body) {
  for (const marker of ['isCompletedSuccessfully', 'creationOptions', 'asyncState', '"status":5']) {
    if (body.includes(marker)) {
      console.error(`  FAIL ${label}: response body is a serialised Task ("${marker}")`);
      failures++;
      return;
    }
  }
}

async function call(label, method, path, body, { expectStatus = 200, check, expectType } = {}) {
  try {
    const res = await fetch(`${base}${path}`, {
      method,
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
    const text = await res.text();
    if (res.status !== expectStatus) {
      console.error(`  FAIL ${label}: expected ${expectStatus}, got ${res.status} ${text.slice(0, 160)}`);
      failures++;
      return null;
    }
    if (expectType) {
      const actual = res.headers.get('content-type') || '';
      if (!actual.includes(expectType)) {
        console.error(`  FAIL ${label}: expected content-type ${expectType}, got "${actual}"`);
        failures++;
        return null;
      }
    }
    assertNotTask(label, text);
    if (check) {
      const parsed = JSON.parse(text);
      const problem = check(parsed);
      if (problem) {
        console.error(`  FAIL ${label}: ${problem}`);
        failures++;
        return null;
      }
    }
    console.log(`  ok   ${label}`);
    return text.length ? JSON.parse(text) : null;
  } catch (err) {
    console.error(`  FAIL ${label}: ${err.message}`);
    failures++;
    return null;
  }
}

const has = (keys) => (obj) => {
  if (!obj || typeof obj !== 'object') return 'response is not a JSON object';
  const missing = keys.filter((k) => !(k in obj));
  return missing.length ? `missing keys: ${missing.join(', ')}` : null;
};

const isList = (obj) => (Array.isArray(obj?.items) ? null : 'expected a paginated { items: [] } envelope');

async function waitForOp(label, accepted) {
  if (!accepted?.opId) return null;
  for (let i = 0; i < 120; i++) {
    const status = await call(`${label} (poll)`, 'GET', `/ops/${accepted.opId}`);
    if (!status) return null;
    if (status.state === 'done') return status;
    if (status.state === 'failed') {
      console.error(`  FAIL ${label}: operation failed: ${status.error}`);
      failures++;
      return null;
    }
    await sleep(150);
  }
  console.error(`  FAIL ${label}: timed out`);
  failures++;
  return null;
}

try {
  for (let i = 0; i < 60; i++) {
    try {
      await fetch(`${base}/health`);
      break;
    } catch {
      await sleep(500);
    }
  }

  console.log('');
  console.log('  resu-clean API smoke test');
  console.log('');

  // ---- system ----
  await call('GET /health', 'GET', '/health', undefined, { check: has(['status', 'version']) });
  await call('GET /capabilities', 'GET', '/capabilities', undefined, { check: has(['anyModelConfigured', 'needsModel', 'availableWorkflows']) });
  await call('GET /config', 'GET', '/config', undefined, { check: has(['authRequired', 'maxUploadMb']) });
  await call('GET /metrics/mem', 'GET', '/metrics/mem', undefined, { check: has(['rssMb', 'managedHeapMb']) });

  // ---- profiles ----
  await call('GET /profile', 'GET', '/profile', undefined, { check: has(['voice', 'preferences', 'contact']) });
  await call('PUT /profile', 'PUT', '/profile', { voice: 'Direct and plain', contact: { email: 'smoke@example.com' } },
    { check: has(['voice', 'contact']) });
  await call('GET /profile/facts', 'GET', '/profile/facts', undefined, { check: (o) => (Array.isArray(o) ? null : 'expected an array') });

  // NEW INFO RULE: a submitted fact must land in pending, never in facts.
  const fact = await call('POST /profile/facts', 'POST', '/profile/facts', { kind: 'achievement', text: 'Smoke test fact about reducing errors' },
    { check: has(['added', 'message']) });
  if (fact && fact.fact !== null && fact.fact !== undefined) {
    console.error('  FAIL POST /profile/facts: a new fact was verified without approval');
    failures++;
  }
  await call('GET /profile/pending', 'GET', '/profile/pending', undefined, { check: (o) => (Array.isArray(o) ? null : 'expected an array') });
  await call('GET /profile/pending/count', 'GET', '/profile/pending/count', undefined, { check: has(['count']) });

  // ---- scoring dictionary ----
  // The proposer may only ever queue a term for approval. If it could write to the dictionary
  // directly, a scoring list would be something the tool decides instead of you.
  await call('GET /profile/dictionary', 'GET', '/profile/dictionary', undefined, { check: (o) => (Array.isArray(o) ? null : 'expected an array') });
  const proposal = await call('POST /profile/dictionary/propose', 'POST', '/profile/dictionary/propose', { useModel: false },
    { check: has(['queued', 'proposed']) });
  if (proposal && (proposal.queued || []).some((q) => !q.id || !q.text)) {
    console.error('  FAIL dictionary/propose: a queued proposal is not a real pending fact');
    failures++;
  }

  // ---- resumes ----
  await call('GET /resumes', 'GET', '/resumes', undefined, { check: isList });
  const created = await call('POST /resumes', 'POST', '/resumes',
    { label: 'Smoke resume', text: 'JANE DOE\njane@example.com | +254700000000\nEXPERIENCE\n- Led reporting from 2020 to 2024' },
    { expectStatus: 201, check: has(['resumeId', 'versionId', 'text']) });

  if (created) {
    await call('GET /resumes/{id}', 'GET', `/resumes/${created.resumeId}`, undefined,
      { check: (o) => (o.summary && Array.isArray(o.versions) ? null : 'expected { summary, versions }') });

    const clean = await call('POST clean', 'POST', `/resumes/versions/${created.versionId}/clean`, { useModel: false },
      { expectStatus: 202, check: has(['opId']) });
    const cleanDone = await waitForOp('clean', clean);
    if (cleanDone?.result) {
      if (typeof cleanDone.result.text !== 'string') {
        console.error('  FAIL clean: result.text is not a string');
        failures++;
      }
      if (cleanDone.result.versionId !== created.versionId) {
        console.error('  FAIL clean: result.versionId did not match');
        failures++;
      }
    }

    await call('POST ats', 'POST', `/resumes/versions/${created.versionId}/ats`, { useModel: false },
      { expectStatus: 202, check: has(['opId']) });

    // Grounding: the deterministic pass needs no model, and must queue rather than delete.
    const grounded = await call('POST fact-check', 'POST', `/resumes/versions/${created.versionId}/fact-check`, {},
      { expectStatus: 202, check: has(['opId']) });
    if (grounded) {
      const done = await waitForOp('fact-check', grounded);
      const result = done?.result;
      if (!result || typeof result.checked !== 'number' || typeof result.ungrounded !== 'number') {
        console.error('  FAIL fact-check: expected checked and ungrounded counts');
        failures++;
      } else if (result.method !== 'deterministic') {
        console.error(`  FAIL fact-check: expected the deterministic method with no route, got "${result.method}"`);
        failures++;
      } else if (!Array.isArray(result.claims) || !Array.isArray(result.queued)) {
        console.error('  FAIL fact-check: claims and queued must both be arrays');
        failures++;
      } else {
        console.log(`  ok   fact-check (${result.checked} claims, ${result.ungrounded} queued)`);
      }
    }
    await call('GET /fact-checks', 'GET', '/resumes/fact-checks', undefined,
      { check: (o) => (Array.isArray(o) ? null : 'expected an array') });

    // The rewrite ledger is the only honest answer to "is this model pulling its weight": every
    // candidate was re-scored before it was shown, and this is the record of what happened.
    await call('GET /resumes/rewrites', 'GET', '/resumes/rewrites', undefined,
      { check: (o) => (Array.isArray(o?.rows) && Array.isArray(o?.recent) ? null : 'expected rows and recent arrays') });

    // ATS must return a 0-100 score with a tip on every check.
    const ats = await call('POST /jobs', 'POST', '/jobs',
      { title: 'Data Analyst', company: 'Acme', location: 'Nairobi', url: 'https://example.org/1', text: 'SQL and Power BI. Email careers@example.org.', sourceName: 'manual' },
      { expectStatus: 201, check: has(['id', 'title']) });
    if (ats) {
      const kit = await call('POST kit', 'POST', '/kits', { jobId: ats.id, resumeVersionId: created.versionId, coverLetter: true, useModel: false },
        { expectStatus: 202, check: has(['opId']) });
      const kitDone = await waitForOp('kit', kit);
      const built = kitDone?.result;
      if (built) {
        if (!built.readyToSend) {
          console.error('  FAIL kit: not readyToSend');
          failures++;
        }
        if (typeof built.wordCount !== 'number' || built.wordCount >= 150) {
          console.error(`  FAIL kit: body was ${built.wordCount} words, expected under 150`);
          failures++;
        }
        for (const placeholder of ['[company]', '[your name]', 'XXX']) {
          if ((built.body || '').toLowerCase().includes(placeholder.toLowerCase())) {
            console.error(`  FAIL kit: body contains placeholder ${placeholder}`);
            failures++;
          }
        }
      }

      await call('GET /jobs', 'GET', '/jobs', undefined, { check: isList });
      await call('GET /jobs/{id}', 'GET', `/jobs/${ats.id}`, undefined, { check: has(['id', 'title', 'sourceName']) });

      // ---- requirement classification ----
      // Split a posting's demands and check them against the profile, with no model configured.
      const reqs = await call('GET /jobs/{id}/requirements', 'GET', `/jobs/${ats.id}/requirements`, undefined,
        { check: has(['requirements', 'unmetBlockers', 'unmetRequired', 'method']) });
      if (reqs) {
        const classes = new Set((reqs.requirements || []).map((r) => r.class));
        if (typeof reqs.unmetBlockers !== 'number' || typeof reqs.unmetRequired !== 'number') {
          console.error('  FAIL requirements: unmet counts must be numbers');
          failures++;
        } else if (![...classes].every((c) => ['blocker', 'required', 'preferred', 'implicit'].includes(c))) {
          console.error(`  FAIL requirements: unexpected class in ${[...classes].join(', ')}`);
          failures++;
        } else {
          console.log(`  ok   requirements (${reqs.requirements.length} found, ${reqs.unmetBlockers} blockers)`);
        }
      }

      const reranked = await call('POST /jobs/requirements/refresh', 'POST', '/jobs/requirements/refresh', {},
        { check: has(['checkedJobs', 'unmetBlockers']) });
      if (reranked && typeof reranked.checkedJobs !== 'number') {
        console.error('  FAIL requirements/refresh: checkedJobs must be a number');
        failures++;
      }

      // The job list must carry the ranking inputs through as real JSON fields.
      const afterRerank = await call('GET /jobs (ranked)', 'GET', '/jobs', undefined,
        { check: (o) => (Array.isArray(o?.items) && o.items.every((j) => typeof j.unmetBlockers === 'number' && typeof j.requirementsChecked === 'boolean')
          ? null : 'expected unmetBlockers and requirementsChecked on every row') });

      // ---- posting quality ----
      // Flags are judgement about someone else's posting, so a flag without a quote is a guess and
      // must not appear. Staleness is arithmetic and carries no quote at all.
      const flagCheck = (o) => {
        if (!Array.isArray(o?.flags)) return 'expected a flags array';
        for (const flag of o.flags) {
          if (flag.severity === 'warn' && !flag.quote) {
            return `a warning was raised with no quote: "${flag.detail}"`;
          }
        }
        return null;
      };
      const quality = await call('GET /jobs/{id}/quality', 'GET', `/jobs/${ats.id}/quality`, undefined,
        { check: (o) => flagCheck(o) ?? (typeof o.hasWarnings === 'boolean' ? null : 'expected hasWarnings') });
      if (quality && quality.method !== 'deterministic') {
        console.error(`  FAIL quality: expected the deterministic method with no route, got "${quality.method}"`);
        failures++;
      }

      // ---- gap ramp ----
      // Every suggestion must name the verified fact it builds on. An uncited one is a shopping
      // list dressed up as advice, so it is discarded server-side.
      const ramp = await call('POST /jobs/gap-ramp', 'POST', '/jobs/gap-ramp',
        { requirement: 'Experience with Terraform', requirementClass: 'required', missing: ['terraform'] },
        { check: (o) => {
          const missing = ['requirement', 'class', 'suggestions', 'closable', 'method'].filter((k) => !(k in o));
          if (missing.length) return `missing keys: ${missing.join(', ')}`;
          // Nulls are omitted rather than serialised, so "no nearest fact" arrives as an absent key.
          if (o.nearestFact !== undefined && typeof o.nearestFact !== 'string') {
            return 'nearestFact must be a string when present';
          }
          return null;
        } });
      if (ramp) {
        const uncited = (ramp.suggestions || []).find((s) => !s.buildsOnFactId || !s.buildsOnFactText);
        if (uncited) {
          console.error(`  FAIL gap-ramp: a suggestion arrived with no verified fact behind it: "${uncited.text}"`);
          failures++;
        } else if (typeof ramp.closable !== 'boolean') {
          console.error('  FAIL gap-ramp: closable must be a boolean, so "not closable" is never mistaken for "no suggestions"');
          failures++;
        }
      }

      // ---- source provenance ----
      // An aggregator is a convenience, not evidence. Only an employer's own board can return a
      // meaningful 404, so the screen has to be able to say which one a source is.
      const sources = await call('GET /sources (provenance)', 'GET', '/sources', undefined,
        { check: (o) => (Array.isArray(o) ? null : 'expected an array') });
      const withProvenance = sources || [];
      for (const source of withProvenance) {
        if (!['employer_ats', 'aggregator', 'board', 'unknown'].includes(source.provenance)) {
          console.error(`  FAIL sources: "${source.name}" has provenance "${source.provenance}"`);
          failures++;
        }
      }

      // A source saved with a nonsense verdict must not store it.
      await call('POST /sources (bogus provenance)', 'POST', '/sources',
        { name: 'Smoke provenance check', type: 'rss', url: 'https://example.org/feed', provenance: 'trust-me' },
        { expectStatus: 201, check: (o) => (o.provenance === 'unknown' ? null : `expected unknown, got ${o.provenance}`) });

      // ---- verification ----
      // The timestamp and the status are separate facts, and only one of them may move on a check
      // that could not confirm anything. A posting that reads as "open" because nobody re-checked it
      // is the failure this endpoint exists to prevent.
      const verified = await call('POST /jobs/{id}/verification', 'POST', `/jobs/${ats.id}/verification`,
        { status: 'open' },
        { check: has(['jobId', 'status', 'effectiveStatus']) });
      if (verified && verified.effectiveStatus !== 'open') {
        console.error(`  FAIL verification: a confirmed-open posting reads as "${verified.effectiveStatus}"`);
        failures++;
      }
      if (verified && !verified.lastVerifiedAt) {
        console.error('  FAIL verification: an open posting carries no lastVerifiedAt');
        failures++;
      }

      const unverifiable = await call('POST /jobs/{id}/verification (unverifiable)', 'POST',
        `/jobs/${ats.id}/verification`, { status: 'unverifiable' },
        { check: has(['jobId', 'status', 'effectiveStatus']) });
      if (unverifiable && unverifiable.effectiveStatus === 'open') {
        console.error('  FAIL verification: an unverifiable posting was reported as open');
        failures++;
      }

      await call('POST /jobs/{id}/verification (bad status) -> 400', 'POST', `/jobs/${ats.id}/verification`,
        { status: 'probably-fine' }, { expectStatus: 400 });

      // ---- interview prep ----
      // The claims nothing backs are reported even though no model is configured: those are the
      // landmines, and finding them does not need a model.
      await call('POST best-fit', 'POST', `/resumes/versions/${created.versionId}/best-fit`, undefined,
        { check: has(['versionId', 'bestFit']) });
      const prep = await call('GET /jobs/{id}/interview', 'GET', `/jobs/${ats.id}/interview`, undefined,
        { check: has(['jobId', 'versionId', 'questions', 'notes', 'method']) });
      if (prep) {
        if (prep.method !== 'deterministic') {
          console.error(`  FAIL interview: expected the deterministic method with no route, got "${prep.method}"`);
          failures++;
        }
        // Every risk the report raises has to point at a claim, not at a hunch.
        const unanchored = (prep.questions || []).find((q) => q.risk && !q.groundedIn);
        if (unanchored) {
          console.error(`  FAIL interview: a risk was raised with nothing to anchor it: "${unanchored.text}"`);
          failures++;
        }
      }
      await call('GET /jobs/{id}/interview (unknown job) -> 404', 'GET', '/jobs/job_missing/interview', undefined,
        { expectStatus: 404 });

      const entry = await call('POST /tracker', 'POST', '/tracker', { jobId: ats.id, status: 'sent-by-me', notes: 'smoke' },
        { expectStatus: 201, check: has(['id', 'status']) });
      if (entry && entry.status !== 'sent') {
        console.error(`  FAIL tracker: status "sent-by-me" was not normalised (got ${entry.status})`);
        failures++;
      }
      await call('GET /tracker', 'GET', '/tracker', undefined, { check: isList });

      // ---- tracker export / import ----
      // Checked over real HTTP so a serialised Task or a mis-cased property cannot pass as working.
      const exported = await call('GET /tracker/export-text', 'GET', '/tracker/export-text', undefined,
        { check: has(['json']) });
      if (exported && typeof exported.json === 'string') {
        try {
          const parsed = JSON.parse(exported.json);
          const shapeOk = parsed && typeof parsed === 'object' && Object.values(parsed).every(
            (v) => v && typeof v === 'object'
              && typeof v.stage === 'string' && typeof v.note === 'string'
              && typeof v.applied === 'string' && typeof v.updated === 'string');
          if (!shapeOk) {
            console.error('  FAIL tracker/export: entries are not {stage,note,applied,updated} strings');
            failures++;
          }
        } catch {
          console.error('  FAIL tracker/export: response is not valid JSON');
          failures++;
        }
      }

      await call('GET /tracker/export', 'GET', '/tracker/export', undefined, { expectType: 'application/json' });

      const file = JSON.stringify({
        smokeco: { stage: 'applied', note: 'from smoke', applied: '2026-10-11', updated: '2026-10-11' }
      });

      const imported = await call('POST /tracker/import', 'POST', '/tracker/import', { json: file },
        { check: has(['imported', 'created', 'updated', 'skipped']) });
      if (imported && imported.created !== 1) {
        console.error(`  FAIL tracker/import: expected 1 new entry, got ${imported.created}`);
        failures++;
      }

      // Re-importing the same file must not create a second entry.
      const repeat = await call('POST /tracker/import (repeat)', 'POST', '/tracker/import', { json: file },
        { check: has(['imported', 'skipped']) });
      if (repeat && repeat.imported !== 0) {
        console.error(`  FAIL tracker/import: a repeat import created ${repeat.imported} entries instead of 0`);
        failures++;
      }

      await call('POST /tracker/import (bad json)', 'POST', '/tracker/import', { json: '{ nope' },
        { expectStatus: 400 });
    }
  }

  // ---- sources ----
  await call('GET /sources', 'GET', '/sources', undefined, { check: (o) => (Array.isArray(o) ? null : 'expected an array') });
  await call('GET /sources/packs/all', 'GET', '/sources/packs/all', undefined,
    { check: (o) => (Array.isArray(o) && o.length === 4 ? null : 'expected 4 seed packs') });
  await call('GET /jobs/search-url', 'POST', '/jobs/search-url', { name: 'Smoke link source', type: 'link', urlTemplate: 'https://example.org/jobs?q={query}' },
    { check: has(['url']) });

  // ---- models ----
  await call('GET /providers', 'GET', '/providers', undefined, { check: (o) => (Array.isArray(o) ? null : 'expected an array') });
  const presets = await call('GET /providers/presets', 'GET', '/providers/presets', undefined, { check: (o) => (Array.isArray(o) ? null : 'expected an array') });
  const presetIds = (presets || []).map((p) => p.id);
  for (const required of ['openai', 'anthropic', 'openrouter', 'groq', 'gemini', 'nvidia', 'cleanapis']) {
    if (!presetIds.includes(required)) {
      console.error(`  FAIL providers/presets: missing preset "${required}"`);
      failures++;
    }
  }
  await call('GET /routes/clean', 'GET', '/routes/clean', undefined, { check: has(['workflow', 'entries']) });
  await call('GET /routes', 'GET', '/routes', undefined,
    { check: (o) => (Array.isArray(o) && o.length === 13 ? null : `expected 13 workflows, got ${Array.isArray(o) ? o.length : 'not an array'}`) });

  // ---- model cost telemetry ----
  await call('GET /metrics/models', 'GET', '/metrics/models', undefined,
    { check: (o) => (Array.isArray(o?.byWorkflow) && Array.isArray(o?.recent) ? null : 'expected byWorkflow and recent arrays') });

  // ---- search ----
  await call('GET /search/capabilities', 'GET', '/search/capabilities', undefined,
    { check: has(['canSearch']) });
  await call('POST /search (no query)', 'POST', '/search', {}, { expectStatus: 400 });

  // ---- errors use the standard envelope ----
  const missing = await call('GET unknown id -> 404', 'GET', '/resumes/res_missing', undefined, { expectStatus: 404 });
  if (missing && !missing.error?.code) {
    console.error('  FAIL 404 body: expected the { error: { code, message } } envelope');
    failures++;
  }
  await call('GET bad workflow -> 400', 'GET', '/routes/not-a-workflow', undefined, { expectStatus: 400 });

  console.log('');
  if (failures > 0) {
    console.error(`  ${failures} check(s) failed.`);
    process.exitCode = 1;
  } else {
    console.log('  all checks passed.');
  }
  console.log('');
} catch (err) {
  console.error('\nsmoke test crashed:', err.message);
  if (serverLog) console.error(serverLog.split('\n').slice(-12).join('\n'));
  process.exitCode = 1;
} finally {
  const exited = new Promise((resolve) => child.on('exit', resolve));
  child.kill('SIGTERM');
  await Promise.race([exited, sleep(4000)]);
  try {
    rmSync(dataDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  } catch {
    log('smoke', `left ${dataDir} behind; delete it manually`);
  }
}