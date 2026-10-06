// npm run build: installs web deps if needed, builds the SPA, then restores the backend.
// The backend output goes to server/bin/... so `npm start` runs from the build output with no publish step.
import { existsSync } from 'node:fs';
import { p, webDir, serverDir, log, run, npmCmd, dotnetCmd } from './lib.mjs';

const pre = await run('preflight', process.execPath, [p('scripts', 'preflight.mjs')]);
if (pre !== 0) process.exit(pre);

if (!existsSync(p('web', 'node_modules'))) {
  log('build', 'Installing web dependencies (first run only)...');
  const install = await run('web:install', npmCmd, ['install'], { cwd: webDir });
  if (install !== 0) process.exit(install);
}

const webBuild = await run('web', npmCmd, ['run', 'build'], { cwd: webDir });
if (webBuild !== 0) {
  console.error('[build] Frontend build failed.');
  process.exit(webBuild);
}

const apiBuild = await run('api', dotnetCmd, ['build', p('server', 'ResuClean.csproj'), '-c', 'Release', '--nologo'], {
  cwd: serverDir
});
if (apiBuild !== 0) {
  console.error('[build] Backend build failed.');
  process.exit(apiBuild);
}

log('build', 'Done. Run `npm start` to serve the UI and API on a single port.');