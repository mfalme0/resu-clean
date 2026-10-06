// npm run publish: self-contained single-file backend with the built SPA embedded.
// Output: artifacts/resu-clean-<rid> (one ResuClean.exe + wwwroot + data dir).
// Use it when you want to copy the tool onto another machine without installing .NET.
import { existsSync, mkdirSync, rmSync, copyFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { p, serverDir, root, log, run, npmCmd, dotnetCmd, isWin } from './lib.mjs';

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

const allRids = process.argv.includes('--all-rids');
// x64 only, deliberately: three platforms we can actually verify from here.
// ARM64 is a one-line change to this array plus a real test on the hardware.
const RIDS = ['win-x64', 'osx-x64', 'linux-x64'];
// Each RID gets a COMPLETE bundle (app + launcher + wwwroot), so a release zip is a folder you
// unzip and run. The project is pure managed code, so cross-publishing from one host works.
const targets = allRids ? RIDS : [ridValue];

/** Publishes the backend plus both launcher binaries into artifacts/resu-clean-<rid>. */
async function buildBundle(target) {
  const outDir = p('artifacts', `resu-clean-${target}`);
  if (existsSync(outDir)) rmSync(outDir, { recursive: true, force: true });
  mkdirSync(outDir, { recursive: true });

  const args = [
    'publish',
    p('server', 'ResuClean.csproj'),
    '-c',
    'Release',
    '-r',
    target,
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

  const pub = await run(`publish:${target}`, dotnetCmd, args, { cwd: serverDir });
  if (pub !== 0) {
    console.error(`[publish] dotnet publish failed for ${target}.`);
    return null;
  }

  // The launcher is published per target too, so its own native output matches that platform.
  const launcherOut = p('artifacts', `launcher-${target}`);
  if (existsSync(launcherOut)) rmSync(launcherOut, { recursive: true, force: true });
  const launcher = await run(`launcher:${target}`, dotnetCmd, [
    'publish',
    p('tools', 'launcher', 'ResuClean.Launcher.csproj'),
    '-c', 'Release',
    '-r', target,
    '-o', launcherOut,
    '--nologo'
  ]);

  // The binary the target platform produces has that platform's naming, even when cross-building.
  const binNameFor = (rid) => (rid.startsWith('win') ? 'resu-clean.exe' : 'resu-clean');
  if (launcher === 0 && existsSync(join(launcherOut, binNameFor(target)))) {
    copyFileSync(join(launcherOut, binNameFor(target)), join(outDir, binNameFor(target)));
    copyFileSync(join(launcherOut, binNameFor(target)), join(outDir, binNameFor(target).replace('resu-clean', 'resu-clean-dev')));
    rmSync(launcherOut, { recursive: true, force: true });
  } else {
    console.error(`[publish] The ${target} launcher build failed; the bundle has no launcher.`);
  }

  for (const file of ['.env.example', 'README.md', 'LICENSE']) {
    if (existsSync(p(file))) spawnSync(isWin ? 'copy' : 'cp', [p(file), outDir], { shell: true });
  }

  return outDir;
}

log('publish', `building complete bundles for ${targets.join(', ')}...`);
const bundles = [];
for (const target of targets) {
  const dir = await buildBundle(target);
  if (dir) bundles.push({ rid: target, dir });
}

if (bundles.length === 0) process.exit(1);

for (const { rid: target, dir } of bundles) {
  const binName = target.startsWith('win') ? 'resu-clean.exe' : './resu-clean';
  const devName = target.startsWith('win') ? 'resu-clean-dev.exe' : './resu-clean dev';
  console.log('');
  log('publish', `${target} bundle ready in artifacts/resu-clean-${target}`);
  log('publish', '');
  log('publish', `  ${binName.padEnd(18)} run the app (UI + API on one port)`);
  log('publish', `  ${devName.padEnd(18)} run both dev servers with hot reload`);
  log('publish', '');
  log('publish', 'Your database and any stored API keys live in the Data/ folder inside this bundle.');
  log('publish', 'Copy the whole folder anywhere and run it. .NET is not required on the target machine.');
}