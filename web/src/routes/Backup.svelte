<script lang="ts">
  import { onMount } from 'svelte';
  import { api, type ExportResult, type PreviewResult, type ImportResult } from '../lib/api';
  import { flash } from '../lib/store';
  import Rule from '../lib/ui/Rule.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Notice from '../lib/ui/Notice.svelte';

  // Export state
  let exportDest = '';
  let exportPassword = '';
  let exporting = false;
  let exportResult: ExportResult | null = null;

  // Preview state
  let previewArchive = '';
  let previewPassword = '';
  let previewing = false;
  let previewResult: PreviewResult | null = null;

  // Import state
  let importArchive = '';
  let importPassword = '';
  let importing = false;
  let importResult: ImportResult | null = null;

  async function doExport() {
    if (!exportDest.trim()) { flash('Enter a destination path first.', 'error'); return; }
    if (!exportPassword.trim()) { flash('Enter a password to encrypt the backup.', 'error'); return; }
    exporting = true;
    exportResult = null;
    try {
      exportResult = await api.post<ExportResult>('/data/export', {
        destination: exportDest.trim(),
        password: exportPassword.trim()
      });
      flash(`Exported ${exportResult.filesIncluded} file${exportResult.filesIncluded === 1 ? '' : 's'} (${Math.round(exportResult.bytes / 1024)} KB) to ${exportResult.fileName}.`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      exporting = false;
    }
  }

  async function doPreview() {
    if (!previewArchive.trim()) { flash('Enter a backup file path first.', 'error'); return; }
    if (!previewPassword.trim()) { flash('Enter the backup password.', 'error'); return; }
    previewing = true;
    previewResult = null;
    try {
      previewResult = await api.post<PreviewResult>('/data/preview', {
        archivePath: previewArchive.trim(),
        password: previewPassword.trim()
      });
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      previewing = false;
    }
  }

  async function doImport() {
    if (!importArchive.trim()) { flash('Enter a backup file path first.', 'error'); return; }
    if (!importPassword.trim()) { flash('Enter the backup password.', 'error'); return; }
    if (!confirm('This will replace your current data directory with the backup. Continue?')) return;
    importing = true;
    importResult = null;
    try {
      importResult = await api.post<ImportResult>('/data/import', {
        archivePath: importArchive.trim(),
        password: importPassword.trim()
      });
      flash(`Restored ${importResult.filesRestored} file${importResult.filesRestored === 1 ? '' : 's'} (${Math.round(importResult.bytesRestored / 1024)} KB). The database has been replaced.`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      importing = false;
    }
  }

  function formatBytes(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }
</script>

<svelte:head><title>Backup &middot; resu-clean</title></svelte:head>

<section class="intro">
  <span class="label">Section eight</span>
  <h1>Back up<br />your data.</h1>
  <p class="justify lede dropcap">
    Everything resu-clean knows — your database, verified profile, saved jobs, kits, and stored
    API keys — lives in one folder. This screen packs that folder into a single encrypted file you
    can copy to a USB stick, another machine, or a safe place. Nothing is sent anywhere; the file
    is encrypted with a password you choose, and nothing inside can be read without it.
  </p>
</section>

<div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

<section class="block">
  <Rule text="Export" note="write an encrypted backup to any path" />

  <div class="form-grid">
    <div class="field">
      <label class="label" for="export-dest">Destination file</label>
      <input
        id="export-dest"
        class="input"
        bind:value={exportDest}
        placeholder="~/Documents/resu-clean-backup.resubak"
      />
      <p class="meta">Ends in <span class="mono">.resubak</span> if you omit the extension.</p>
    </div>
    <div class="field">
      <label class="label" for="export-pass">Backup password</label>
      <input id="export-pass" class="input" type="password" bind:value={exportPassword} />
      <p class="meta">Needed to restore this backup. Forget it and the file is unrecoverable.</p>
    </div>
  </div>

  <button type="button" class="btn btn-primary" on:click={doExport} disabled={exporting}>
    {exporting ? 'Exporting…' : 'Export encrypted backup'}
  </button>

  {#if exportResult}
    <Notice tone="info" title="Backup written">
      {exportResult.fileName} &middot; {formatBytes(exportResult.bytes)} &middot;
      {exportResult.filesIncluded} file{exportResult.filesIncluded === 1 ? '' : 's'}
    </Notice>
  {/if}
</section>

<section class="block">
  <Rule text="Preview" note="check a backup before restoring it" />

  <div class="form-grid">
    <div class="field">
      <label class="label" for="preview-archive">Backup file</label>
      <input id="preview-archive" class="input" bind:value={previewArchive} placeholder="~/Documents/resu-clean-backup.resubak" />
    </div>
    <div class="field">
      <label class="label" for="preview-pass">Password</label>
      <input id="preview-pass" class="input" type="password" bind:value={previewPassword} />
    </div>
  </div>

  <button type="button" class="btn" on:click={doPreview} disabled={previewing}>
    {previewing ? 'Reading…' : 'Preview backup'}
  </button>

  {#if previewResult}
    <div class="panel">
      <p><strong>{previewResult.files}</strong> file{previewResult.files === 1 ? '' : 's'}, {formatBytes(previewResult.totalBytes)} total.</p>
      {#if previewResult.looksValid}
        <p class="ok">Contains a resu-clean database — safe to restore.</p>
      {:else}
        <p class="bad">No database found in this backup.</p>
      {/if}
      {#if previewResult.databasePath}
        <p class="meta">Database: <span class="mono">{previewResult.databasePath}</span></p>
      {/if}
      {#if previewResult.machineKeyPath}
        <p class="meta">Machine key: <span class="mono">{previewResult.machineKeyPath}</span></p>
      {/if}
    </div>
  {/if}
</section>

<section class="block">
  <Rule text="Import" note="replace your data directory with a backup" />

  <div class="form-grid">
    <div class="field">
      <label class="label" for="import-archive">Backup file</label>
      <input id="import-archive" class="input" bind:value={importArchive} placeholder="~/Documents/resu-clean-backup.resubak" />
    </div>
    <div class="field">
      <label class="label" for="import-pass">Password</label>
      <input id="import-pass" class="input" type="password" bind:value={importPassword} />
    </div>
  </div>

  <p class="meta warning">
    Importing replaces your current database, verified profile, saved jobs, kits and stored keys.
    Anything created since the backup was made will be lost unless you back it up first.
  </p>

  <button type="button" class="btn btn-primary" on:click={doImport} disabled={importing}>
    {importing ? 'Restoring…' : 'Import encrypted backup'}
  </button>

  {#if importResult}
    <Notice tone="info" title="Data restored">
      {importResult.filesRestored} file{importResult.filesRestored === 1 ? '' : 's'} replaced.
    </Notice>
  {/if}
</section>

<section class="block">
  <Rule text="What travels with a backup" />

  <table class="data">
    <thead>
      <tr><th>In the backup</th><th>Notes</th></tr>
    </thead>
    <tbody>
      <tr>
        <td>resu-clean.db</td>
        <td>Your database: jobs, tracker, profile facts, sources, kits, routes, model usage.</td>
      </tr>
      <tr>
        <td>machine.key <span class="mono">(macOS / Linux)</span></td>
        <td>Lets stored API keys decrypt on the same machine. On Windows, keys use DPAPI bound to your user account and need re-entry on a different account or machine.</td>
      </tr>
      <tr>
        <td>kits/, documents/, uploads/</td>
        <td>Generated application kits, resume PDFs/DOCXs, and uploaded resume files.</td>
      </tr>
    </tbody>
  </table>

  <p class="meta">
    Not stored: your .env file (it is gitignored and lives outside the data directory). Set it up
    again on any machine you restore to.
  </p>
</section>

<style>
  .intro {
    max-width: 60ch;
  }

  .lede {
    margin-top: var(--s4);
  }

  .block {
    margin-top: var(--s7);
  }

  .form-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: var(--s4);
    margin-bottom: var(--s4);
  }

  .field {
    display: flex;
    flex-direction: column;
    gap: var(--s1);
  }

  .panel {
    border: var(--hair);
    border-left: var(--rule-heavy);
    padding: var(--s5);
    background: var(--paper);
    margin-top: var(--s3);
  }

  .ok {
    color: var(--ink);
  }

  .bad {
    color: var(--accent);
  }

  .warning {
    color: var(--accent);
    font-size: 0.875rem;
  }

  .data {
    width: 100%;
    border-collapse: collapse;
    margin-top: var(--s3);
    font-size: 0.875rem;
  }

  .data th {
    text-align: left;
    color: var(--neutral-600);
    font-family: var(--font-mono);
    font-size: 0.6875rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    padding: var(--s2) var(--s3);
    border-bottom: var(--hair);
  }

  .data td {
    padding: var(--s2) var(--s3);
    border-bottom: var(--hair-soft);
    vertical-align: top;
  }

  .data tr:last-child td {
    border-bottom: none;
  }

  .mono {
    font-family: var(--font-mono);
  }

  @media (max-width: 767px) {
    .form-grid {
      grid-template-columns: 1fr;
    }
  }
</style>