// npm run mem: prints live memory for the running backend.
// Prefers the in-process reading from /api/metrics/mem; falls back to the OS process view.
import { spawnSync } from 'node:child_process';
import { loadEnv, isWin } from './lib.mjs';

const env = loadEnv();
const port = env.RESUCLEAN_PORT || '5177';
const headers = env.RESUCLEAN_API_KEY ? { 'X-API-Key': env.RESUCLEAN_API_KEY } : {};

async function fromApi() {
  try {
    const res = await fetch(`http://127.0.0.1:${port}/api/metrics/mem`, { headers });
    if (!res.ok) return null;
    return await res.json();
  } catch {
    return null;
  }
}

function osProcesses() {
  if (isWin) {
    try {
      const out = spawnSync(
        'powershell.exe',
        [
          '-NoProfile',
          '-Command',
          "Get-CimInstance Win32_Process -Filter \"Name='ResuClean.exe'\" | " +
            'Select-Object ProcessId,Name,@{n=\'ws\';e={[math]::Round($_.WorkingSet64/1MB,1)}} | ConvertTo-Json'
        ],
        { encoding: 'utf8' }
      );
      const parsed = JSON.parse((out.stdout || '').trim() || '[]');
      const list = Array.isArray(parsed) ? parsed : [parsed];
      return list.map((x) => ({ pid: x.ProcessId, label: x.Name, mb: x.ws }));
    } catch {
      return [];
    }
  }
  const r = spawnSync('ps', ['-eo', 'pid,rss,comm'], { encoding: 'utf8' });
  if (r.status !== 0) return [];
  return String(r.stdout)
    .split('\n')
    .filter((line) => /ResuClean/i.test(line))
    .map((line) => {
      const [pid, rss, ...rest] = line.trim().split(/\s+/);
      return { pid: Number(pid), label: rest.join(' '), mb: Math.round((Number(rss) / 1024) * 10) / 10 };
    })
    .filter((x) => Number.isFinite(x.mb));
}

function fmtDuration(seconds) {
  const s = Math.max(0, Math.floor(seconds || 0));
  return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m ${s % 60}s`;
}

const pad = (s, n) => String(s).padEnd(n);

const api = await fromApi();
console.log('');

if (api) {
  const pct = Math.round(((api.rssMb || 0) / 60) * 100);
  console.log(`  ${pad('backend', 16)} port ${port}`);
  console.log(`  ${pad('uptime', 16)} ${fmtDuration(api.uptimeSeconds)}`);
  console.log(`  ${pad('RSS', 16)} ${api.rssMb} MB   ${pct}% of the 60 MB idle target`);
  console.log(`  ${pad('managed heap', 16)} ${api.managedHeapMb} MB`);
  console.log(`  ${pad('peak RSS', 16)} ${api.peakRssMb} MB`);
  console.log(`  ${pad('requests', 16)} ${api.requests}`);
  console.log(`  ${pad('ops running', 16)} ${api.opsActive}`);
} else {
  console.log(`  No backend answering on port ${port}.`);
  const procs = osProcesses();
  if (procs.length) {
    for (const proc of procs) console.log(`  ${pad(String(proc.pid), 8)} ${pad(proc.label, 16)} ${proc.mb} MB`);
  } else {
    console.log('  Nothing running. Start it with `npm start` or `npm run dev`.');
  }
}
console.log('');