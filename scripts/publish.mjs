// npm run publish: self-contained single-file backend with the built SPA embedded.
// Output: artifacts/resu-clean-<rid> (one ResuClean.exe + wwwroot + data dir).
// Use it when you want to copy the tool onto another machine without installing .NET.
import { existsSync, mkdirSync, rmSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { p, serverDir, root, log, run, npmCmd, dotnetCmd } from './lib.mjs';

const pre = await run('preflight', process.execPath, [p('scripts', 'preflight.mjs')]);
if (pre !== 0) process.exit(pre);

// ---- 1. Build the SPA straight into the backend wwwroot -------------------
if (!existsSync(p('web', 'node_modules'))) {
  log('publish', 'Installing web dependencies (first run only)...');
  const install = await run('web:install', npmCmd, ['install'], { cwd: p('web') });
  if (install !== 0) process.exit(install);
}
const webBuild = await run('web', npmCmd, ['run', 'build'], { cwd: p('web') });
if (webBuild !== 0) process.exit(webBuild);

// ---- 2. Publish self-contained --------------------------------------------
const rid = spawnSync(dotnetCmd, ['--info'], { encoding: 'utf8' });
const ridLine = String(rid.stdout || '')
  .split(/\r?\n/)
  .map((l) => l.trim())
  .find((l) => /^RID:/.test(l));
const ridValue = ridLine ? ridLine.replace('RID:', '').trim() : 'unknown';
const outDir = p('artifacts', `resu-clean-${ridValue}`);
if (existsSync(outDir)) rmSync(outDir, { recursive: true, force: true });
mkdirSync(outDir, { recursive: true });

const args = [
  'publish',
  p('server', 'ResuClean.csproj'),
  '-c',
  'Release',
  '-r',
  ridValue,
  '--self-contained',
  'true',
  '-p:PublishSingleFile=true',
  '-p:IncludeNativeLibrariesForSelfExtract=true',
  '-p:EnableCompressionInSingleFile=true',
  '-p:DebugType=none',
  '-p:InvariantGlobalization=true',
  '-o',
  outDir,
  '--nologo'
];

const pub = await run('publish', dotnetCmd, args, { cwd: serverDir });
if (pub !== 0) {
  console.error('[publish] dotnet publish failed.');
  process.exit(pub);
}

// ---- 3. Ship .env.example and docs alongside -------------------------------
spawnSync(dotnetCmd, ['--version'], { stdio: 'ignore' });
for (const file of ['.env.example', 'README.md']) {
  if (existsSync(p(file))) spawnSync(process.platform === 'win32' ? 'copy' : 'cp', [p(file), outDir], { shell: true });
}

console.log('');
log('publish', `Self-contained build written to ${outDir.replace(root + '\\', '').replace(root + '/', '')}`);
log('publish', 'Copy that folder anywhere and run the ResuClean executable. .NET is not required there.');