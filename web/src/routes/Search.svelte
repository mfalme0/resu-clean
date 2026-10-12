<script lang="ts">
  import { onMount } from 'svelte';
  import { api, waitForOp, type SearchAnswer, type SearchCapabilities } from '../lib/api';
  import Notice from '../lib/ui/Notice.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Rule from '../lib/ui/Rule.svelte';

  let query = '';
  let location = '';
  let busy = false;
  let error = '';
  let caps: SearchCapabilities | null = null;
  let answer: SearchAnswer | null = null;

  onMount(async () => {
    try {
      caps = await api.get<SearchCapabilities>('/search/capabilities');
    } catch {
      caps = null;
    }
  });

  /** Which backend actually answered, so the user is never guessing where facts came from. */
  const TIER_LABEL: Record<string, string> = {
    native: 'Answered by the model’s own search tool',
    'api+model': 'Search API results, summarised by the model',
    api: 'Raw search API results (no model)',
    none: 'No search backend available'
  };

  async function run() {
    if (!query.trim() || busy) return;

    busy = true;
    error = '';
    answer = null;
    try {
      const accepted = await api.post<{ opId: string }>('/search', {
        query: query.trim(),
        location: location.trim() || null,
        maxResults: 8
      });
      answer = await waitForOp<SearchAnswer>(accepted.opId, (s) => {
        if (s.state === 'running') return 'Searching…';
        return 'Waiting for the model…';
      });
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      busy = false;
    }
  }
</script>

<div class="screen">
  <header class="head">
    <h1>Search</h1>
    <p class="lede">
      Ask a question and get an answer with sources. Useful for company research, salary bands, and
      checking whether a posting you found is real.
    </p>
  </header>

  <Rule text="Ask" note="{caps?.canSearch ? 'native, API or both' : 'no backend configured'}" />

  <div class="fields">
    <label for="q">Question</label>
    <input
      id="q"
      type="search"
      bind:value={query}
      placeholder="e.g. what does a data analyst role at Kenya Airways pay?"
      autocomplete="off"
      disabled={busy}
      on:keydown={(e) => e.key === 'Enter' && run()}
    />

    <label for="loc">Location <span class="opt">optional</span></label>
    <input
      id="loc"
      type="text"
      bind:value={location}
      placeholder="Nairobi, Kenya"
      autocomplete="off"
      disabled={busy}
      on:keydown={(e) => e.key === 'Enter' && run()}
    />

    <button type="button" on:click={run} disabled={busy || !query.trim()}>
      {busy ? 'Searching…' : 'Search'}
    </button>
  </div>

  {#if caps && !caps.canSearch}
    <Notice
      tone="info"
      title="No search backend is set up yet"
      hint="Set TAVILY_API_KEY or BRAVE_SEARCH_API_KEY in .env for search results, or route the web_search workflow in Models to a model that has its own search tool."
    >
      <p>
        Everything else in resu-clean still works without either. Job search uses the source packs,
        which need neither.
      </p>
    </Notice>
  {/if}

  {#if error}
    <Notice title="The search failed" message={error} hint="Try again, or check whether your search key is still valid." />
  {/if}

  {#if answer}
    <section class="result">
      <p class="tier">
        <span class="label">Source</span>
        {TIER_LABEL[answer.tier] ?? answer.tier}
        {#if answer.provider}
          <span class="muted">via {answer.provider}/{answer.model}</span>
        {/if}
      </p>

      {#each answer.notes ?? [] as note}
        <p class="note">{note}</p>
      {/each}

      {#if answer.answer}
        <div class="answer">{answer.answer}</div>
      {/if}

      {#if answer.results?.length}
        <h2>Sources</h2>
        <ol class="results">
          {#each answer.results as hit}
            <li>
              <a href={hit.url} target="_blank" rel="noreferrer noopener">{hit.title}</a>
              <span class="host">{hit.source}</span>
              {#if hit.snippet}<p>{hit.snippet}</p>{/if}
            </li>
          {/each}
        </ol>
      {/if}

      <p class="disclaimer">
        Search results are quoted, never added to your profile. Nothing here is verified as a fact
        about you, and it cannot change your verified facts.
      </p>
    </section>
  {:else if !busy}
    <Empty
      title="Nothing searched yet"
      message="Ask a question above. Answers cite the sources they came from."
    />
  {/if}
</div>

<style>
  .screen { max-width: 46rem; }
  .lede { color: var(--text-muted); margin-bottom: 0; }
  .opt { color: var(--text-muted); font-weight: 400; }
  label { display: block; margin: 0.875rem 0 0.25rem; font-family: var(--font-ui); font-size: 0.875rem; }
  input { width: 100%; }
  button { margin-top: 1rem; }
  .tier { font-family: var(--font-ui); font-size: 0.875rem; }
  .muted { color: var(--text-muted); }
  .note { font-size: 0.875rem; color: var(--text-muted); }
  .answer { white-space: pre-wrap; margin: 1rem 0; }
  h2 { font-size: 1rem; margin-top: 1.5rem; }
  .results li { margin-bottom: 1rem; }
  .results p { margin: 0.25rem 0 0; font-size: 0.9375rem; color: var(--text-muted); }
  .host { margin-left: 0.5rem; font-family: var(--font-ui); font-size: 0.75rem; color: var(--text-muted); }
  .disclaimer {
    margin-top: 2rem;
    padding-top: 1rem;
    border-top: 1px solid var(--line);
    font-size: 0.8125rem;
    color: var(--text-muted);
  }
</style>