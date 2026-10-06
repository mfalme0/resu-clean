// Measures backend memory through the full pipeline and prints the numbers used in README.md.
// Starts its own backend on a spare port with a throwaway data directory, so it never touches
// your real data/resu-clean.db. Usage: node scripts/measure-memory.mjs
import { spawn } from 'node:child_process';
import { existsSync, rmSync } from 'node:fs';
import { p, root, log, loadEnv, dotnetCmd, ensureDataDir } from './lib.mjs';

loadEnv();

const port = Number(process.env.RESUCLEAN_MEM_PORT || 5199);
const dataDir = p('data-metrics');
const dll = p('server', 'bin', 'Release', 'net8.0', 'ResuClean.dll');

if (!existsSync(dll)) {
  console.error('Build first: npm run build');
  process.exit(1);
}
if (existsSync(dataDir)) rmSync(dataDir, { recursive: true, force: true });

const base = `http://127.0.0.1:${port}/api`;
const SAMPLE = `JANE DOE
jane.doe@example.com | +254700000000 | Nairobi, Kenya
SUMMARY
Data analyst with 6 years of experience in financial reporting and reporting automation.
EXPERIENCE
Senior Data Analyst | Kenya Commercial Bank | Mar 2021 - Present
- Led the migration of 14 monthly reports to Power BI, cutting reporting time from 9 days to 2.
- Reduced reconciliation errors by 38% across 1,200 transactions.
- Managed a team of 4 analysts across 3 business units.
Data Analyst | Finlay Analytics | Jun 2018 - Feb 2021
- Built SQL ETL pipelines over 5 million rows of transaction data.
- Automated the regulatory reporting pack, saving 30 hours per month.
EDUCATION
BSc Statistics, University of Nairobi, 2014 - 2018
SKILLS
SQL, Power BI, Excel, Python, dbt, Airflow, stakeholder management`;

const child = spawn(dotnetCmd, [dll], {
  cwd: p('server'),
  env: { ...process.env, RESUCLEAN_PORT: String(port), RESUCLEAN_DATA_DIR: dataDir, RESUCLEAN_HOST: '127.0.0.1' },
  stdio: ['ignore', 'pipe', 'pipe']
});

const rows = [];
let backendLog = '';
child.stdout.on('data', (d) => (backendLog += d));
child.stderr.on('data', (d) => (backendLog += d));

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function api(path, init) {
  const res = await fetch(`${base}${path}`, init);
  if (!res.ok) throw new Error(`${init?.method ?? 'GET'} ${path} -> ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
}

const post = (path, body) =>
  api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });

async function mem() {
  const m = await api('/metrics/mem');
  return { rss: m.rssMb, heap: m.managedHeapMb, peak: m.peakRssMb, threads: m.threads, ops: m.opsActive };
}

async function runOp(kind, fn) {
  const accepted = await fn();
  for (let i = 0; i < 200; i++) {
    const status = await api(`/ops/${accepted.opId}`);
    if (status.state === 'done' || status.state === 'failed') {
      if (status.state === 'failed') throw new Error(`${kind} failed: ${status.error}`);
      return status.result;
    }
    await sleep(150);
  }
  throw new Error(`${kind} timed out`);
}

function record(label, m) {
  rows.push({ stage: label, ...m });
  log('measure', `${label.padEnd(26)} rss=${m.rss} MB  heap=${m.heap} MB  peak=${m.peak} MB`);
}

try {
  // Wait for the server to answer.
  for (let i = 0; i < 60; i++) {
    try {
      await api('/health');
      break;
    } catch {
      await sleep(500);
    }
  }

  record('idle (just started)', await mem());

  const intake = await post('/resumes', { label: 'Metrics sample', text: SAMPLE });
  const versionId = intake.versionId;
  record('after resume intake', await mem());

  await runOp('clean', () => post(`/resumes/versions/${versionId}/clean`, { useModel: false }));
  record('after a resume clean', await mem());

  await runOp('ats', () => post(`/resumes/versions/${versionId}/ats`, { useModel: false, jobDescription: 'Data analyst with SQL and Power BI.' }));
  record('after an ATS scan', await mem());

  const job = await post('/jobs', {
    title: 'Data Analyst',
    company: 'Acme Analytics',
    location: 'Nairobi, Kenya',
    url: 'https://example.org/jobs/1',
    text: 'SQL and Power BI required. Send applications to careers@example.org.',
    sourceName: 'manual'
  });
  await runOp('find_jobs', () => post('/jobs/search', { query: 'data analyst', location: 'Kenya', resumeVersionId: versionId, limit: 50 }));
  record('after a job search', await mem());

  await runOp('apply_email', () => post('/kits', { jobId: job.id, resumeVersionId: versionId, coverLetter: true, useModel: false }));
  record('after an application kit', await mem());

  console.log('');
  console.log('  stage                       RSS      heap     peak');
  console.log('  ' + '-'.repeat(52));
  for (const r of rows) {
    console.log(`  ${r.stage.padEnd(26)} ${`${r.rss} MB`.padEnd(8)} ${`${r.heap} MB`.padEnd(8)} ${r.peak} MB`);
  }
  console.log('');
  console.log(`  measured on ${process.platform}, .NET 8, workstation GC, no model configured`);
  console.log(`  data dir ${dataDir.replace(root + '\\', '').replace(root + '/', '')} (throwaway)`);
  console.log('');
} catch (err) {
  console.error('\nmeasurement failed:', err.message);
  if (backendLog) console.error(backendLog.split('\n').slice(-10).join('\n'));
  process.exitCode = 1;
} finally {
  // Wait for the backend to actually exit before deleting its data directory, or Windows
  // still holds the SQLite file open and the delete fails with EPERM.
  const exited = new Promise((resolve) => child.on('exit', resolve));
  child.kill('SIGTERM');
  await Promise.race([exited, sleep(4000)]);
  for (let attempt = 0; attempt < 5; attempt++) {
    try {
      if (existsSync(dataDir)) rmSync(dataDir, { recursive: true, force: true });
      break;
    } catch {
      await sleep(400);
    }
  }
}