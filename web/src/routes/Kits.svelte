<script lang="ts">
  import { onMount } from 'svelte';
  import { api, waitForOp, type Job, type Kit, type Paged, type TrackerEntry } from '../lib/api';
  import { flash } from '../lib/store';
  import Rule from '../lib/ui/Rule.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Notice from '../lib/ui/Notice.svelte';
  import CopyButton from '../lib/ui/CopyButton.svelte';
  import Icon from '../lib/ui/Icon.svelte';

  let jobs: Paged<Job> = { items: [], page: 1, size: 50, total: 0, pages: 0, hasMore: false };
  let kits: Kit[] = [];
  let tracker: Paged<TrackerEntry> = { items: [], page: 1, size: 50, total: 0, pages: 0, hasMore: false };

  let selectedJobId = '';
  let coverLetter = false;
  let building = false;
  let buildStatus = '';
  let current: Kit | null = null;

  let trackingJobId = '';
  let trackNotes = '';

  onMount(loadAll);

  async function loadAll() {
    try {
      const [jobPage, kitPage, trackerPage] = await Promise.all([
        api.get<Paged<Job>>('/jobs', { size: 50 }),
        api.get<Paged<TrackerEntry>>('/tracker', { size: 50 }),
        api.get<Paged<TrackerEntry>>('/tracker', { size: 50 })
      ]);
      jobs = jobPage;
      tracker = kitPage;
      if (!selectedJobId && jobs.items.length > 0) selectedJobId = jobs.items[0].id;
      if (!trackingJobId && jobs.items.length > 0) trackingJobId = jobs.items[0].id;
      void kitPage;
      void trackerPage;
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function build() {
    if (!selectedJobId) {
      flash('Pick a job first.', 'error');
      return;
    }
    building = true;
    buildStatus = 'Writing the email and assembling the bundle…';
    try {
      const accepted = await api.post<{ opId: string }>('/kits', {
        jobId: selectedJobId,
        coverLetter,
        useModel: true
      });
      current = await waitForOp<Kit>(accepted.opId, (s) => {
        buildStatus = s.state === 'running' ? 'Rendering the documents…' : 'Queued…';
      });
      buildStatus = '';
      await loadAll();
      flash('Kit ready. Nothing has been sent: download the files and send them yourself.');
    } catch (err) {
      buildStatus = '';
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      building = false;
    }
  }

  async function addToTracker() {
    if (!trackingJobId) return;
    try {
      await api.post('/tracker', {
        jobId: trackingJobId,
        kitId: current?.id ?? '',
        resumeVersionId: current?.versionId ?? '',
        status: 'prepared',
        notes: trackNotes
      });
      trackNotes = '';
      await loadAll();
      flash('Added to the tracker.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function setStatus(entry: TrackerEntry, status: string) {
    try {
      await api.patch(`/tracker/${entry.id}`, { status });
      await loadAll();
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function openEntry(entry: TrackerEntry) {
    try {
      current = await api.get<Kit>(`/kits/${entry.kitId}`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function removeEntry(entry: TrackerEntry) {
    if (!confirm('Remove this tracker entry?')) return;
    try {
      await api.del(`/tracker/${entry.id}`);
      await loadAll();
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  const STATUSES = ['prepared', 'sent', 'replied', 'interview', 'rejected'] as const;

  function selectValue(event: Event): string {
    return (event.currentTarget as HTMLSelectElement).value;
  }

  $: selectedJob = jobs.items.find((j) => j.id === selectedJobId) ?? null;
</script>

<svelte:head><title>Kits &middot; resu-clean</title></svelte:head>

<section class="intro">
  <span class="label">Section four</span>
  <h1>Ready when<br />you are.</h1>
  <p class="justify lede dropcap">
    A kit is a bundle of files: the email text, a .eml with the resume already attached, the resume in
    both formats, and a zip. You send it. The tool never sends anything, and every claim in the email
    comes from your resume or your approved profile.
  </p>
</section>

<div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

<section class="block">
  <Rule text="Build a kit" note="for a saved job" />

  {#if jobs.items.length === 0}
    <Empty
      title="No jobs to work from"
      message="Search the sources first, or save a posting by hand. A kit needs a job, because it needs the role title, company and posting text."
      action="Jobs → Search, then come back here."
    />
  {:else}
    <div class="build-form">
      <div class="field">
        <label class="label" for="job">Job</label>
        <select id="job" class="select" bind:value={selectedJobId}>
          {#each jobs.items as job (job.id)}
            <option value={job.id}>{job.title}{job.company ? ` · ${job.company}` : ''}{job.location ? ` · ${job.location}` : ''}</option>
          {/each}
        </select>
      </div>

      <label class="toggle">
        <input type="checkbox" bind:checked={coverLetter} />
        <span>Also write a cover letter</span>
      </label>

      <button type="button" class="btn btn-primary" on:click={build} disabled={building}>
        {building ? 'Building…' : 'Build application kit'}
      </button>
      {#if building}<p class="meta" role="status">{buildStatus}</p>{/if}
    </div>

    {#if selectedJob}
      <div class="job-preview">
        <span class="label">The posting</span>
        <h3 class="serif preview-title">{selectedJob.title}</h3>
        <p class="meta">
          {selectedJob.company || 'company not stated'} · {selectedJob.location || 'location not stated'}
          · {selectedJob.matchPct}% keyword match
        </p>
        {#if selectedJob.url}
          <a class="linkish" href={selectedJob.url} target="_blank" rel="noopener noreferrer">Open the original posting</a>
        {/if}
        {#if selectedJob.description}
          <details>
            <summary class="meta">Read the posting text</summary>
            <pre class="posting">{selectedJob.description}</pre>
          </details>
        {/if}
      </div>
    {/if}
  {/if}
</section>

{#if current}
  <section class="block">
    <Rule text="The kit" note={current.readyToSend ? 'ready to send' : 'files missing'} />

    <div class="grid-rule kit-head">
      <div class="cell">
        <span class="label">To</span>
        <p class="mono">
          {current.toAddress || 'not listed in the posting'}
        </p>
        {#if !current.toAddress}
          <p class="meta">The posting did not list an application email, so the .eml has an empty To field.</p>
        {/if}
      </div>
      <div class="cell">
        <span class="label">Subject</span>
        <p class="mono subject">{current.subject}</p>
      </div>
      <div class="cell">
        <span class="label">Resume used</span>
        <p class="mono">{current.versionLabel}</p>
        <p class="meta">{current.wordCount} words in the body</p>
      </div>
      <div class="cell">
        <span class="label">Written by</span>
        <p>{current.generatedBy === 'model' ? 'A model, checked against your profile' : 'The template (no model routed)'}</p>
      </div>
    </div>

    <div class="fields">
      <div class="field-block">
        <div class="field-head">
          <span class="label">Subject line</span>
          <CopyButton text={current.subject} small />
        </div>
        <p class="mono copyable">{current.subject}</p>
      </div>

      <div class="field-block">
        <div class="field-head">
          <span class="label">Email body</span>
          <CopyButton text={current.body} small />
        </div>
        <pre class="copyable">{current.body}</pre>
      </div>

      {#if current.coverLetter}
        <div class="field-block">
          <div class="field-head">
            <span class="label">Cover letter</span>
            <CopyButton text={current.coverLetter} small />
          </div>
          <pre class="copyable">{current.coverLetter}</pre>
        </div>
      {/if}

      {#if current.applyUrl}
        <div class="field-block">
          <span class="label">Application URL</span>
          <p class="mono copyable">{current.applyUrl}</p>
        </div>
      {/if}
    </div>

    {#if current.factsUsed.length > 0}
      <div class="facts-used">
        <span class="label">Facts used from your profile</span>
        <ul>
          {#each current.factsUsed as fact, i (i)}
            <li>{fact}</li>
          {/each}
        </ul>
      </div>
    {/if}

    <span class="label" style="margin-top: var(--s5)">Send it yourself</span>
    <div class="downloads">
      <a class="btn btn-primary" href={`/api/kits/${current.id}/download/eml`} download>
        <Icon name="download" size={18} />
        .eml with attachment
      </a>
      <a class="btn" href={`/api/kits/${current.id}/download/zip`} download>
        <Icon name="download" size={18} />
        .zip bundle
      </a>
      <a class="btn" href={`/api/kits/${current.id}/download/text`} download>
        <Icon name="download" size={18} />
        email as .txt
      </a>
      {#if current.coverLetter}
        <a class="btn" href={`/api/kits/${current.id}/download/cover`} download>
          <Icon name="download" size={18} />
          cover letter .pdf
        </a>
      {/if}
      {#if current.mailtoLink}
        <a class="btn btn-accent" href={current.mailtoLink}>
          <Icon name="external" size={18} />
          Open in your mail app
        </a>
      {/if}
    </div>

    <div class="tracker-add">
      <span class="label">Track this application</span>
      <div class="track-row">
        <input class="input" bind:value={trackNotes} placeholder="Notes: who you spoke to, what they asked" aria-label="Tracker notes" />
        <button type="button" class="btn" on:click={addToTracker}>Add to tracker</button>
      </div>
    </div>
  </section>
{/if}

<section class="block inverted newsprint-texture">
  <div class="inner">
    <Rule text="Application tracker" note="{tracker.total} entries" />

    {#if tracker.items.length === 0}
      <p class="inverted-empty">
        Nothing tracked yet. Build a kit, or add a job to the tracker below, and update the status by
        hand as you hear back. The tool never contacts anyone.
      </p>
    {:else}
      <table class="data inverted-table">
        <thead>
          <tr>
            <th>Role</th>
            <th>Resume</th>
            <th>Status</th>
            <th>Notes</th>
            <th>Updated</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {#each tracker.items as entry (entry.id)}
            <tr>
              <td>
                <span class="job-title">{entry.jobTitle || 'Untitled'}</span>
                <span class="meta company">{entry.company}</span>
              </td>
              <td class="mono">{entry.resumeLabel || '—'}</td>
              <td>
                <select
                  class="select inverted-select"
                  value={entry.status}
                  on:change={(e) => setStatus(entry, selectValue(e))}
                  aria-label="Status for {entry.jobTitle || 'this role'}"
                >
                  {#each STATUSES as status (status)}
                    <option value={status}>{status}</option>
                  {/each}
                </select>
              </td>
              <td class="notes-cell">{entry.notes || '—'}</td>
              <td class="mono">{new Date(entry.updatedAt).toLocaleDateString()}</td>
              <td class="actions">
                {#if entry.kitId}
                  <button type="button" class="btn inverted-btn btn-sm" on:click={() => openEntry(entry)}>Open kit</button>
                {/if}
                <button type="button" class="btn inverted-btn btn-sm" on:click={() => removeEntry(entry)}>Remove</button>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}

    <div class="manual-track">
      <input class="input inverted-input" bind:value={trackingJobId} placeholder="Job id to track" aria-label="Job id to track" />
      <button type="button" class="btn inverted-btn" on:click={addToTracker}>Track a saved job</button>
    </div>
  </div>
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

  .build-form {
    display: flex;
    flex-direction: column;
    gap: var(--s4);
    max-width: 60ch;
  }

  .toggle {
    display: inline-flex;
    align-items: center;
    gap: var(--s2);
    min-height: var(--tap);
    font-family: var(--font-ui);
    font-size: 0.875rem;
    cursor: pointer;
  }

  .toggle input {
    width: 18px;
    height: 18px;
    accent-color: var(--ink);
  }

  .job-preview {
    margin-top: var(--s6);
    border: var(--hair);
    border-left: var(--rule-heavy);
    padding: var(--s5);
    max-width: 74ch;
  }

  .preview-title {
    font-size: 1.5rem;
    margin: var(--s2) 0;
  }

  .linkish {
    font-family: var(--font-ui);
    font-size: 0.875rem;
    color: var(--ink);
    text-decoration: underline;
    text-decoration-color: var(--accent);
    text-underline-offset: 4px;
  }

  .posting {
    font-family: var(--font-mono);
    font-size: 0.75rem;
    white-space: pre-wrap;
    background: var(--neutral-100);
    padding: var(--s4);
    max-height: 340px;
    overflow: auto;
    margin-top: var(--s3);
  }

  .kit-head {
    grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  }

  .cell {
    padding: var(--s4);
  }

  .cell p {
    margin: var(--s2) 0 0;
    font-size: 0.875rem;
  }

  .subject {
    font-weight: 500;
  }

  .fields {
    margin-top: var(--s5);
    display: grid;
    gap: var(--s4);
  }

  .field-block {
    border: var(--hair);
    padding: var(--s4);
  }

  .field-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--s3);
    margin-bottom: var(--s2);
  }

  .copyable {
    font-family: var(--font-mono);
    font-size: 0.8125rem;
    white-space: pre-wrap;
    margin: 0;
    line-height: 1.6;
    word-break: break-word;
  }

  .facts-used {
    margin-top: var(--s5);
  }

  .facts-used ul {
    font-family: var(--font-ui);
    font-size: 0.875rem;
    padding-left: var(--s5);
    margin: var(--s2) 0 0;
  }

  .facts-used li {
    margin-bottom: var(--s1);
  }

  .downloads {
    display: flex;
    flex-wrap: wrap;
    gap: var(--s3);
    margin-top: var(--s3);
  }

  .tracker-add {
    margin-top: var(--s6);
    border-top: var(--hair);
    padding-top: var(--s4);
    max-width: 74ch;
  }

  .track-row {
    display: flex;
    gap: var(--s3);
    margin-top: var(--s2);
  }

  .track-row .input {
    flex: 1;
  }

  .inner {
    padding: var(--s6) 0;
  }

  .inverted-empty {
    font-family: var(--font-ui);
    font-size: 0.9375rem;
    color: var(--neutral-500);
    max-width: 60ch;
  }

  .inverted-table th {
    border-bottom-color: var(--paper);
    color: var(--paper);
  }

  .inverted-table td {
    border-bottom-color: rgba(255, 255, 255, 0.14);
  }

  .job-title {
    display: block;
    font-family: var(--font-ui);
    font-size: 0.9375rem;
  }

  .company {
    display: block;
    margin-top: 2px;
  }

  .notes-cell {
    font-family: var(--font-ui);
    font-size: 0.8125rem;
    color: var(--neutral-500);
    max-width: 28ch;
  }

  .actions {
    display: flex;
    gap: var(--s2);
    flex-wrap: wrap;
  }

  .inverted-btn {
    border-color: var(--paper);
    color: var(--paper);
  }

  .inverted-btn:hover:not(:disabled) {
    background: var(--paper);
    color: var(--ink);
  }

  .inverted-select {
    color: var(--paper);
    border: 1px solid var(--paper);
    background: transparent;
    min-height: 36px;
  }

  .inverted-input {
    color: var(--paper);
    border-bottom-color: var(--paper);
  }

  .inverted-input:focus-visible {
    background: rgba(255, 255, 255, 0.08);
  }

  .manual-track {
    display: flex;
    gap: var(--s3);
    margin-top: var(--s5);
    max-width: 60ch;
  }

  .manual-track .input {
    flex: 1;
  }

  @media (max-width: 767px) {
    .track-row,
    .manual-track {
      flex-direction: column;
    }
  }
</style>