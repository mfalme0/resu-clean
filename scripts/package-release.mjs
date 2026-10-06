// npm run release:zips -- zips each published bundle for distribution.
// Guard rails, because these files go on a public download page:
//   - a database or WAL file in the bundle means someone's data is about to be published
//   - any .env file must never ship (a real key lives there)
//   - Schema.sql must survive: the app reads it at startup
import { existsSync, rmSync, readdirSync, statSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { p, log } from './lib.mjs';

const RID_DIRS = ['win-x64', 'osx-x64', 'linux-x64'];

function filesIn(dir) {
  const out = [];
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) out.push(...filesIn(full));
    else out.push(full);
  }
  return out;
}

for (const rid of RID_DIRS) {
  const dir = p('artifacts', `resu-clean-${rid}`);
  if (!existsSync(dir)) {
    console.error(`[zips] Missing ${dir}. Run: npm run publish -- --all-rids`);
    process.exit(1);
  }

  // Strip anything user-generated. Keep Data\Schema.sql, which the app needs.
  const dbFiles = filesIn(dir).filter((f) => /\.(db|db-shm|db-wal)$/i.test(f));
  for (const f of dbFiles) rmSync(f, { force: true });

  const envFiles = filesIn(dir).filter((f) => f.toLowerCase().endsWith('.env'));
  for (const f of envFiles) rmSync(f, { force: true });

  if (!existsSync(join(dir, 'Data', 'Schema.sql'))) {
    console.error(`[zips] ${rid}: Data\\Schema.sql is missing. The released app would crash on every request.`);
    process.exit(1);
  }

  const bin = rid.startsWith('win') ? 'resu-clean.exe' : 'resu-clean';
  if (!existsSync(join(dir, bin))) {
    console.error(`[zips] ${rid}: launcher ${bin} is missing.`);
    process.exit(1);
  }

  const zip = p('artifacts', `resu-clean-${rid}.zip`);
  if (existsSync(zip)) rmSync(zip, { force: true });

  // -r recurse, so the zip holds resu-clean-<rid>/... rather than a bare pile of files.
  const z = spawnSync('powershell', [
    '-NoProfile',
    '-Command',
    `Compress-Archive -Path '${join(dir, '*')}' -DestinationPath '${zip}' -Force`
  ], { encoding: 'utf8' });

  if (z.status !== 0) {
    console.error(`[zips] Failed to zip ${rid}: ${z.stderr || z.stdout}`);
    process.exit(1);
  }

  const mb = (statSync(zip).size / 1024 / 1024).toFixed(1);
  log('zips', `resu-clean-${rid}.zip  ${mb} MB  (removed ${dbFiles.length} db file(s), ${envFiles.length} .env)`);
}

// Cheap content check: a key that looks live must never be in a zip.
for (const rid of RID_DIRS) {
  const dir = p('artifacts', `resu-clean-${rid}`);
  for (const f of filesIn(dir)) {
    if (!/\.(json|md|example|txt|sql)$/i.test(f) && !f.includes('.env')) continue;
    const text = readFileSync(f, 'utf8');
    if (/\b(sk-[A-Za-z0-9]{20,}|cc_[A-Za-z0-9]{20,}|gsk_[A-Za-z0-9]{20,})\b/.test(text)) {
      console.error(`[zips] Possible API key in ${f}. Fix before publishing.`);
      process.exit(1);
    }
  }
}

log('zips', 'All bundles clean and zipped.');
