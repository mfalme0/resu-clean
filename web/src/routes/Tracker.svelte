<script lang="ts">
  import { onMount } from 'svelte';
  import { api, type Paged, type PositionImportResult, type PositionPreview, type TrackerEntry, type TrackerImportResult } from '../lib/api';
  import { flash } from '../lib/store';
  import Rule from '../lib/ui/Rule.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Notice from '../lib/ui/Notice.svelte';

  const STATUSES = ['prepared', 'sent', 'replied', 'interview', 'rejected', 'closed'] as const;
  const STATUS_NOTES: Record<string, string> = {
    prepared: 'Kit assembled, nothing sent yet.',
    sent: 'You sent it yourself. Nothing was sent by the tool.',
    replied: 'They came back to you.',
    interview: 'You are at an interview stage.',
    rejected: 'They turned you down. Keep the notes for the next one.',
    // Kept apart from rejected on purpose: the posting is gone, which says nothing about whether
    // anyone looked at your application.
    closed: 'The posting itself is closed. Different from being rejected.'
  };

  let entries: Paged<TrackerEntry> = { items: [], page: 1, size: 50, total: 0, pages: 0, hasMore: false };
  let filter = '';
  let page = 1;
  let editing = new Map<string, string>();

  // Export / import
  let importOpen = false;
  let importText = '';
  let overwrite = false;
  let importResult: TrackerImportResult | null = null;

  // Positions tracked elsewhere: per role rather than per company, and read for the posting URL,
  // title and location hiding in the note.
  let positionsOpen = false;
  let positionsText = '';
  let positionPreview: PositionPreview | null = null;
  let positionResult: PositionImportResult | null = null;
  let previewing = false;

  /**
   * Reads the file and shows what each entry would become, without writing anything.
   *
   * Preview first because the extraction is the interesting part: a posting URL and a role title
   * pulled out of a sentence someone wrote. That is worth checking before it lands in your tracker,
   * and worth seeing which entries carry nothing to extract.
   */
  async function previewPositions() {
    if (positionsText.trim().length === 0) return;
    previewing = true;
    try {
      positionPreview = await api.post<PositionPreview>('/tracker/import-positions/preview', { json: positionsText });
      positionResult = null;
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      previewing = false;
    }
  }

  async function importPositions() {
    if (positionsText.trim().length === 0) return;
    previewing = true;
    try {
      positionResult = await api.post<PositionImportResult>('/tracker/import-positions', {
        json: positionsText,
        overwrite
      });
      await load(1);
      flash(`Imported ${positionResult.imported} position${positionResult.imported === 1 ? '' : 's'}.`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      previewing = false;
    }
  }

  onMount(() => load(1));

  /**
   * The whole tracker as one small JSON file. It carries stages, notes and dates only — no resume
   * text, no job descriptions, no keys — so it is safe to keep alongside your notes.
   */
  async function exportJson() {
    try {
      const res = await fetch('/api/tracker/export', {
        headers: (await import('../lib/api')).hasApiKey() ? { 'X-API-Key': localStorage.getItem('resu-clean.apiKey') ?? '' } : {}
      });
      if (!res.ok) throw new Error('Export failed. Check the server log.');
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = 'applications.json';
      anchor.click();
      URL.revokeObjectURL(url);
      flash('Exported the tracker to applications.json.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function importJson() {
    if (importText.trim().length === 0) return;
    try {
      importResult = await api.post<TrackerImportResult>('/tracker/import', { json: importText, overwrite });
      importText = '';
      await load(1);
      flash(`Imported ${importResult.imported} application${importResult.imported === 1 ? '' : 's'}.`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function load(next: number) {
    try {
      entries = await api.get<Paged<TrackerEntry>>('/tracker', { page: next, size: 50, status: filter || null });
      page = next;
      const draft = new Map<string, string>();
      for (const entry of entries.items) draft.set(entry.id, entry.notes);
      editing = draft;
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function setStatus(entry: TrackerEntry, status: string) {
    try {
      await api.patch(`/tracker/${entry.id}`, { status });
      await load(page);
      flash(`Marked as ${status}.`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function saveNotes(entry: TrackerEntry) {
    const notes = editing.get(entry.id) ?? '';
    if (notes === entry.notes) return;
    try {
      await api.patch(`/tracker/${entry.id}`, { notes });
      await load(page);
      flash('Notes saved.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function remove(entry: TrackerEntry) {
    if (!confirm(`Stop tracking "${entry.jobTitle || 'this role'}"?`)) return;
    try {
      await api.del(`/tracker/${entry.id}`);
      await load(page);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  $: counts = STATUSES.map((status) => ({
    status,
    count: entries.items.filter((e) => e.status === status).length
  }));

  /** Fallback helpers: Svelte templates read better without inline fallbacks. */
  function orDash(value: string): string {
    return value && value.length > 0 ? value : '—';
  }

  function companyOf(entry: TrackerEntry): string {
    return entry.company && entry.company.length > 0 ? entry.company : 'company not stated';
  }

  function selectValue(event: Event): string {
    return (event.currentTarget as HTMLSelectElement).value;
  }

  function textareaValue(event: Event): string {
    return (event.currentTarget as HTMLTextAreaElement).value;
  }
</script>

<svelte:head><title>Tracker &middot; resu-clean</title></svelte:head>

<section class="intro">
  <span class="label">Section five</span>
  <h1>Who said<br />what.</h1>
  <p class="justify lede dropcap">
    A ledger, not a system that acts for you. You move each row along by hand, and you keep the notes
    that make the next application better.
  </p>
</section>

<div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

<section class="block">
  <Rule text="Applications" note="{entries.total} tracked" />

  <div class="transfer">
    <p class="meta">
      The tracker moves as one JSON file, so you can keep it in your notes and read it without this tool.
    </p>
    <div class="transfer-actions">
      <button type="button" class="btn" on:click={exportJson}>Export applications.json</button>
      <button type="button" class="btn" on:click={() => { importOpen = !importOpen; importResult = null; }}>
        {importOpen ? 'Close import' : 'Import…'}
      </button>
    </div>
  </div>

  {#if importOpen}
    <div class="panel">
      <label class="label" for="import-json">Paste the exported file</label>
      <textarea
        id="import-json"
        class="textarea mono-area"
        bind:value={importText}
        placeholder={'{\n  "mkopa": { "stage": "applied", "note": "", "applied": "2026-10-11", "updated": "2026-10-11" },\n  "zeno":   { "stage": "todo",     "note": "", "applied": "", "updated": "" }\n}'}
      ></textarea>
      <label class="check">
        <input type="checkbox" bind:checked={overwrite} />
        <span>Overwrite entries that are already tracked</span>
      </label>
      <p class="meta">
        Off by default, so importing an older file cannot quietly undo applications you have since
        moved forward. Companies not already in your Jobs list are added as manual entries.
      </p>
      <button type="button" class="btn" on:click={importJson} disabled={importText.trim().length === 0}>Import</button>
    </div>

    {#if importResult}
      <Notice
        tone="info"
        title="{importResult.imported} imported: {importResult.created} new, {importResult.updated} updated"
        message={importResult.skipped.length > 0
          ? 'Left alone: ' + importResult.skipped.join('; ')
          : 'Every entry in the file is now tracked.'}
        hint={importResult.skipped.length > 0 ? 'Tick the overwrite box to replace entries that already exist.' : undefined}
      />
    {/if}
  {/if}

  <div class="transfer">
    <p class="meta">
      <strong>Positions tracked somewhere else.</strong> Same file shape, but one entry per role
      rather than one per company — so four Jumia roles stay four roles — and the note is read for a
      posting URL, a title, a location and any closing date it names.
    </p>
    <div class="transfer-actions">
      <button type="button" class="btn" on:click={() => { positionsOpen = !positionsOpen; positionResult = null; }}>
        {positionsOpen ? 'Close position import' : 'Import positions…'}
      </button>
    </div>
  </div>

  {#if positionsOpen}
    <div class="panel">
      <label class="label" for="positions-json">Paste your position file</label>
      <textarea
        id="positions-json"
        class="textarea mono-area"
        bind:value={positionsText}
        placeholder={'{\n  "jumia_nodejs": {\n    "stage": "closed",\n    "note": "Jumia - Senior NodeJS Software Engineer (Nairobi). https://jobs.example/123",\n    "applied": "",\n    "updated": "2026-10-12"\n  }\n}'}
      ></textarea>

      <div class="transfer-actions">
        <button type="button" class="btn" on:click={previewPositions} disabled={positionsText.trim().length === 0}>
          Preview
        </button>
        <button
          type="button"
          class="btn btn-primary"
          on:click={importPositions}
          disabled={positionsText.trim().length === 0 || previewing}
        >
          {previewing ? 'Importing…' : 'Import'}
        </button>
      </div>

      <p class="meta">
        An empty <span class="mono">applied</span> stays empty. Nothing is dated on your behalf: a file
        that says you never applied to something must not come back saying you applied today.
      </p>

      {#if positionPreview}
        <Rule text="What will be created" note="{positionPreview.total} position{positionPreview.total === 1 ? '' : 's'}" />
        <table class="data import-table">
          <thead>
            <tr><th>Key</th><th>Stage</th><th>Role</th><th>Where</th><th>Posting</th></tr>
          </thead>
          <tbody>
            {#each positionPreview.positions as p (p.key)}
              <tr>
                <td class="mono">{p.key}</td>
                <td><span class="badge">{p.stage}</span></td>
                <td>
                  {p.title ?? p.key}
                  {#if p.verified}
                    <span class="badge badge-pass">{p.verified}</span>
                  {/if}
                  {#if p.deadlineAt}
                    <span class="meta">closes {p.deadlineAt.slice(0, 10)}</span>
                  {/if}
                </td>
                <td class="mono">{p.location || p.company || '—'}</td>
                <td class="mono import-url">
                  {#if p.url}
                    <a href={p.url} target="_blank" rel="noopener noreferrer">link</a>
                  {:else}
                    <span class="meta">none found</span>
                  {/if}
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
        <p class="meta">
          {positionPreview.positions.filter((p) => p.missing.length > 0).length} of these have no title or
          posting URL in the note, and will use your key as the label. Add the URL by hand afterwards so
          the role can be re-verified.
        </p>
        {#if positionPreview.warnings.length > 0}
          <Notice tone="info" title="Some entries were skipped" message={positionPreview.warnings.join('; ')} />
        {/if}
      {/if}

      {#if positionResult}
        <Notice
          tone="info"
          title="{positionResult.imported} imported: {positionResult.created} new, {positionResult.updated} updated"
          message={positionResult.skipped.length > 0 ? 'Left alone: ' + positionResult.skipped.join('; ') : 'Every position in the file is now tracked.'}
        />
      {/if}
    </div>
  {/if}

  <div class="tally">
    {#each counts as item (item.status)}
      <button
        type="button"
        class="tally-cell"
        class:active={filter === item.status}
        on:click={() => { filter = filter === item.status ? '' : item.status; load(1); }}
      >
        <span class="label">{item.status}</span>
        <span class="tally-figure">{item.count}</span>
      </button>
    {/each}
  </div>

  {#if entries.items.length === 0}
    <Empty
      title={filter ? `Nothing is "${filter}"` : 'Nothing tracked yet'}
      message={filter
        ? 'Clear the filter to see the rest of your applications.'
        : 'Build a kit in the Kits screen, or add a job to the tracker from there.'}
      action={filter ? 'Clear the filter to see everything.' : 'Kits → build an application kit.'}
    >
      {#if filter}
        <button type="button" class="btn" on:click={() => { filter = ''; load(1); }}>Clear filter</button>
      {/if}
    </Empty>
  {:else}
    <div class="list">
      {#each entries.items as entry (entry.id)}
        <article class="entry" class:rejected={entry.status === 'rejected'}>
          <header class="entry-head">
            <div class="head-main">
              <h3 class="entry-title">{entry.jobTitle || 'Untitled role'}</h3>
              <p class="meta">
                {companyOf(entry)}
                {#if entry.resumeLabel} &middot; {entry.resumeLabel}{/if}
                &middot; added {new Date(entry.createdAt).toLocaleDateString()}
              </p>
            </div>
            <div class="head-actions">
              <select
                class="select status"
                value={entry.status}
                on:change={(e) => setStatus(entry, selectValue(e))}
                aria-label="Status for {entry.jobTitle || 'this role'}"
              >
                {#each STATUSES as status (status)}
                  <option value={status}>{status}</option>
                {/each}
              </select>
              <button type="button" class="btn btn-ghost btn-sm" on:click={() => remove(entry)}>Remove</button>
            </div>
          </header>

          <p class="status-note meta">{STATUS_NOTES[entry.status] ?? ''}</p>

          <label class="sr-only" for={`notes-${entry.id}`}>Notes</label>
          <textarea
            id={`notes-${entry.id}`}
            class="textarea notes"
            value={editing.get(entry.id) ?? ''}
            on:input={(e) => {
              const next = new Map(editing);
              next.set(entry.id, textareaValue(e));
              editing = next;
            }}
            placeholder="What they asked. Who you spoke to. What to change next time."
          ></textarea>
          {#if editing.get(entry.id) !== entry.notes}
            <button type="button" class="btn btn-sm" on:click={() => saveNotes(entry)}>Save notes</button>
          {/if}

          {#if entry.jobId}
            <p class="meta" style="margin-top: var(--s3)">
              Job id <span class="mono">{entry.jobId}</span>
              {#if entry.kitId} &middot; kit <span class="mono">{entry.kitId}</span>{/if}
            </p>
          {/if}
        </article>
      {/each}
    </div>

    {#if entries.pages > 1}
      <nav class="pager" aria-label="Pagination">
        <button type="button" class="btn btn-sm" disabled={page <= 1} on:click={() => load(page - 1)}>Previous</button>
        <span class="meta">Page {page} of {entries.pages}</span>
        <button type="button" class="btn btn-sm" disabled={!entries.hasMore} on:click={() => load(page + 1)}>Next</button>
      </nav>
    {/if}
  {/if}
</section>

<section class="block">
  <Rule text="How this works" />
  <Notice
    tone="info"
    title="You update the statuses"
    message="prepared means the kit exists and nothing has gone out. Move a row to sent once you have actually sent it, then track replies yourself."
    hint="There is no email integration here by design. Nothing in this tool can contact an employer."
  />
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

  .tally {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
    border: var(--hair);
    margin-bottom: var(--s5);
  }

  .tally-cell {
    display: flex;
    flex-direction: column;
    gap: var(--s2);
    align-items: flex-start;
    padding: var(--s4);
    background: transparent;
    border: none;
    border-right: var(--hair-soft);
    cursor: pointer;
    color: var(--ink);
    min-height: var(--tap);
    text-align: left;
    transition: background-color 200ms ease-out, color 200ms ease-out;
  }

  .tally-cell:hover {
    background: var(--neutral-100);
  }

  .tally-cell.active {
    background: var(--ink);
    color: var(--paper);
  }

  .tally-cell.active .label {
    color: var(--neutral-400);
  }

  .tally-figure {
    font-family: var(--font-display);
    font-size: 2rem;
    font-weight: 900;
    line-height: 1;
  }

  .list {
    display: grid;
    gap: var(--s4);
  }

  .transfer {
    margin-bottom: var(--s4);
  }

  .transfer-actions {
    display: flex;
    gap: var(--s2);
    flex-wrap: wrap;
    margin-top: var(--s2);
  }

  .panel {
    border: var(--hair);
    border-left: var(--rule-heavy);
    padding: var(--s5);
    margin-bottom: var(--s5);
    display: grid;
    gap: var(--s3);
  }

  .mono-area {
    font-family: var(--font-mono);
    font-size: 0.85rem;
    min-height: 12rem;
  }

  /* The import preview. Kept compact because there can be thirty of them, and the point is to
     scan the extracted role and posting link rather than read a table. */
  .import-table td {
    vertical-align: top;
    font-size: 0.875rem;
  }

  .import-url a {
    color: var(--accent);
  }

  .check {
    display: flex;
    gap: var(--s2);
    align-items: center;
    font-size: 0.9rem;
  }

  .entry {
    border: var(--hair);
    border-left: var(--rule-heavy);
    padding: var(--s5);
  }

  .entry.rejected {
    border-left-color: var(--accent);
  }

  .entry-head {
    display: flex;
    gap: var(--s4);
    align-items: flex-start;
    flex-wrap: wrap;
  }

  .head-main {
    flex: 1;
    min-width: 0;
  }

  .entry-title {
    font-size: 1.375rem;
    margin-bottom: var(--s1);
  }

  .head-actions {
    display: flex;
    gap: var(--s2);
    align-items: center;
  }

  .status {
    width: auto;
    min-width: 140px;
    border: 1px solid var(--ink);
    min-height: 36px;
  }

  .status-note {
    margin: var(--s3) 0;
  }

  .notes {
    min-height: 90px;
    font-family: var(--font-ui);
    font-size: 0.875rem;
  }

  .pager {
    display: flex;
    align-items: center;
    gap: var(--s4);
    margin-top: var(--s5);
  }

  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
  }

  @media (max-width: 767px) {
    .head-actions {
      width: 100%;
    }

    .status {
      flex: 1;
    }
  }
</style>