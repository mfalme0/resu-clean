<script lang="ts">
  import { onMount } from 'svelte';
  import { api, waitForOp, type DetectResult, type Job, type Paged, type SearchOutcome, type Source, type SourcePack, type TestResult, type ResumeSummary } from '../lib/api';
  import { flash } from '../lib/store';
  import Rule from '../lib/ui/Rule.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Notice from '../lib/ui/Notice.svelte';

  const tabs = ['Search', 'Saved jobs', 'Add a job', 'Sources', 'Packs'] as const;
  let tab: (typeof tabs)[number] = 'Search';

  let query = '';
  let location = '';
  let remoteOnly = false;
  let selectedSources: string[] = [];
  let resumes: ResumeSummary[] = [];
  let resumeVersionId = '';

  let searching = false;
  let searchStatus = '';
  let lastRun: SearchOutcome['summary'] | null = null;

  let jobs: Paged<Job> = { items: [], page: 1, size: 25, total: 0, pages: 0, hasMore: false };
  let jobsPage = 1;

  // Manual save
  let manualTitle = '';
  let manualCompany = '';
  let manualLocation = '';
  let manualUrl = '';
  let manualText = '';
  let manualRemote = false;

  // Sources
  let sources: Source[] = [];
  let packs: SourcePack[] = [];
  let detectUrl = '';
  let detectQuery = '';
  let detectLocation = '';
  let detection: DetectResult | null = null;
  let detecting = false;
  let testResult: TestResult | null = null;

  onMount(async () => {
    await Promise.all([loadJobs(1), loadSources(), loadPacks()]);
    try {
      const page = await api.get<Paged<ResumeSummary>>('/resumes', { size: 50 });
      resumes = page.items;
    } catch {
      /* no resumes yet: the search still works, just without ranking */
    }
  });

  async function loadJobs(page: number) {
    try {
      jobs = await api.get<Paged<Job>>('/jobs', { page, size: 25 });
      jobsPage = page;
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function loadSources() {
    try {
      sources = await api.get<Source[]>('/sources');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function loadPacks() {
    try {
      packs = await api.get<SourcePack[]>('/sources/packs/all');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function search() {
    searching = true;
    searchStatus = 'Contacting sources…';
    try {
      const accepted = await api.post<{ opId: string }>('/jobs/search', {
        query: query.trim(),
        location: location.trim(),
        remoteOnly,
        sourceIds: selectedSources.length > 0 ? selectedSources : null,
        resumeVersionId: resumeVersionId || null,
        saveResults: true,
        limit: 50
      });
      const outcome = await waitForOp<SearchOutcome>(accepted.opId, (s) => {
        searchStatus = s.state === 'running' ? 'Fetching feeds…' : 'Queued…';
      });
      lastRun = outcome.summary;
      await loadJobs(1);
      tab = 'Saved jobs';
      const summary = outcome.summary;
      flash(
        `${summary.saved} saved from ${summary.fetched} fetched, ${summary.afterDedupe} after dedupe.`,
        summary.errors.length > 0 ? 'info' : 'info'
      );
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      searching = false;
      searchStatus = '';
    }
  }

  async function saveManualJob() {
    if (!manualTitle.trim() && !manualText.trim()) {
      flash('Give at least a title, or paste the posting text.', 'error');
      return;
    }
    try {
      await api.post('/jobs', {
        title: manualTitle.trim() || null,
        company: manualCompany.trim() || null,
        location: manualLocation.trim() || null,
        url: manualUrl.trim() || null,
        text: manualText.trim() || null,
        remote: manualRemote,
        sourceName: 'manual'
      });
      manualTitle = '';
      manualCompany = '';
      manualLocation = '';
      manualUrl = '';
      manualText = '';
      manualRemote = false;
      await loadJobs(1);
      tab = 'Saved jobs';
      flash('Job saved. It is now in the pipeline for kits and tracking.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function removeJob(job: Job) {
    if (!confirm(`Remove "${job.title}" from your saved jobs?`)) return;
    try {
      await api.del(`/jobs/${job.id}`);
      await loadJobs(jobsPage);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function toggleSource(source: Source) {
    try {
      await api.post(`/sources/${source.id}/enabled`, !source.enabled);
      await loadSources();
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function detect() {
    if (!detectUrl.trim()) {
      flash('Paste a URL first.', 'error');
      return;
    }
    detecting = true;
    testResult = null;
    try {
      detection = await api.post<DetectResult>('/sources/detect', {
        url: detectUrl.trim(),
        query: detectQuery.trim() || null,
        location: detectLocation.trim() || null
      });
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      detecting = false;
    }
  }

  async function saveDetected() {
    if (!detection) return;
    try {
      await api.post('/sources', {
        name: new URL(detection.url).hostname.replace('www.', ''),
        type: detection.suggestedType,
        url: detection.suggestedType === 'link' ? null : detection.url,
        urlTemplate: detection.suggestedType === 'link' ? detection.url : null,
        regions: ['global'],
        enabled: detection.suggestedType !== 'link',
        notes: detection.notes.join(' ')
      });
      detection = null;
      detectUrl = '';
      await loadSources();
      tab = 'Sources';
      flash('Source saved.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function testSource(source: Source) {
    try {
      testResult = await api.post<TestResult>('/sources/test', {
        id: source.id,
        query: query.trim() || null,
        location: location.trim() || null,
        bypassCache: true
      });
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function installPack(pack: SourcePack) {
    try {
      await api.post(`/sources/packs/${pack.id}/install`);
      await loadSources();
      await loadPacks();
      flash(`Installed ${pack.name}. Enable the sources you want, then run a search.`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function removePack(pack: SourcePack) {
    if (!confirm(`Remove the ${pack.name} pack? Sources you have edited are left alone.`)) return;
    try {
      await api.del(`/sources/packs/${pack.id}`);
      await loadSources();
      await loadPacks();
      flash('Pack removed.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  $: enabledCount = sources.filter((s) => s.enabled).length;
</script>

<svelte:head><title>Jobs &middot; resu-clean</title></svelte:head>

<section class="intro">
  <span class="label">Section three</span>
  <h1>Where the<br />work is.</h1>
  <p class="justify lede dropcap">
    Sources live in a registry you control, not in code. Anything without a documented feed or API is a
    link source: the tool builds the search URL and you open it yourself. Nothing here logs in, pays, or
    works around a site's protection.
  </p>
</section>

<div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

<div class="tabs" role="tablist" aria-label="Job sections">
  {#each tabs as name (name)}
    <button
      type="button"
      role="tab"
      class="tab"
      class:active={tab === name}
      aria-selected={tab === name}
      on:click={() => (tab = name)}
    >{name}</button>
  {/each}
</div>

{#if tab === 'Search'}
  <section class="pane">
    <Rule text="Search" note="{enabledCount} source{enabledCount === 1 ? '' : 's'} enabled" />

    <div class="form-grid">
      <div class="field">
        <label class="label" for="q">What</label>
        <input id="q" class="input" bind:value={query} placeholder="data analyst" />
      </div>
      <div class="field">
        <label class="label" for="loc">Where (any country)</label>
        <input id="loc" class="input" bind:value={location} placeholder="Kenya, or Remote" />
      </div>
      <div class="field">
        <label class="label" for="resume">Rank against</label>
        <select id="resume" class="select" bind:value={resumeVersionId}>
          <option value="">No resume (no match %)</option>
          {#each resumes as resume (resume.id)}
            <option value={resume.bestFitVersionId ?? ''}>{resume.label}{resume.bestFitVersionId ? ' · best fit' : ''}</option>
          {/each}
        </select>
      </div>
    </div>

    <label class="toggle">
      <input type="checkbox" bind:checked={remoteOnly} />
      <span>Remote only</span>
    </label>

    {#if sources.length > 0}
      <span class="label">Sources (leave all off to use every enabled source)</span>
      <div class="source-chips">
        {#each sources as source (source.id)}
          <label class="chip" class:off={!source.enabled}>
            <input type="checkbox" value={source.id} bind:group={selectedSources} disabled={!source.enabled} />
            <span>{source.name}</span>
            <span class="chip-type mono">{source.type}</span>
          </label>
        {/each}
      </div>
    {/if}

    <button type="button" class="btn btn-primary" on:click={search} disabled={searching}>
      {searching ? 'Searching…' : 'Search sources'}
    </button>
    {#if searching}<p class="meta" role="status">{searchStatus}</p>{/if}

    {#if lastRun}
      <div class="run-summary">
        <span class="label">Last run</span>
        <p class="run-line">
          fetched <strong>{lastRun.fetched}</strong> &middot;
          after dedupe <strong>{lastRun.afterDedupe}</strong> &middot;
          saved <strong>{lastRun.saved}</strong>
        </p>
        {#if lastRun.errors.length > 0}
          <div class="errors">
            <span class="label label-ink">Sources that could not be used</span>
            <ul>
              {#each lastRun.errors as error (error.source)}
                <li><strong>{error.source}</strong>: {error.message}</li>
              {/each}
            </ul>
          </div>
        {/if}
      </div>
    {/if}
  </section>
{:else if tab === 'Saved jobs'}
  <section class="pane wide">
    <Rule text="Saved jobs" note="{jobs.total} total" />

    {#if jobs.items.length === 0}
      <Empty
        title="No jobs saved yet"
        message="Run a search, or save a posting by hand so anything you found elsewhere can enter the pipeline."
        action="Tip: pick a resume version in Search so results are ranked by match."
      >
        <button type="button" class="btn" on:click={() => (tab = 'Add a job')}>Save a job manually</button>
      </Empty>
    {:else}
      <table class="data">
        <thead>
          <tr>
            <th>Role</th>
            <th>Where</th>
            <th>Source</th>
            <th>Match</th>
            <th>Posted</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {#each jobs.items as job (job.id)}
            <tr>
              <td>
                <span class="job-title">{job.title}</span>
                {#if job.company}<span class="meta company">{job.company}</span>{/if}
                {#if job.remote}<span class="badge badge-pass">remote</span>{/if}
                {#if job.isManual}<span class="badge">manual</span>{/if}
              </td>
              <td class="mono">{job.location || '—'}</td>
              <td class="mono">{job.sourceName}</td>
              <td>
                <span class="match" class:high={job.matchPct >= 60}>{job.matchPct}%</span>
              </td>
              <td class="mono">{job.postedAt ?? '—'}</td>
              <td class="actions">
                {#if job.url}
                  <a class="btn btn-sm" href={job.url} target="_blank" rel="noopener noreferrer">
                    Open
                    <span class="sr-only">in a new tab</span>
                  </a>
                {/if}
                <button type="button" class="btn btn-sm" on:click={() => removeJob(job)}>Remove</button>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>

      {#if jobs.pages > 1}
        <nav class="pager" aria-label="Pagination">
          <button type="button" class="btn btn-sm" disabled={jobsPage <= 1} on:click={() => loadJobs(jobsPage - 1)}>Previous</button>
          <span class="meta">Page {jobsPage} of {jobs.pages}</span>
          <button type="button" class="btn btn-sm" disabled={!jobs.hasMore} on:click={() => loadJobs(jobsPage + 1)}>Next</button>
        </nav>
      {/if}
    {/if}
  </section>
{:else if tab === 'Add a job'}
  <section class="pane">
    <Rule text="Save a job by hand" note="any posting" />

    <p class="note">
      Paste the posting text so keyword matching and the application kit have something real to work from.
      This is the intended route for boards that forbid automated access.
    </p>

    <div class="form-grid">
      <div class="field">
        <label class="label" for="mt">Title</label>
        <input id="mt" class="input" bind:value={manualTitle} />
      </div>
      <div class="field">
        <label class="label" for="mc">Company</label>
        <input id="mc" class="input" bind:value={manualCompany} />
      </div>
      <div class="field">
        <label class="label" for="ml">Location</label>
        <input id="ml" class="input" bind:value={manualLocation} />
      </div>
      <div class="field">
        <label class="label" for="mu">URL</label>
        <input id="mu" class="input" bind:value={manualUrl} placeholder="https://" />
      </div>
    </div>

    <label class="toggle">
      <input type="checkbox" bind:checked={manualRemote} />
      <span>Remote</span>
    </label>

    <label class="label" for="mtext">Posting text</label>
    <textarea id="mtext" class="textarea" bind:value={manualText} placeholder="Paste the whole posting."></textarea>

    <button type="button" class="btn btn-primary" on:click={saveManualJob}>Save job</button>
  </section>
{:else if tab === 'Sources'}
  <section class="pane wide">
    <Rule text="Add a source by URL" note="auto-detect, then preview" />

    <div class="detect-row">
      <input class="input" bind:value={detectUrl} placeholder="https://example.com/jobs" aria-label="Source URL" />
      <input class="input" bind:value={detectQuery} placeholder="query" aria-label="Detection query" />
      <input class="input" bind:value={detectLocation} placeholder="location" aria-label="Detection location" />
      <button type="button" class="btn btn-primary" on:click={detect} disabled={detecting}>
        {detecting ? 'Testing…' : 'Detect'}
      </button>
    </div>

    {#if detection}
      <div class="detection">
        <div class="detect-head">
          <span class="badge" class:badge-accent={detection.suggestedType === 'link'}>{detection.suggestedType}</span>
          <span class="meta">{detection.confidence} confidence</span>
        </div>
        <p class="mono url">{detection.url}</p>

        {#if detection.notes.length > 0}
          <ul class="notes">
            {#each detection.notes as note, i (i)}
              <li>{note}</li>
            {/each}
          </ul>
        {/if}

        {#if detection.preview.length > 0}
          <span class="label">Parsed preview</span>
          <table class="data">
            <thead>
              <tr><th>Title</th><th>Company</th><th>Where</th><th>Posted</th></tr>
            </thead>
            <tbody>
              {#each detection.preview as item, i (i)}
                <tr>
                  <td>{item.title}</td>
                  <td>{item.company || '—'}</td>
                  <td class="mono">{item.location || '—'}</td>
                  <td class="mono">{item.postedAt || '—'}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        {/if}

        <button type="button" class="btn btn-primary" on:click={saveDetected}>Save this source</button>
      </div>
    {/if}

    <div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

    <Rule text="Source registry" note="{sources.length} configured" />

    {#if sources.length === 0}
      <Empty
        title="No sources yet"
        message="Install a pack below, or paste a URL above and let the tool work out what it is."
        action="Be honest about what each source allows: resu-clean never fetches a source whose robots.txt disallows it."
      />
    {:else}
      <table class="data">
        <thead>
          <tr>
            <th>Name</th>
            <th>Type</th>
            <th>Regions</th>
            <th>Notes</th>
            <th>Enabled</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {#each sources as source (source.id)}
            <tr>
              <td>
                <span class="job-title">{source.name}</span>
                {#if source.urlTemplate || source.url}
                  <span class="meta company mono">{(source.urlTemplate ?? source.url ?? '').slice(0, 60)}</span>
                {/if}
              </td>
              <td><span class="badge">{source.type}</span></td>
              <td class="mono">{source.regions.join(', ')}</td>
              <td class="note-cell">{source.notes}</td>
              <td>
                <button
                  type="button"
                  class="btn btn-sm"
                  class:btn-primary={source.enabled}
                  on:click={() => toggleSource(source)}
                >{source.enabled ? 'On' : 'Off'}</button>
              </td>
              <td class="actions">
                <button type="button" class="btn btn-sm" on:click={() => testSource(source)}>Test</button>
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    {/if}

    {#if testResult}
      <div class="detection">
        <Rule text="Test result" note="{testResult.count} parsed in {testResult.elapsedMs} ms" />
        {#if testResult.ok}
          <table class="data">
            <thead>
              <tr><th>Title</th><th>Company</th><th>Where</th><th>Posted</th></tr>
            </thead>
            <tbody>
              {#each testResult.items as item, i (i)}
                <tr>
                  <td>{item.title}</td>
                  <td>{item.company || '—'}</td>
                  <td class="mono">{item.location || '—'}</td>
                  <td class="mono">{item.postedAt || '—'}</td>
                </tr>
              {/each}
            </tbody>
          </table>
        {:else}
          <Notice title="This source returned nothing usable" message={testResult.error ?? 'No listings were parsed.'} hint="Check the field mapping or CSS selectors, or save it as a link source." />
        {/if}
      </div>
    {/if}
  </section>
{:else if tab === 'Packs'}
  <section class="pane wide">
    <Rule text="Seed packs" note="enable, disable or delete" />

    <div class="grid-rule packs">
      {#each packs as pack (pack.id)}
        <div class="cell">
          <div class="pack-head">
            <h3 class="card-title">{pack.name}</h3>
            {#if pack.installed}<span class="badge badge-pass">installed</span>{/if}
          </div>
          <p class="note">{pack.description}</p>
          <table class="data compact">
            <thead>
              <tr><th>Source</th><th>Type</th><th>Why</th></tr>
            </thead>
            <tbody>
              {#each pack.entries as entry (entry.key)}
                <tr>
                  <td>{entry.name}</td>
                  <td><span class="badge" class:badge-accent={entry.type === 'link'}>{entry.type}</span></td>
                  <td class="note-cell">{entry.notes}</td>
                </tr>
              {/each}
            </tbody>
          </table>
          <div class="pack-actions">
            <button type="button" class="btn btn-primary btn-sm" on:click={() => installPack(pack)}>
              {pack.installed ? 'Reinstall' : 'Install'}
            </button>
            {#if pack.installed}
              <button type="button" class="btn btn-sm" on:click={() => removePack(pack)}>Remove</button>
            {/if}
          </div>
        </div>
      {/each}
    </div>

    <p class="meta" style="margin-top: var(--s4)">
      Full findings, with the date each source was checked, are in docs/SOURCES.md in the repository.
    </p>
  </section>
{/if}

<style>
  .intro {
    max-width: 60ch;
  }

  .lede {
    margin-top: var(--s4);
  }

  .tabs {
    display: flex;
    flex-wrap: wrap;
    border-bottom: var(--hair);
    margin: var(--s6) 0 var(--s5);
  }

  .tab {
    font-family: var(--font-ui);
    font-size: 0.75rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    padding: var(--s3) var(--s4);
    min-height: var(--tap);
    background: transparent;
    border: none;
    border-right: var(--hair-soft);
    cursor: pointer;
    color: var(--ink);
  }

  .tab:hover {
    background: var(--neutral-100);
  }

  .tab.active {
    background: var(--ink);
    color: var(--paper);
  }

  .pane {
    max-width: 74ch;
  }

  .pane.wide {
    max-width: none;
  }

  .form-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    gap: var(--s4);
    margin-bottom: var(--s4);
  }

  .toggle {
    display: inline-flex;
    align-items: center;
    gap: var(--s2);
    min-height: var(--tap);
    font-family: var(--font-ui);
    font-size: 0.875rem;
    margin-bottom: var(--s4);
    cursor: pointer;
  }

  .toggle input {
    width: 18px;
    height: 18px;
    accent-color: var(--ink);
  }

  .source-chips {
    display: flex;
    flex-wrap: wrap;
    gap: var(--s2);
    margin: var(--s2) 0 var(--s5);
  }

  .chip {
    display: inline-flex;
    align-items: center;
    gap: var(--s2);
    border: 1px solid var(--ink);
    padding: var(--s2) var(--s3);
    min-height: 36px;
    font-family: var(--font-ui);
    font-size: 0.8125rem;
    cursor: pointer;
    transition: background-color 200ms ease-out;
  }

  .chip:hover {
    background: var(--neutral-100);
  }

  .chip.off {
    opacity: 0.45;
    cursor: not-allowed;
  }

  .chip-type {
    font-size: 0.625rem;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    color: var(--neutral-500);
  }

  .run-summary {
    margin-top: var(--s6);
    border-top: var(--hair);
    padding-top: var(--s4);
  }

  .run-line {
    font-family: var(--font-display);
    font-size: 1.25rem;
    margin: var(--s2) 0;
  }

  .errors ul,
  .notes {
    font-family: var(--font-ui);
    font-size: 0.875rem;
    padding-left: var(--s5);
    margin: var(--s2) 0 0;
  }

  .errors li,
  .notes li {
    margin-bottom: var(--s2);
  }

  .note {
    font-family: var(--font-ui);
    font-size: 0.875rem;
    color: var(--neutral-600);
  }

  .job-title {
    display: block;
    font-family: var(--font-ui);
    font-size: 0.9375rem;
    font-weight: 500;
  }

  .company {
    display: block;
    margin: 2px 0 4px;
  }

  .note-cell {
    font-family: var(--font-ui);
    font-size: 0.8125rem;
    color: var(--neutral-600);
    max-width: 42ch;
  }

  .match {
    font-family: var(--font-mono);
    font-weight: 700;
  }

  .match.high {
    color: var(--pass);
  }

  .actions {
    display: flex;
    gap: var(--s2);
    flex-wrap: wrap;
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

  .detect-row {
    display: grid;
    grid-template-columns: 2fr 1fr 1fr auto;
    gap: var(--s3);
    align-items: end;
    margin-bottom: var(--s4);
  }

  .detection {
    border: var(--hair);
    border-left: var(--rule-heavy);
    padding: var(--s5);
  }

  .detect-head {
    display: flex;
    align-items: center;
    gap: var(--s3);
  }

  .url {
    font-size: 0.8125rem;
    word-break: break-all;
    margin: var(--s2) 0;
  }

  .packs {
    grid-template-columns: repeat(auto-fit, minmax(400px, 1fr));
  }

  .cell {
    padding: var(--s5);
  }

  .pack-head {
    display: flex;
    align-items: center;
    gap: var(--s3);
    margin-bottom: var(--s2);
  }

  .pack-head .card-title {
    margin: 0;
  }

  .pack-actions {
    display: flex;
    gap: var(--s2);
    margin-top: var(--s4);
  }

  table.compact td,
  table.compact th {
    padding: var(--s2);
    font-size: 0.75rem;
  }

  @media (max-width: 767px) {
    .detect-row {
      grid-template-columns: 1fr;
    }

    .packs {
      grid-template-columns: 1fr;
    }
  }
</style>