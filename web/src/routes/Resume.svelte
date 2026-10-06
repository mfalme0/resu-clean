<script lang="ts">
  import { onMount } from 'svelte';
  import { api, upload, waitForOp, ApiError, type Paged, type ResumeDetail, type ResumeSummary, type ResumeVersion, type AtsReport, type CleanResult, type UpdateResult } from '../lib/api';
  import { flash, pendingCount, capabilities } from '../lib/store';
  import Rule from '../lib/ui/Rule.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Notice from '../lib/ui/Notice.svelte';
  import Icon from '../lib/ui/Icon.svelte';
  import AtsReportView from '../lib/ui/AtsReport.svelte';
  import CopyButton from '../lib/ui/CopyButton.svelte';

  let resumes: ResumeSummary[] = [];
  let detail: ResumeDetail | null = null;
  let selectedVersionId = '';

  let pastedText = '';
  let pasteLabel = '';
  let fileInput: HTMLInputElement;
  let uploading = false;

  let busy: '' | null = null;
  let progress = '';
  let clean: CleanResult | null = null;
  let ats: AtsReport | null = null;
  let update: UpdateResult | null = null;
  let jobDescription = '';
  let instruction = '';
  let versionText = '';

  let loadError: string | null = null;

  const tabs = ['Resume', 'Versions', 'Clean', 'ATS scan', 'Tailor'] as const;
  let tab: (typeof tabs)[number] = 'Resume';

  onMount(async () => {
    await listResumes();
  });

  async function listResumes() {
    try {
      const page = await api.get<Paged<ResumeSummary>>('/resumes', { size: 50 });
      resumes = page.items;
      if (resumes.length > 0 && !selectedVersionId) await openResume(resumes[0].id);
    } catch (err) {
      loadError = err instanceof Error ? err.message : String(err);
    }
  }

  async function openResume(id: string) {
    try {
      detail = await api.get<ResumeDetail>(`/resumes/${id}`);
      selectedVersionId = detail.summary.currentVersionId ?? detail.versions[0]?.id ?? '';
      await loadVersionText();
      clean = null;
      ats = null;
      update = null;
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function loadVersionText() {
    if (!selectedVersionId) {
      versionText = '';
      return;
    }
    try {
      const result = await api.get<{ versionId: string; text: string }>(`/resumes/versions/${selectedVersionId}/text`);
      versionText = result.text;
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function submitPaste() {
    if (pastedText.trim().length === 0) {
      flash('Paste your resume text first.', 'error');
      return;
    }
    uploading = true;
    try {
      await api.post('/resumes', { label: pasteLabel || 'Resume', text: pastedText });
      pastedText = '';
      pasteLabel = '';
      await listResumes();
      flash('Resume added.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      uploading = false;
    }
  }

  async function submitFile(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    uploading = true;
    try {
      const form = new FormData();
      form.append('file', file);
      if (pasteLabel) form.append('label', pasteLabel);
      await upload('/resumes', form);
      await listResumes();
      flash('File imported.');
    } catch (err) {
      const message = err instanceof ApiError ? err.message : String(err);
      flash(message, 'error');
    } finally {
      uploading = false;
      input.value = '';
    }
  }

  async function runClean() {
    if (!selectedVersionId) return;
    busy = 'clean';
    progress = 'Cleaning deterministically, then asking the model if one is routed…';
    try {
      const accepted = await api.post<{ opId: string }>(`/resumes/versions/${selectedVersionId}/clean`, { useModel: true });
      clean = await waitForOp<CleanResult>(accepted.opId, (s) => (progress = s.state === 'running' ? 'Working…' : 'Queued…'));
      progress = '';
      await listResumes();
      await loadVersionText();
      flash(clean.usedModel ? `Cleaned with ${clean.model}.` : 'Cleaned. No model was routed, so only the deterministic pass ran.');
    } catch (err) {
      progress = '';
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      busy = null;
    }
  }

  async function runAts() {
    if (!selectedVersionId) return;
    busy = 'ats';
    progress = 'Scoring against the ATS rules…';
    try {
      const accepted = await api.post<{ opId: string }>(`/resumes/versions/${selectedVersionId}/ats`, {
        jobDescription: jobDescription || null,
        useModel: true
      });
      ats = await waitForOp<AtsReport>(accepted.opId, () => (progress = 'Scoring…'));
      progress = '';
    } catch (err) {
      progress = '';
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      busy = null;
    }
  }

  async function runUpdate() {
    if (!selectedVersionId) return;
    busy = 'update';
    progress = 'Checking the instruction for new information…';
    try {
      const accepted = await api.post<{ opId: string }>(`/resumes/versions/${selectedVersionId}/update`, {
        instruction: instruction || null,
        jobDescription: jobDescription || null,
        useModel: true
      });
      update = await waitForOp<UpdateResult>(accepted.opId, () => (progress = 'Working…'));
      progress = '';
      if (update.blockedByPending) {
        $pendingCount = update.pending.length;
        tab = 'Tailor';
        flash('Stopped: there is unapproved information in your instruction. Approve or reject it first.');
      } else if (update.versionId) {
        await listResumes();
        await loadVersionText();
        flash('Tailored version saved as a new version.');
      }
    } catch (err) {
      progress = '';
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      busy = null;
    }
  }

  async function markBestFit(version: ResumeVersion) {
    try {
      await api.post(`/resumes/versions/${version.id}/best-fit`);
      await openResume(version.resumeId);
      flash(`"${version.label}" is now the best-fit version. Job search ranks against it.`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function exportVersion(format: 'pdf' | 'docx') {
    try {
      const res = await fetch(`/api/resumes/versions/${selectedVersionId}/export`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...(await import('../lib/api')).hasApiKey() ? { 'X-API-Key': localStorage.getItem('resu-clean.apiKey') ?? '' } : {} },
        body: JSON.stringify({ versionId: selectedVersionId, format })
      });
      if (!res.ok) throw new Error('Export failed. Check the server log.');
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = `resume.${format}`;
      anchor.click();
      URL.revokeObjectURL(url);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function removeResume(id: string) {
    if (!confirm('Delete this resume and all its versions? This cannot be undone.')) return;
    try {
      await api.del(`/resumes/${id}`);
      if (detail?.summary.id === id) {
        detail = null;
        selectedVersionId = '';
      }
      await listResumes();
      flash('Resume deleted.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  $: selectedVersion = detail?.versions.find((v) => v.id === selectedVersionId) ?? null;
</script>

<svelte:head><title>Resume &middot; resu-clean</title></svelte:head>

<section class="intro">
  <span class="label">Section one</span>
  <h1>The resume,<br />cleaned.</h1>
  <p class="justify lede dropcap">
    Everything downstream depends on what you put here. Import a file or paste the text, then run the
    deterministic clean, the ATS scan, and the tailoring step. The tool will never invent a fact, and
    anything new you mention has to be approved before it enters your profile.
  </p>
</section>

<div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

{#if loadError}
  <Notice title="Could not load your resumes" message={loadError} hint="Check that the backend is running, then reload." />
{/if}

<section class="block">
  <Rule text="Import" note="paste, .txt, .docx or .pdf" />

  <div class="grid-rule two">
    <div class="cell">
      <label class="label" for="paste">Paste the text</label>
      <textarea
        id="paste"
        class="textarea"
        bind:value={pastedText}
        placeholder="Paste your resume exactly as it is now. Messy is fine: the cleaner handles bullets, icons and spacing."
      ></textarea>
      <div class="row">
        <input class="input" bind:value={pasteLabel} placeholder="Label (optional)" aria-label="Resume label" />
        <button type="button" class="btn btn-primary" on:click={submitPaste} disabled={uploading}>
          {uploading ? 'Importing…' : 'Add resume'}
        </button>
      </div>
    </div>

    <div class="cell">
      <span class="label">Or upload a file</span>
      <div class="drop" on:click={() => fileInput.click()} on:keydown={(e) => e.key === 'Enter' && fileInput.click()} role="button" tabindex="0">
        <Icon name="doc" size={32} />
        <p class="drop-title">Choose a file</p>
        <p class="meta">.docx, .pdf with a text layer, or .txt &middot; up to {$capabilities?.maxUploadMb ?? 10} MB</p>
      </div>
      <input
        bind:this={fileInput}
        type="file"
        accept=".txt,.text,.md,.docx,.pdf"
        on:change={submitFile}
        style="display:none"
      />
      <p class="note">
        Tables in a .docx are flattened in reading order and a warning is shown, because they are the
        usual reason a parser loses your content.
      </p>
    </div>
  </div>
</section>

{#if resumes.length === 0}
  <Empty
    title="No resumes yet"
    message="Import one above. Every later step &mdash; clean, ATS scan, tailoring, application kits &mdash; works from these versions."
    action="Next: paste a resume, or upload a .docx you exported from Word."
  />
{:else}
  <section class="block">
    <Rule text="Your resumes" note="{resumes.length} on file" />

    <div class="grid-rule list">
      {#each resumes as resume (resume.id)}
        <div class="cell row-card" class:selected={detail?.summary.id === resume.id}>
          <button type="button" class="pick" on:click={() => openResume(resume.id)}>
            <span class="serif pick-title">{resume.label}</span>
            <span class="meta">{resume.versionCount} version{resume.versionCount === 1 ? '' : 's'} &middot; updated {new Date(resume.updatedAt).toLocaleDateString()}</span>
          </button>
          <button type="button" class="btn btn-ghost btn-sm" on:click={() => removeResume(resume.id)} title="Delete this resume">
            <Icon name="trash" size={16} />
            <span class="sr-only">Delete</span>
          </button>
        </div>
      {/each}
    </div>
  </section>

  {#if detail}
    <section class="block">
      <Rule text={detail.summary.label} note="{detail.versions.length} versions" />

      <div class="tabs" role="tablist" aria-label="Resume actions">
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

      {#if tab === 'Resume'}
        <div class="pane">
          <div class="pane-head">
            <label class="label" for="version">Working version</label>
            <div class="head-actions">
              <CopyButton text={versionText} small label="Copy text" />
              <button type="button" class="btn btn-sm" on:click={() => exportVersion('docx')}>Export .docx</button>
              <button type="button" class="btn btn-sm" on:click={() => exportVersion('pdf')}>Export .pdf</button>
            </div>
          </div>
          <select id="version" class="select" bind:value={selectedVersionId} on:change={loadVersionText}>
            {#each detail.versions as version (version.id)}
              <option value={version.id}>
                {version.label} &middot; {version.sourceKind} &middot; {version.charCount} chars{version.bestFit ? ' · best fit' : ''}
              </option>
            {/each}
          </select>

          <label class="label" for="text" style="margin-top: var(--s4)">Resume text</label>
          <textarea id="text" class="textarea editor" bind:value={versionText}></textarea>
          <p class="meta">This is the text every model workflow sees. Edit it freely; nothing is saved until you run an action.</p>

          {#if selectedVersion}
            <p class="version-meta">
              <span class="label">Source</span>
              {selectedVersion.sourceKind} &middot; created {new Date(selectedVersion.createdAt).toLocaleString()}
              {#if !selectedVersion.bestFit}
                <button type="button" class="btn btn-sm" on:click={() => selectedVersion && markBestFit(selectedVersion)}>Mark as best fit</button>
              {:else}
                <span class="badge badge-pass">Best fit</span>
              {/if}
            </p>
          {/if}
        </div>
      {:else if tab === 'Versions'}
        <div class="pane">
          <table class="data">
            <thead>
              <tr>
                <th>Label</th>
                <th>From</th>
                <th>Size</th>
                <th>Created</th>
                <th>Files</th>
                <th>Best fit</th>
              </tr>
            </thead>
            <tbody>
              {#each detail.versions as version (version.id)}
                <tr>
                  <td>
                    <button type="button" class="linkish" on:click={() => { selectedVersionId = version.id; loadVersionText(); tab = 'Resume'; }}>
                      {version.label}
                    </button>
                  </td>
                  <td class="mono">{version.sourceKind}</td>
                  <td class="mono">{version.charCount} chars</td>
                  <td class="mono">{new Date(version.createdAt).toLocaleDateString()}</td>
                  <td class="mono">{version.hasDocx ? 'docx' : ''} {version.hasPdf ? 'pdf' : ''}</td>
                  <td>
                    {#if version.bestFit}
                      <span class="badge badge-pass">Yes</span>
                    {:else}
                      <button type="button" class="btn btn-ghost btn-sm" on:click={() => markBestFit(version)}>Set</button>
                    {/if}
                  </td>
                </tr>
              {/each}
            </tbody>
          </table>
          <p class="meta" style="margin-top: var(--s3)">
            Each clean and each tailoring run saves a new version, so you can always go back.
          </p>
        </div>
      {:else if tab === 'Clean'}
        <div class="pane">
          <p>
            The deterministic pass runs first every time: unicode normalisation, bullet and icon removal,
            whitespace collapsing, date and number tidy-up. A model rewrite is optional and can only
            reword what is already there.
          </p>
          <button type="button" class="btn btn-primary" on:click={runClean} disabled={busy !== null}>
            {busy === 'clean' ? 'Cleaning…' : 'Clean this version'}
          </button>
          {#if busy}
            <p class="meta" role="status">{progress}</p>
          {/if}

          {#if clean}
            <div class="result">
              <Rule text="Changes applied" note={clean.usedModel ? `rewritten by ${clean.model}` : 'deterministic only'} />
              <table class="data">
                <thead>
                  <tr><th>Pass</th><th>What changed</th><th>Amount</th></tr>
                </thead>
                <tbody>
                  {#each clean.changes as change (change.kind + change.detail)}
                    <tr>
                      <td class="mono">{change.kind}</td>
                      <td>{change.detail}</td>
                      <td class="mono">{change.count}</td>
                    </tr>
                  {/each}
                </tbody>
              </table>

              {#if clean.warnings.length > 0}
                <div class="warnings">
                  <span class="label label-ink">Notes and warnings</span>
                  <ul>
                    {#each clean.warnings as warning, i (i)}
                      <li>{warning}</li>
                    {/each}
                  </ul>
                </div>
              {/if}

              <label class="label" for="cleaned" style="margin-top: var(--s4)">Cleaned text</label>
              <textarea id="cleaned" class="textarea editor" readonly value={clean.text}></textarea>
            </div>
          {:else}
            <Empty
              title="Nothing cleaned yet"
              message="Run the cleaner and the changes appear here as a list, so you can see exactly what was touched."
            />
          {/if}
        </div>
      {:else if tab === 'ATS scan'}
        <div class="pane">
          <label class="label" for="jd">Job description (optional, for keyword matching)</label>
          <textarea
            id="jd"
            class="textarea"
            bind:value={jobDescription}
            placeholder="Paste the posting. Keywords are extracted from it and compared against your resume."
          ></textarea>
          <button type="button" class="btn btn-primary" on:click={runAts} disabled={busy !== null}>
            {busy === 'ats' ? 'Scoring…' : 'Run ATS scan'}
          </button>
          {#if busy}<p class="meta" role="status">{progress}</p>{/if}

          {#if ats}
            <div class="result"><AtsReportView {ats} /></div>
          {:else}
            <Empty
              title="No scan yet"
              message="The rule-based scan scores eleven checks out of 100 and gives you the specific fix for every failure."
              action="Tip: add a job description to also get keyword coverage."
            />
          {/if}
        </div>
      {:else if tab === 'Tailor'}
        <div class="pane">
          <p>
            Tailoring uses only facts from this resume and your approved profile. If your instruction
            contains information that is not in either, the run stops and asks you to approve it first.
          </p>

          <label class="label" for="instr">Instruction</label>
          <textarea
            id="instr"
            class="textarea"
            bind:value={instruction}
            placeholder="e.g. Lead with the payments work and shorten the earliest role to two bullets."
          ></textarea>

          <button type="button" class="btn btn-primary" on:click={runUpdate} disabled={busy !== null}>
            {busy === 'update' ? 'Working…' : 'Tailor this resume'}
          </button>
          {#if busy}<p class="meta" role="status">{progress}</p>{/if}

          {#if update}
            <div class="result">
              {#if update.blockedByPending}
                <Notice
                  title="Approval needed before anything changes"
                  message="These facts are not in your resume or your verified profile, so nothing was written."
                  hint="Approve or reject each one in Profile. Then run the tailoring again."
                >
                  <ul class="pending-preview">
                    {#each update.pending as item (item.id)}
                      <li><span class="badge">{item.kind}</span> {item.text}</li>
                    {/each}
                  </ul>
                  <a class="btn btn-sm" href="/profile">Open Profile</a>
                </Notice>
              {:else}
                <Rule text="Tailored version" note={update.usedModel ? `written by ${update.model}` : 'no model was routed'} />
                <ul class="notes">
                  {#each update.notes as note, i (i)}
                    <li>{note}</li>
                  {/each}
                </ul>
                <textarea class="textarea editor" readonly value={update.text}></textarea>
              {/if}
            </div>
          {/if}
        </div>
      {/if}
    </section>
  {/if}
{/if}

<style>
  .intro {
    max-width: 60ch;
  }

  .lede {
    margin-top: var(--s4);
    font-size: 1.0625rem;
  }

  .block {
    margin-top: var(--s7);
  }

  .two {
    grid-template-columns: 1fr 1fr;
  }

  .cell {
    padding: var(--s5);
    display: flex;
    flex-direction: column;
    gap: var(--s3);
  }

  .row {
    display: flex;
    gap: var(--s3);
    align-items: flex-end;
  }

  .row .input {
    flex: 1;
  }

  .drop {
    border: 1px dashed var(--ink);
    padding: var(--s6) var(--s4);
    text-align: center;
    cursor: pointer;
    transition: background-color 200ms ease-out;
  }

  .drop:hover {
    background: var(--neutral-100);
  }

  .drop-title {
    font-family: var(--font-display);
    font-size: 1.25rem;
    margin: var(--s2) 0 0;
  }

  .note {
    font-family: var(--font-ui);
    font-size: 0.8125rem;
    color: var(--neutral-600);
    margin: 0;
  }

  .list {
    grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  }

  .row-card {
    display: flex;
    align-items: center;
    gap: var(--s2);
  }

  .row-card.selected {
    background: var(--neutral-100);
  }

  .pick {
    flex: 1;
    text-align: left;
    background: transparent;
    border: none;
    cursor: pointer;
    padding: var(--s3) 0;
    min-height: var(--tap);
    font: inherit;
    color: inherit;
  }

  .pick-title {
    display: block;
    font-size: 1.25rem;
    font-weight: 700;
  }

  .tabs {
    display: flex;
    flex-wrap: wrap;
    border-bottom: var(--hair);
    margin-bottom: var(--s5);
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
    transition: background-color 200ms ease-out, color 200ms ease-out;
  }

  .tab:hover {
    background: var(--neutral-100);
  }

  .tab.active {
    background: var(--ink);
    color: var(--paper);
  }

  .pane {
    max-width: 78ch;
  }

  .pane-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--s3);
    flex-wrap: wrap;
  }

  .head-actions {
    display: flex;
    gap: var(--s2);
    flex-wrap: wrap;
  }

  .editor {
    min-height: 420px;
    font-family: var(--font-mono);
    font-size: 0.8125rem;
  }

  .version-meta {
    display: flex;
    align-items: center;
    gap: var(--s3);
    flex-wrap: wrap;
    margin-top: var(--s4);
    font-family: var(--font-ui);
    font-size: 0.8125rem;
    color: var(--neutral-600);
  }

  .linkish {
    background: none;
    border: none;
    padding: 0;
    font: inherit;
    font-family: var(--font-display);
    font-size: 1rem;
    color: var(--ink);
    cursor: pointer;
    text-decoration: underline;
    text-decoration-color: var(--accent);
    text-underline-offset: 4px;
  }

  .result {
    margin-top: var(--s6);
  }

  .warnings {
    margin-top: var(--s4);
  }

  .warnings ul,
  .notes,
  .pending-preview {
    font-family: var(--font-ui);
    font-size: 0.875rem;
    padding-left: var(--s5);
    margin: var(--s2) 0 0;
  }

  .warnings li,
  .notes li,
  .pending-preview li {
    margin-bottom: var(--s2);
  }

  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
  }

  @media (max-width: 767px) {
    .two {
      grid-template-columns: 1fr;
    }

    .row {
      flex-direction: column;
      align-items: stretch;
    }

    .tabs {
      overflow-x: auto;
    }
  }
</style>