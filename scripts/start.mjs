// npm start: low-RAM everyday mode. Builds the frontend once if needed, then runs ONLY the backend,
// which serves the built UI and the API from a single port.
import { existsSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { p, serverDir, log, loadEnv, ensureEnv, ensureDataDir, dotnetCmd } from './lib.mjs';

const env = loadEnv();
if (ensureEnv()) log('start', 'Created .env from .env.example.');
ensureDataDir();

const dll = p('server', 'bin', 'Release', 'net8.0', 'ResuClean.dll');
const spa = p('server', 'wwwroot', 'index.html');

if (!existsSync(spa)) {
  console.error('');
  console.error('The frontend has not been built yet.');
  console.error('Run `npm run build` once, then `npm start`.');
  console.error('');
  process.exit(1);
}
if (!existsSync(dll)) {
  console.error('');
  console.error('The backend has not been built yet.');
  console.error('Run `npm run build` once, then `npm start`.');
  console.error('');
  process.exit(1);
}

const port = env.RESUCLEAN_PORT || '5177';
const host = env.RESUCLEAN_HOST || '127.0.0.1';
const shown = host === '127.0.0.1' || host === 'localhost' ? host : host;
log('start', `Serving UI + API on http://${shown}:${port}`);
log('start', 'Press Ctrl+C to stop.');

const child = spawn(dotnetCmd, [dll], { cwd: serverDir, env: { ...process.env }, stdio: 'inherit' });

const forward = (signal) => {
  process.on(signal, () => {
    try {
      child.kill('SIGTERM');
    } catch {
      /* ignore */
    }
  });
};
forward('SIGINT');
forward('SIGTERM');

child.on('close', (code) => process.exit(code ?? 0));
child.on('error', (err) => {
  console.error(`[start] Could not start the backend: ${err.message}`);
  process.exit(1);
});