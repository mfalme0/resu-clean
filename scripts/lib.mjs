// Shared helpers for the resu-clean npm scripts. No dependencies: node built-ins only.
import { existsSync, readFileSync, writeFileSync, copyFileSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawn, spawnSync } from 'node:child_process';

export const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const p = (...parts) => join(root, ...parts);
export const serverDir = p('server');
export const webDir = p('web');

export const isWin = process.platform === 'win32';
export const npmCmd = isWin ? 'npm.cmd' : 'npm';

export function log(scope, msg) {
  const stamp = new Date().toISOString().slice(11, 19);
  console.log(`[${stamp}] [${scope}] ${msg}`);
}

/** Load .env into process.env without overwriting variables already set by the shell. */
export function loadEnv() {
  const envPath = p('.env');
  if (!existsSync(envPath)) return {};
  const out = {};
  for (const rawLine of readFileSync(envPath, 'utf8').split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith('#')) continue;
    const eq = line.indexOf('=');
    if (eq <= 0) continue;
    const key = line.slice(0, eq).trim();
    let value = line.slice(eq + 1).trim();
    if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'"))) {
      value = value.slice(1, -1);
    }
    out[key] = value;
    if (process.env[key] === undefined) process.env[key] = value;
  }
  return out;
}

/** Create .env from .env.example on first run. Never overwrites an existing one. */
export function ensureEnv() {
  const envPath = p('.env');
  if (existsSync(envPath)) return false;
  const example = p('.env.example');
  if (!existsSync(example)) throw new Error('.env.example is missing from the repo.');
  copyFileSync(example, envPath);
  return true;
}

/** Create the data directory and a minimal .gitkeep so the folder is visible. */
export function ensureDataDir() {
  const env = loadEnv();
  const dataDir = resolve(root, env.RESUCLEAN_DATA_DIR || 'data');
  if (!existsSync(dataDir)) mkdirSync(dataDir, { recursive: true });
  return dataDir;
}

export function readPackageJson(dir) {
  return JSON.parse(readFileSync(join(dir, 'package.json'), 'utf8'));
}

/**
 * Checks whether a command is runnable. On Windows the real .exe is invoked directly
 * (no shell), which is both faster and avoids cmd.exe quoting problems with paths.
 */
export function haveCommand(cmd, args = ['--version']) {
  const r = spawnSync(cmd, args, { stdio: 'ignore', shell: false });
  return r.status === 0;
}

/** The dotnet executable to use on this platform. */
export const dotnetCmd = isWin ? 'dotnet.exe' : 'dotnet';

/**
 * Runs a child process with prefixed, line-buffered output.
 *
 * Windows note: .exe commands are launched without a shell so paths containing spaces work.
 * Batch shims (.cmd, .bat) still need a shell, so their arguments are quoted for cmd.exe.
 * Returns a promise of the exit code.
 */
export function run(label, cmd, args, opts = {}) {
  return new Promise((resolvePromise) => {
    const useShell = opts.shell ?? (isWin && /\.(cmd|bat)$/i.test(cmd));
    const finalArgs = useShell ? args.map((a) => `"${a}"`) : args;

    log('launch', `${label}: ${cmd} ${finalArgs.join(' ')}`);
    const child = spawn(cmd, finalArgs, {
      cwd: opts.cwd || root,
      shell: useShell,
      env: { ...process.env, ...(opts.env || {}) },
      stdio: ['ignore', 'pipe', 'pipe']
    });
    const tag = `[${label}]`;
    const pipe = (stream, isErr) => {
      let buf = '';
      stream.setEncoding('utf8');
      stream.on('data', (chunk) => {
        buf += chunk;
        const lines = buf.split(/\r?\n/);
        buf = lines.pop() ?? '';
        for (const line of lines) {
          if (line.trim().length === 0) continue;
          if (isErr) console.error(`${tag} ${line}`);
          else console.log(`${tag} ${line}`);
        }
      });
    };
    pipe(child.stdout, false);
    pipe(child.stderr, true);
    child.on('error', (err) => {
      console.error(`${tag} failed to start: ${err.message}`);
      resolvePromise(1);
    });
    child.on('close', (code) => resolvePromise(code ?? 0));
    if (opts.onChild) opts.onChild(child);
  });
}

export function writeJson(file, value) {
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, JSON.stringify(value, null, 2) + '\n', 'utf8');
}