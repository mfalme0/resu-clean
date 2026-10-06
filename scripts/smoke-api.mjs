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

async function call(label, method, path, body, { expectStatus = 200, check } = {}) {
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

      const entry = await call('POST /tracker', 'POST', '/tracker', { jobId: ats.id, status: 'sent-by-me', notes: 'smoke' },
        { expectStatus: 201, check: has(['id', 'status']) });
      if (entry && entry.status !== 'sent') {
        console.error(`  FAIL tracker: status "sent-by-me" was not normalised (got ${entry.status})`);
        failures++;
      }
      await call('GET /tracker', 'GET', '/tracker', undefined, { check: isList });
    }
  }

  // ---- sources ----
  await call('GET /sources', 'GET', '/sources', undefined, { check: (o) => (Array.isArray(o) ? null : 'expected an array') });
  await call('GET /sources/packs/all', 'GET', '/sources/packs/all', undefined,
    { check: (o) => (Array.isArray(o) && o.length === 2 ? null : 'expected 2 seed packs') });
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
  await call('GET /routes', 'GET', '/routes', undefined, { check: (o) => (Array.isArray(o) && o.length === 6 ? null : 'expected 6 workflows') });
  await call('GET /routes/clean', 'GET', '/routes/clean', undefined, { check: has(['workflow', 'entries']) });

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