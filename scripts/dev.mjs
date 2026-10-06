// npm run dev: preflight, then run the backend (dotnet watch) and the Vite dev server together.
// Ctrl+C shuts both down cleanly. Works on Windows, macOS and Linux.
import { existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { p, serverDir, webDir, isWin, log, run, loadEnv, ensureEnv, ensureDataDir, npmCmd, dotnetCmd } from './lib.mjs';

loadEnv();
if (ensureEnv()) log('dev', 'Created .env from .env.example.');
ensureDataDir();

const children = [];
let shuttingDown = false;

function killTree(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  try {
    if (isWin) {
      // Windows consoles do not forward SIGTERM; kill the whole process tree.
      spawnSync('taskkill', ['/pid', String(child.pid), '/T', '/F'], { stdio: 'ignore', shell: true });
    } else {
      child.kill('SIGTERM');
    }
  } catch {
    /* already exited */
  }
}

function shutdown(code = 0) {
  if (shuttingDown) return;
  shuttingDown = true;
  for (const child of children) killTree(child);
  log('dev', code === 0 ? 'Stopped.' : 'Stopped (one process exited with an error).');
  setTimeout(() => process.exit(code), 500);
}

process.on('SIGINT', () => shutdown(0));
process.on('SIGTERM', () => shutdown(0));
process.on('exit', () => {
  for (const child of children) killTree(child);
});

const pre = await run('preflight', process.execPath, [p('scripts', 'preflight.mjs')]);
if (pre !== 0) process.exit(pre);

if (!existsSync(p('web', 'node_modules'))) {
  log('dev', 'Installing web dependencies (first run only)...');
  const install = await run('web:install', npmCmd, ['install'], { cwd: webDir });
  if (install !== 0) {
    console.error('[dev] npm install in web/ failed. Fix the npm/network error and retry.');
    process.exit(install);
  }
}

const backendArgs = ['watch', '--project', p('server', 'ResuClean.csproj'), '--no-launch-profile'];
log('dev', 'Starting dotnet watch (backend, hot reload) and vite (UI, hot reload).');
log('dev', 'Open the Vite URL it prints. Press Ctrl+C to stop both.');

const backend = run('api', dotnetCmd, backendArgs, { cwd: serverDir, onChild: (c) => children.push(c) });
const frontend = run('web', npmCmd, ['run', 'dev'], { cwd: webDir, onChild: (c) => children.push(c) });

const results = await Promise.all([backend, frontend]);
shutdown(results.some((code) => code !== 0) ? 1 : 0);