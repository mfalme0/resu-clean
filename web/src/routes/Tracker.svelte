<script lang="ts">
  import { onMount } from 'svelte';
  import { api, type Paged, type TrackerEntry } from '../lib/api';
  import { flash } from '../lib/store';
  import Rule from '../lib/ui/Rule.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Notice from '../lib/ui/Notice.svelte';

  const STATUSES = ['prepared', 'sent', 'replied', 'interview', 'rejected'] as const;
  const STATUS_NOTES: Record<string, string> = {
    prepared: 'Kit assembled, nothing sent yet.',
    sent: 'You sent it yourself. Nothing was sent by the tool.',
    replied: 'They came back to you.',
    interview: 'You are at an interview stage.',
    rejected: 'Closed. Keep the notes for the next one.'
  };

  let entries: Paged<TrackerEntry> = { items: [], page: 1, size: 50, total: 0, pages: 0, hasMore: false };
  let filter = '';
  let page = 1;
  let editing = new Map<string, string>();

  onMount(() => load(1));

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