// npm test: backend xUnit suite, then the frontend smoke test.
import { existsSync } from 'node:fs';
import { p, webDir, log, run, npmCmd, dotnetCmd } from './lib.mjs';

const args = process.argv.slice(2);
const only = args.find((a) => a.startsWith('--only='))?.split('=')[1] ?? 'all';

if (only === 'all' || only === 'api') {
  const test = await run('api:test', dotnetCmd, ['test', p('tests', 'ResuClean.Tests', 'ResuClean.Tests.csproj'), '--nologo', '-c', 'Release'], {
    cwd: p('tests', 'ResuClean.Tests')
  });
  if (test !== 0) {
    console.error('[test] Backend tests failed.');
    process.exit(test);
  }
}

if (only === 'all' || only === 'web') {
  if (!existsSync(p('web', 'node_modules'))) {
    log('test', 'Installing web dependencies (first run only)...');
    const install = await run('web:install', npmCmd, ['install'], { cwd: webDir });
    if (install !== 0) process.exit(install);
  }
  const web = await run('web:test', npmCmd, ['test'], { cwd: webDir });
  if (web !== 0) {
    console.error('[test] Frontend tests failed.');
    process.exit(web);
  }
}

console.log('');
log('test', 'All suites passed.');