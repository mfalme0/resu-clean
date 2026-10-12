<script lang="ts">
  import { onMount } from 'svelte';
  import { api, waitForOp, type DetectResult, type GapRamp, type InterviewPrep, type Job, type JobQuality, type JobRequirements, type Paged, type SearchOutcome, type Source, type SourcePack, type TestResult, type ResumeSummary } from '../lib/api';
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

  // Requirement classification: what a posting demands, split by how closable it is.
  let requirements: Record<string, JobRequirements> = {};
  let readingJob = '';
  let reranking = false;

  // Posting quality: stale, mislabelled, and eligibility-capped. Loaded with the requirements
  // because they answer the same question from different angles: is this worth your afternoon.
  let quality: Record<string, JobQuality> = {};

  // Interview prep and gap ramps are per-job and only fetched when asked for, since both can be
  // slow and neither is needed to read a list.
  let interview: Record<string, InterviewPrep> = {};
  let interviewBusy = '';
  let interviewError: Record<string, string> = {};
  let ramps: Record<string, GapRamp> = {};
  let rampBusy = '';

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

/**
   * Reads one posting and splits what it demands from what it merely mentions. Deterministic unless
   * `fact_check`-style routing is set for `requirements`.
   */
  async function readRequirements(job: Job) {
    if (requirements[job.id]) {
      // Toggle off when it is already open.
      const next = { ...requirements };
      delete next[job.id];
      requirements = next;
      return;
    }
    readingJob = job.id;
    try {
      // Both are cheap and deterministic by default, so they come back together: a posting that is
      // stale and blocked is one message, not two round trips.
      const [report, check] = await Promise.all([
        api.get<JobRequirements>(`/jobs/${job.id}/requirements`),
        api.get<JobQuality>(`/jobs/${job.id}/quality`).catch(() => null)
      ]);
      requirements = { ...requirements, [job.id]: report };
      if (check) quality = { ...quality, [job.id]: check };
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      readingJob = '';
    }
  }

  /**
   * Likely questions from this posting, tied to the claims on your best-fit resume that they will
   * probe. The landmines are reported either way: a claim nothing in your profile backs is a
   * question you do not want to be asked cold.
   */
  async function prepInterview(job: Job) {
    if (interview[job.id]) {
      const next = { ...interview };
      delete next[job.id];
      interview = next;
      return;
    }
    interviewBusy = job.id;
    try {
      const report = await api.get<InterviewPrep>(`/jobs/${job.id}/interview`);
      interview = { ...interview, [job.id]: report };
    } catch (err) {
      // The endpoint refuses without a best-fit version, which is a setup step rather than a
      // failure, so it is said plainly instead of being swallowed.
      const message = err instanceof Error ? err.message : String(err);
      interviewError = { ...interviewError, [job.id]: message };
      flash(message, 'error');
    } finally {
      interviewBusy = '';
    }
  }

  /**
   * A grounded route to closing one named gap. Every suggestion comes back citing a verified fact,
   * so the button answers with what you already do rather than a shopping list of courses.
   */
  async function rampGap(jobId: string, requirement: JobRequirement) {
    rampBusy = `${jobId}:${requirement.text}`;
    try {
      const report = await api.post<GapRamp>('/jobs/gap-ramp', {
        requirement: requirement.text,
        requirementClass: requirement.class,
        missing: requirement.missing
      });
      ramps = { ...ramps, [requirement.text]: report };
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      rampBusy = '';
    }
  }

  /**
   * Re-reads every saved posting so the list can rank on what is winnable rather than on keyword
   * overlap alone. Deterministic, so this costs no model calls however many postings there are.
   */
  async function rerankJobs() {
    reranking = true;
    try {
      const result = await api.post<{ checkedJobs: number; unmetBlockers: number }>('/jobs/requirements/refresh', {});
      await loadJobs(1);
      flash(`Read ${result.checkedJobs} posting${result.checkedJobs === 1 ? '' : 's'}. ${result.unmetBlockers} unmet blocker${result.unmetBlockers === 1 ? '' : 's'} across them.`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      reranking = false;
    }
  }

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
      <div class="list-tools">
        <p class="note">
          Ranked by what is winnable: a posting you can meet first, then one nobody has read yet,
          then one carrying a blocker you do not have.
        </p>
        <button type="button" class="btn btn-sm" on:click={rerankJobs} disabled={reranking}>
          {reranking ? 'Reading postings…' : 'Re-read every posting'}
        </button>
      </div>

      <table class="data">
        <thead>
          <tr>
            <th>Role</th>
            <th>Where</th>
            <th>Source</th>
            <th>Match</th>
            <th>Fits?</th>
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
              <td>
                {#if !job.requirementsChecked}
                  <span class="badge">unread</span>
                {:else if job.unmetBlockers > 0}
                  <span class="badge badge-accent" title="A requirement you cannot satisfy yet">
                    {job.unmetBlockers} blocker{job.unmetBlockers === 1 ? '' : 's'}
                  </span>
                {:else if job.unmetRequired > 0}
                  <span class="badge">{job.unmetRequired} gap{job.unmetRequired === 1 ? '' : 's'}</span>
                {:else}
                  <span class="badge badge-pass">clear</span>
                {/if}
              </td>
              <td class="mono">{job.postedAt ?? '—'}</td>
              <td class="actions">
                <button type="button" class="btn btn-sm" on:click={() => readRequirements(job)}
                        disabled={readingJob === job.id}>
                  {readingJob === job.id ? 'Reading…' : requirements[job.id] ? 'Hide' : 'What it asks'}
                </button>
                <button type="button" class="btn btn-sm" on:click={() => prepInterview(job)}
                        disabled={interviewBusy === job.id}>
                  {interviewBusy === job.id ? 'Reading…' : interview[job.id] ? 'Hide prep' : 'Prep'}
                </button>
                {#if job.url}
                  <a class="btn btn-sm" href={job.url} target="_blank" rel="noopener noreferrer">
                    Open
                    <span class="sr-only">in a new tab</span>
                  </a>
                {/if}
                <button type="button" class="btn btn-sm" on:click={() => removeJob(job)}>Remove</button>
              </td>
            </tr>
            {#if requirements[job.id] || interview[job.id]}
              {@const report = requirements[job.id]}
              {@const check = quality[job.id]}
              <tr class="requirements-row">
                <td colspan="7">
                  <div class="requirements">
                    {#if check && check.flags.length > 0}
                      <div class="req-group">
                        <span class="label">worth knowing first</span>
                        <ul>
                          {#each check.flags as flag (flag.kind + flag.detail)}
                            <li class:unmet={flag.severity === 'warn'}>
                              <span class="req-mark" aria-hidden="true">!</span>
                              {flag.detail}
                              {#if flag.quote}
                                <span class="req-evidence">“{flag.quote}”</span>
                              {/if}
                            </li>
                          {/each}
                        </ul>
                      </div>
                    {/if}
                    {#if report}
                      {#each ['blocker', 'required', 'preferred', 'implicit'] as cls (cls)}
                      {@const items = report.requirements.filter((r) => r.class === cls)}
                      {#if items.length > 0}
                        <div class="req-group">
                          <span class="label">{cls}</span>
                          <ul>
                            {#each items as r (r.text)}
                              <li class:unmet={!r.met}>
                                <span class="req-mark" aria-hidden="true">{r.met ? '✓' : '·'}</span>
                                {r.text}
                                {#if !r.met && r.missing.length > 0}
                                  <span class="req-missing">missing: {r.missing.join(', ')}</span>
                                {/if}
                                {#if r.evidence}
                                  <span class="req-evidence">“{r.evidence}”</span>
                                {/if}
                                {#if !r.met}
                                  <button
                                    type="button"
                                    class="btn btn-sm btn-ghost"
                                    on:click={() => rampGap(job.id, r)}
                                    disabled={rampBusy === `${job.id}:${r.text}`}
                                  >
                                    {rampBusy === `${job.id}:${r.text}` ? 'Thinking…' : 'How do I close this?'}
                                  </button>
                                  {#if ramps[r.text]}
                                    {@const ramp = ramps[r.text]}
                                    <div class="ramp">
                                      {#if !ramp.closable}
                                        <p class="meta">
                                          Not something a weekend closes. It is a requirement of fact, not a skill.
                                        </p>
                                      {:else if ramp.suggestions.length === 0}
                                        <p class="meta">
                                          No suggestion, because nothing here is grounded in something you have
                                          done. Approve a fact or two in Profile and this fills in.
                                        </p>
                                      {:else}
                                        <ul>
                                          {#each ramp.suggestions as s, i (i)}
                                            <li>
                                              {s.text}
                                              <span class="req-evidence">builds on: “{s.buildsOnFactText}” — {s.why}</span>
                                            </li>
                                          {/each}
                                        </ul>
                                      {/if}
                                      {#if ramp.nearestFact}
                                        <p class="meta">
                                          Closest thing you already do: “{ramp.nearestFact}”
                                        </p>
                                      {/if}
                                    </div>
                                  {/if}
                                {/if}
                              </li>
                            {/each}
                          </ul>
                        </div>
                      {/if}
                    {/each}
                    {/if}
                    <p class="meta">
                      {#if report}
                        {#if report.unmetBlockers > 0}
                          {report.unmetBlockers} blocker{report.unmetBlockers === 1 ? '' : 's'} here cannot be closed by
                          studying. The rest are skills, and those can be.
                        {:else}
                          Nothing here is a hard blocker.
                        {/if}
                        Read by the {report.method === 'model' ? `model${report.model ? ` (${report.model})` : ''}` : 'deterministic pass'}.
                      {:else}
                        Press “What it asks” to read what this posting demands.
                      {/if}
                    </p>

                    {#if interviewError[job.id]}
                      <p class="meta">{interviewError[job.id]}</p>
                    {/if}
                    {#if interview[job.id]}
                      {@const prep = interview[job.id]}
                      {#if prep.notes.length > 0}
                        <div class="req-group">
                          <span class="label">be ready to defend these</span>
                          <ul>
                            {#each prep.notes as note (note)}
                              <li class="unmet">{note}</li>
                            {/each}
                          </ul>
                        </div>
                      {/if}
                      {#if prep.questions.length > 0}
                        <div class="req-group">
                          <span class="label">likely questions</span>
                          <ul>
                            {#each prep.questions as q, i (i)}
                              <li>
                                {q.text}
                                <span class="req-evidence">
                                  asks about “{q.groundedIn}”{q.risk && q.riskNote ? ` — ${q.riskNote}` : ''}
                                </span>
                              </li>
                            {/each}
                          </ul>
                        </div>
                      {/if}
                      <p class="meta">
                        {prep.questions.length === 0
                          ? 'No questions written, but the claims above are the ones to have answers ready for.'
                          : `Written by the ${prep.method === 'model' ? `model${prep.model ? ` (${prep.model})` : ''}` : 'deterministic pass'}, which only reads this posting and your best-fit resume.`}
                      </p>
                    {/if}
                  </div>
                </td>
              </tr>
            {/if}
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

  .list-tools {
    display: flex;
    gap: var(--s4);
    align-items: flex-start;
    justify-content: space-between;
    flex-wrap: wrap;
    margin-bottom: var(--s4);
  }

  .requirements-row td {
    background: var(--paper-sunk, rgba(0, 0, 0, 0.03));
  }

  .requirements {
    padding: var(--s3) 0;
  }

  .req-group {
    margin-bottom: var(--s4);
  }

  .req-group ul {
    list-style: none;
    padding: 0;
    margin: var(--s2) 0 0;
    display: grid;
    gap: var(--s1);
  }

  .req-group li {
    display: flex;
    gap: var(--s2);
    align-items: baseline;
    flex-wrap: wrap;
    font-size: 0.9rem;
  }

  .req-mark {
    color: var(--pass);
    font-weight: 700;
  }

  .req-group li.unmet .req-mark {
    color: var(--accent);
  }

  .req-missing {
    font-family: var(--font-mono);
    font-size: 0.75rem;
    opacity: 0.75;
  }

  .req-evidence {
    font-style: italic;
    opacity: 0.6;
    font-size: 0.8rem;
  }

  /* The ramp answer sits under the requirement it answers, so the citation is next to the claim. */
  .ramp {
    flex-basis: 100%;
    padding: var(--s2) var(--s3);
    border-left: 2px solid var(--rule);
    background: var(--paper);
  }

  .ramp ul {
    list-style: none;
    padding: 0;
    margin: 0;
    display: grid;
    gap: var(--s2);
  }

  .ramp li {
    font-size: 0.875rem;
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