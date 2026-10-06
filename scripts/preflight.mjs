// Preflight check: verifies the toolchain before anything tries to build.
// Requirements: Node 20+ and the .NET 8 SDK.
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { p, root, log, haveCommand, dotnetCmd } from './lib.mjs';

const problems = [];
const warnings = [];

function fail(msg) {
  problems.push(msg);
}

// ---- Node ------------------------------------------------------------------
const nodeMajor = Number(process.versions.node.split('.')[0]);
if (!Number.isFinite(nodeMajor)) {
  fail(`Cannot determine Node version (saw "${process.versions.node}").`);
} else if (nodeMajor < 20) {
  fail(`Node ${nodeMajor}.${process.versions.node.split('.')[1]} is too old. resu-clean needs Node 20 or newer.`);
} else {
  log('preflight', `Node ${process.versions.node} (ok, need >=20)`);
}

// ---- dotnet ----------------------------------------------------------------
if (!haveCommand(dotnetCmd, ['--version'])) {
  fail(
    'The .NET SDK was not found on PATH.\n' +
      '    Install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0\n' +
      '    (make sure "dotnet" is on PATH, then open a new terminal).'
  );
} else {
  const sdks = spawnSync(dotnetCmd, ['--list-sdks'], { encoding: 'utf8' });
  const text = sdks.stdout || '';
  const versions = text
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter(Boolean)
    .map((l) => l.replace(/\s*\[.*\]$/, '').trim())
    .map((v) => v.split('.').slice(0, 2).join('.'));
  if (versions.includes('8.0')) {
    log('preflight', `.NET SDK 8.0 available (targets: ${versions.join(', ')})`);
  } else {
    fail(
      `The .NET 8 SDK is required but only these SDKs are installed: ${versions.join(', ') || 'none'}.\n` +
        '    Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and re-run.'
    );
  }

  const runtimes = spawnSync(dotnetCmd, ['--list-runtimes'], { encoding: 'utf8' });
  if (!/^Microsoft\.NETCore\.App 8\./m.test(runtimes.stdout || '')) {
    warnings.push('No Microsoft.NETCore.App 8.x runtime detected. `dotnet publish` self-contained will still work.');
  }
}

// ---- Project files ---------------------------------------------------------
for (const required of ['package.json', '.env.example', p('server', 'ResuClean.csproj'), p('web', 'package.json')]) {
  if (!existsSync(required)) fail(`Expected project file is missing: ${required.replace(root + '\\', '').replace(root + '/', '')}`);
}

log('preflight', `root: ${root}`);

if (warnings.length) {
  console.log('');
  for (const w of warnings) console.warn(`  warning: ${w}`);
}

if (problems.length) {
  console.error('');
  console.error('Preflight failed:');
  for (const problem of problems) console.error(`  - ${problem}`);
  console.error('');
  process.exit(1);
}

console.log('');
log('preflight', 'All requirements satisfied.');