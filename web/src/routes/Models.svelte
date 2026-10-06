<script lang="ts">
  import { onMount } from 'svelte';
import { api, type Provider, type Route, type ProviderPreset, type DiscoverModelsResult } from '../lib/api';
  import { capabilities, flash, loadSystem, memory, refreshMemory } from '../lib/store';
  import Rule from '../lib/ui/Rule.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Notice from '../lib/ui/Notice.svelte';
  import Icon from '../lib/ui/Icon.svelte';

  const WORKFLOW_NOTES: Record<string, string> = {
    clean: 'The wording pass after the deterministic clean. Falls back to the deterministic result.',
    ats: 'Prioritised fixes from the rule scan. The score itself never needs a model.',
    profile: 'Extracting standalone facts from text so they can be queued for approval.',
    update_resume: 'Tailoring a resume to a posting, using only resume plus approved profile facts.',
    find_jobs: 'Optional: model-written search terms. Feed and API sources work without it.',
    apply_email: 'Writing the application email. Falls back to the template email.'
  };

  const WORKFLOWS = Object.keys(WORKFLOW_NOTES);

  let providers: Provider[] = [];
  let routes: Route[] = [];
  let presets: ProviderPreset[] = [];
  let busy = new Set<string>();
  let testMessage: Record<string, string> = {};
  let refreshing = false;

  // Provider editor
  let editing: (Partial<Provider> & { apiKey?: string; presetId?: string }) | null = null;
  let modelsText = '';

  // Model discovery
  let discovered: DiscoverModelsResult | null = null;
  let discovering = false;
  let pickedModels: string[] = [];
  let modelFilter = '';

  // Route editor: workflow -> ordered list of chosen models. Structured rather than free text, so
  // the provider and model always come from real dropdowns and cannot be typed wrong.
  type RouteEntryDraft = { providerId: string; model: string };
  let routeDraft: Record<string, RouteEntryDraft[]> = {};
  // Test results are keyed per entry, not per workflow: "live test" now targets one chosen model.
  let routeTest: Record<string, string> = {};
  let routeBusy: Record<string, boolean> = {};

  onMount(load);

  /** Re-reads providers, routes and presets. Called after anything that changes them. */
  async function load() {
    try {
      [providers, routes, presets] = await Promise.all([
        api.get<Provider[]>('/providers'),
        api.get<Route[]>('/routes'),
        api.get<ProviderPreset[]>('/providers/presets')
      ]);
      const draft: Record<string, RouteEntryDraft[]> = {};
      for (const route of routes) {
        draft[route.workflow] = route.entries.map((e) => ({ providerId: e.providerId, model: e.model }));
      }
      // Every workflow always gets a list, even when no route row exists yet, so the picker shows.
      for (const workflow of WORKFLOWS) {
        if (!draft[workflow]) draft[workflow] = [];
      }
      routeDraft = draft;
      routeTest = {};
      await refreshMemory();
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  /** Explicit refresh, for when keys or models were changed outside this page. */
  async function refreshAll() {
    refreshing = true;
    await load();
    await loadSystem();
    refreshing = false;
    flash('Refreshed. Providers and models are up to date.');
  }

  function modelsFor(providerId: string): string[] {
    return providers.find((p) => p.id === providerId)?.models ?? [];
  }

  function setEntry(workflow: string, index: number, patch: Partial<RouteEntryDraft>) {
    const list = [...(routeDraft[workflow] ?? [])];
    const current = list[index];
    if (!current) return;
    list[index] = { ...current, ...patch };
    routeDraft = { ...routeDraft, [workflow]: list };
  }

  /** Changing provider pre-selects that provider's first model, since the old one will not fit. */
  function setProvider(workflow: string, index: number, providerId: string) {
    const available = modelsFor(providerId);
    setEntry(workflow, index, { providerId, model: available[0] ?? '' });
  }

  function addEntry(workflow: string) {
    const firstWithModels = providers.find((p) => p.models.length > 0) ?? providers[0];
    const list = [...(routeDraft[workflow] ?? [])];
    list.push({ providerId: firstWithModels?.id ?? '', model: firstWithModels?.models[0] ?? '' });
    routeDraft = { ...routeDraft, [workflow]: list };
  }

  function removeEntry(workflow: string, index: number) {
    const list = [...(routeDraft[workflow] ?? [])];
    list.splice(index, 1);
    routeDraft = { ...routeDraft, [workflow]: list };
  }

  /** Fallbacks are tried in order, so moving a row changes real behaviour. */
  function moveEntry(workflow: string, index: number, delta: number) {
    const list = [...(routeDraft[workflow] ?? [])];
    const target = index + delta;
    if (target < 0 || target >= list.length) return;
    const [row] = list.splice(index, 1);
    list.splice(target, 0, row);
    routeDraft = { ...routeDraft, [workflow]: list };
  }

  function startNew() {
    editing = { name: '', type: 'openai', baseUrl: '', envVar: '', models: [], enabled: true };
    modelsText = '';
    discovered = null;
    pickedModels = [];
    modelFilter = '';
  }

  function startEdit(provider: Provider) {
    editing = { ...provider, apiKey: '' };
    modelsText = provider.models.join('\n');
    discovered = null;
    pickedModels = [];
    modelFilter = '';
  }

  /** Selecting a preset fills in the type, base URL and env var. Nothing is saved yet. */
  function applyPreset(presetId: string) {
    const preset = presets.find((p) => p.id === presetId);
    if (!preset || !editing) return;
    editing = {
      ...editing,
      presetId: preset.id,
      name: editing.name || preset.name,
      type: preset.type,
      baseUrl: preset.baseUrl,
      envVar: editing.envVar || preset.envVar
    };
    modelsText = editing.models && editing.models.length > 0 ? editing.models.join('\n') : preset.suggestedModels.join('\n');
    discovered = null;
    pickedModels = [];
  }

  async function fetchModels() {
    if (!editing) return;
    discovering = true;
    try {
      discovered = await api.post<DiscoverModelsResult>('/providers/discover', {
        type: editing.type,
        baseUrl: editing.baseUrl,
        apiKey: editing.apiKey || null,
        envVar: editing.envVar || null
      });
      if (discovered.ok) {
        // Pre-select what is already configured so saving does not silently drop models.
        const current = modelsText.split('\n').map((m) => m.trim()).filter(Boolean);
        pickedModels = discovered.models.filter((m) => current.includes(m));
        if (pickedModels.length === 0 && discovered.models.length > 0) pickedModels = discovered.models.slice(0, 3);
        modelFilter = '';
      }
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      discovering = false;
    }
  }

  async function refreshModels(provider: Provider) {
    busy = new Set([...busy, provider.id]);
    try {
      const result = await api.post<DiscoverModelsResult>(`/providers/${provider.id}/models`);
      if (result.ok && result.models.length > 0) {
        await api.put(`/providers/${provider.id}`, { id: provider.id, models: result.models });
        await load();
        flash(`Fetched ${result.models.length} models for ${provider.name}.`);
      } else {
        flash(result.error ?? 'No models returned.', 'error');
      }
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      const next = new Set(busy);
      next.delete(provider.id);
      busy = next;
    }
  }

  function togglePicked(model: string) {
    pickedModels = pickedModels.includes(model)
      ? pickedModels.filter((m) => m !== model)
      : [...pickedModels, model];
  }

  function applyPickedToText() {
    modelsText = [...new Set([...pickedModels, ...modelsText.split('\n').map((m) => m.trim()).filter(Boolean)])].join('\n');
    flash(`${pickedModels.length} models added to the list.`);
  }

  $: visibleDiscovered = (discovered?.models ?? []).filter((m) =>
    modelFilter.trim().length === 0 || m.toLowerCase().includes(modelFilter.trim().toLowerCase())
  );
  $: activePreset = presets.find((p) => p.id === editing?.presetId) ?? null;

  async function saveProvider() {
    if (!editing?.name) {
      flash('Give the provider a name.', 'error');
      return;
    }
    const payload = {
      id: editing.id,
      presetId: editing.presetId ?? null,
      name: editing.name,
      type: editing.type,
      baseUrl: editing.baseUrl || null,
      envVar: editing.envVar || null,
      apiKey: editing.apiKey || null,
      models: modelsText.split('\n').map((m) => m.trim()).filter(Boolean),
      enabled: editing.enabled
    };
    try {
      await api.post('/providers', payload);
      const savedName = editing.name;
      editing = null;
      await load();
      await loadSystem();
      flash(`Provider "${savedName}" saved.`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function removeProvider(provider: Provider) {
    if (!confirm(`Remove "${provider.name}"? Routes that use it will be skipped.`)) return;
    try {
      await api.del(`/providers/${provider.id}`);
      await load();
      await loadSystem();
      flash('Provider removed.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function test(provider: Provider) {
    busy = new Set([...busy, provider.id]);
    testMessage = { ...testMessage };
    try {
      const result = await api.post<{ ok: boolean; message: string }>(`/providers/${provider.id}/test`, { model: provider.models[0] ?? null });
      testMessage[provider.id] = result.message;
    } catch (err) {
      testMessage[provider.id] = err instanceof Error ? err.message : String(err);
    } finally {
      const next = new Set(busy);
      next.delete(provider.id);
      busy = next;
    }
  }

  async function saveRoute(workflow: string) {
    const entries = (routeDraft[workflow] ?? []).filter((e) => e.providerId && e.model);
    const skipped = (routeDraft[workflow] ?? []).length - entries.length;

    try {
      await api.put(`/routes/${workflow}`, { entries });
      await load();
      await loadSystem();
      flash(
        `Route for "${workflow}" saved: ${entries.length} model${entries.length === 1 ? '' : 's'} in order.` +
          (skipped > 0 ? ` ${skipped} incomplete row${skipped === 1 ? '' : 's'} skipped.` : '')
      );
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  /** Live-tests one chosen provider+model pair, saved or not. */
  async function previewEntry(workflow: string, index: number) {
    const key = `${workflow}:${index}`;
    const entry = routeDraft[workflow]?.[index];
    if (!entry?.providerId || !entry?.model) {
      routeTest = { ...routeTest, [key]: 'Choose both a provider and a model first.' };
      return;
    }
    routeBusy = { ...routeBusy, [key]: true };
    try {
      const result = await api.post<{ ok: boolean; message: string }>(`/routes/${workflow}/preview`, {
        entries: [{ providerId: entry.providerId, model: entry.model }]
      });
      routeTest = { ...routeTest, [key]: result.message };
    } catch (err) {
      routeTest = { ...routeTest, [key]: err instanceof Error ? err.message : String(err) };
    } finally {
      routeBusy = { ...routeBusy, [key]: false };
    }
  }

  $: openAiDefaults = 'https://api.openai.com/v1';

  function selectValue(event: Event): string {
    return (event.currentTarget as HTMLSelectElement).value;
  }

  function inputValue(event: Event): string {
    return (event.currentTarget as HTMLInputElement).value;
  }
</script>

<svelte:head><title>Models &amp; Routes &middot; resu-clean</title></svelte:head>

<section class="intro">
  <span class="label">Section six</span>
  <h1>Your models,<br />your rules.</h1>
  <p class="justify lede dropcap">
    Bring your own keys, point each workflow at whichever model you want, and list fallbacks in order.
    Keys are encrypted at rest and never come back out through the API. Everything below works without
    any of it configured.
  </p>
</section>

<div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

<section class="block">
  <div class="label-row">
    <span class="label">Providers</span>
    <div class="actions">
      <button type="button" class="btn btn-sm" on:click={refreshAll} disabled={refreshing}>
        {refreshing ? 'Refreshing…' : 'Refresh'}
      </button>
      <button type="button" class="btn btn-primary btn-sm" on:click={startNew}>Add a provider</button>
    </div>
  </div>

  {#if providers.length === 0}
    <Empty
      title="No providers configured"
      message="The tool is fully functional without one: deterministic clean, ATS scoring, job search and template kits all work. A model only adds rewording, prioritised fixes, tailoring and written emails."
      action="Next: add a provider, or set an API key in .env and point a provider at the environment variable."
    >
      <button type="button" class="btn btn-primary" on:click={startNew}>Add a provider</button>
    </Empty>
  {:else}
    <table class="data">
      <thead>
        <tr>
          <th>Name</th>
          <th>Type</th>
          <th>Base URL</th>
          <th>Models</th>
          <th>Key</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        {#each providers as provider (provider.id)}
          <tr>
            <td>
              <span class="job-title">{provider.name}</span>
              {#if provider.envVar}<span class="meta company mono">{provider.envVar}</span>{/if}
            </td>
            <td><span class="badge">{provider.type}</span></td>
            <td class="mono url">{provider.baseUrl}</td>
            <td class="mono models">{provider.models.join(', ') || '—'}</td>
            <td>
              {#if provider.hasKey}
                <span class="badge badge-pass" title={provider.keySource}>{provider.keySource === 'stored' ? 'stored' : 'env'}</span>
              {:else}
                <span class="badge badge-accent">no key</span>
              {/if}
            </td>
            <td class="actions">
              <button type="button" class="btn btn-sm" on:click={() => refreshModels(provider)} disabled={busy.has(provider.id)}>
                {busy.has(provider.id) ? 'Fetching…' : 'Fetch models'}
              </button>
              <button type="button" class="btn btn-sm" on:click={() => test(provider)} disabled={busy.has(provider.id)}>
                Test
              </button>
              <button type="button" class="btn btn-sm" on:click={() => startEdit(provider)}>Edit</button>
              <button type="button" class="btn btn-ghost btn-sm" on:click={() => removeProvider(provider)}>Remove</button>
            </td>
          </tr>
          {#if testMessage[provider.id]}
            <tr class="msg-row">
              <td colspan="6"><span class="meta">{testMessage[provider.id]}</span></td>
            </tr>
          {/if}
        {/each}
      </tbody>
    </table>

    <button type="button" class="btn" style="margin-top: var(--s4)" on:click={startNew}>Add a provider</button>
  {/if}

  <p class="meta" style="margin-top: var(--s3)">
    {providers.length} configured. After you add a key or fetch models, press Refresh (or just save the
    provider) and the route dropdowns below pick up the new models.
  </p>
</section>

{#if editing}
  <section class="block">
    <Rule text={editing.id ? 'Edit provider' : 'New provider'} note="pick a preset or fill it in yourself" />

    <div class="editor">
      <div class="field">
        <label class="label" for="preset">Start from a preset</label>
        <select id="preset" class="select" value={editing.presetId ?? ''} on:change={(e) => applyPreset(selectValue(e))}>
          <option value="">Custom (fill in below)</option>
          {#each presets as preset (preset.id)}
            <option value={preset.id}>{preset.name}{preset.keyless ? ' — no key needed' : ''}</option>
          {/each}
        </select>
        {#if activePreset}
          <p class="meta preset-note">
            {activePreset.notes}
            {#if activePreset.docsUrl}
              <a class="linkish" href={activePreset.docsUrl} target="_blank" rel="noopener noreferrer">Where do I get a key?</a>
            {/if}
          </p>
        {/if}
      </div>

      <div class="form-grid">
        <div class="field">
          <label class="label" for="pname">Name</label>
          <input id="pname" class="input" bind:value={editing.name} placeholder="OpenRouter" />
        </div>
        <div class="field">
          <label class="label" for="ptype">Type</label>
          <select id="ptype" class="select" bind:value={editing.type}>
            <option value="openai">openai-compatible</option>
            <option value="anthropic">anthropic</option>
          </select>
        </div>
        <div class="field">
          <label class="label" for="purl">Base URL</label>
          <input id="purl" class="input" bind:value={editing.baseUrl} placeholder={openAiDefaults} />
        </div>
        <div class="field">
          <label class="label" for="penv">Env var for the key</label>
          <input id="penv" class="input" bind:value={editing.envVar} placeholder="OPENROUTER_API_KEY" />
        </div>
      </div>

      <div class="field">
        <label class="label" for="pkey">API key {editing.id ? '(leave blank to keep the stored one)' : ''}</label>
        <input id="pkey" class="input" type="password" bind:value={editing.apiKey} autocomplete="off" placeholder={activePreset?.keyHint ?? 'sk-...'} />
        <p class="meta">
          Encrypted at rest (DPAPI on Windows, AES-256 with a local key file elsewhere). No endpoint ever
          returns it. Prefer an env var if you would rather not store it at all.
        </p>
      </div>

      <div class="field">
        <div class="label-row">
          <span class="label">Models, one per line</span>
          <button type="button" class="btn btn-sm" on:click={fetchModels} disabled={discovering}>
            {discovering ? 'Fetching…' : 'Fetch models from the provider'}
          </button>
        </div>

        {#if discovered}
          {#if discovered.ok}
            <div class="discovered">
              <div class="label-row">
                <span class="label">{discovered.models.length} models available</span>
                <input class="input filter" bind:value={modelFilter} placeholder="Filter" aria-label="Filter models" />
              </div>
              <p class="meta">From {discovered.source} in {discovered.elapsedMs} ms. Tick the ones you want, then add them.</p>
              <ul class="model-list">
                {#each visibleDiscovered as model (model)}
                  <li>
                    <label class="model-pick">
                      <input type="checkbox" checked={pickedModels.includes(model)} on:change={() => togglePicked(model)} />
                      <span class="mono">{model}</span>
                    </label>
                  </li>
                {/each}
              </ul>
              {#if visibleDiscovered.length === 0}
                <p class="meta">No model matches that filter.</p>
              {/if}
              <button type="button" class="btn btn-sm" on:click={applyPickedToText} disabled={pickedModels.length === 0}>
                Add {pickedModels.length} ticked to the list
              </button>
            </div>
          {:else}
            <Notice
              tone="info"
              title="Could not fetch the model list"
              message={discovered.error ?? 'Unknown error.'}
              hint={discovered.hint ?? 'Type the model ids by hand, or check the base URL. Saving still works.'}
            />
          {/if}
        {/if}

        <textarea id="pmodels" class="textarea mono-input" bind:value={modelsText} placeholder={'gpt-4o-mini\nclaude-sonnet-4-5'}></textarea>
        <p class="meta">
          Leave this empty when saving and resu-clean tries once to fetch the list for you. Non-chat models
          (embeddings, images, audio) are filtered out, and Gemini's <span class="mono">models/</span> prefix is stripped.
        </p>
      </div>

      <div class="editor-actions">
        <button type="button" class="btn btn-primary" on:click={saveProvider}>Save provider</button>
        <button type="button" class="btn" on:click={() => (editing = null)}>Cancel</button>
      </div>
    </div>
  </section>
{/if}

<div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

<section class="block inverted newsprint-texture">
  <div class="inner">
    <Rule text="How routing works" />

    <div class="steps">
      {#each WORKFLOWS as workflow, index (workflow)}
        <div class="step">
          <span class="step-number">{String(index + 1).padStart(2, '0')}</span>
          <h3 class="step-title">{workflow}</h3>
          <p class="step-body">{WORKFLOW_NOTES[workflow]}</p>
          <p class="step-meta">
            {(routeDraft[workflow] ?? []).length} model{(routeDraft[workflow] ?? []).length === 1 ? '' : 's'} chosen &middot;
            active: {routes.find((r) => r.workflow === workflow)?.activeModel ?? 'none'}
          </p>
        </div>
      {/each}
    </div>

    <p class="inverted-hint">
      A call tries the first entry. If the key is wrong, the model name is wrong or the provider errors,
      the router moves to the next entry and says so. With no working entry, the workflow returns its
      deterministic result rather than failing.
    </p>
  </div>
</section>

<section class="block">
  <Rule text="Routes" note="choose the models, in the order they should be tried" />

  {#if providers.length === 0}
    <Notice
      tone="info"
      title="No providers yet, so there is nothing to route to"
      message="Add a provider and fetch its models above. Each workflow then gets a dropdown of those models."
      hint="Every workflow still works without a model, so this is optional."
    />
  {:else}
    <div class="grid-rule route-grid">
      {#each WORKFLOWS as workflow (workflow)}
        {@const entries = routeDraft[workflow] ?? []}
        {@const saved = routes.find((r) => r.workflow === workflow)}
        <div class="cell route-cell">
          <div class="route-head">
            <h3 class="card-title">{workflow}</h3>
            {#if saved?.activeModel}
              <span class="badge badge-pass">via {saved.activeModel}</span>
            {:else}
              <span class="badge badge-accent">no active model</span>
            {/if}
          </div>

          <p class="meta route-note">{WORKFLOW_NOTES[workflow]}</p>

          {#if entries.length === 0}
            <p class="meta">No models chosen. This workflow uses its deterministic result.</p>
          {:else}
            <ol class="route-entries">
              {#each entries as entry, index (index)}
                {@const key = `${workflow}:${index}`}
                <li class="route-entry">
                  <div class="route-line">
                    <span class="route-pos" title="Fallback order">{index + 1}</span>

                    <div class="field route-field">
                      <label class="sr-only" for={`${key}-provider`}>Provider for {workflow}, position {index + 1}</label>
                      <select
                        id={`${key}-provider`}
                        class="select"
                        value={entry.providerId}
                        on:change={(e) => setProvider(workflow, index, selectValue(e))}
                      >
                        <option value="">Choose a provider…</option>
                        {#each providers as p (p.id)}
                          <option value={p.id}>{p.name}{p.hasKey ? '' : ' (no key)'}</option>
                        {/each}
                      </select>
                    </div>

                    <div class="field route-field">
                      <label class="sr-only" for={`${key}-model`}>Model for {workflow}, position {index + 1}</label>
                      {#if modelsFor(entry.providerId).length > 0}
                        <select
                          id={`${key}-model`}
                          class="select mono-select"
                          value={entry.model}
                          on:change={(e) => setEntry(workflow, index, { model: selectValue(e) })}
                        >
                          <option value="">Choose a model…</option>
                          {#each modelsFor(entry.providerId) as model (model)}
                            <option value={model}>{model}</option>
                          {/each}
                        </select>
                      {:else}
                        <input
                          id={`${key}-model`}
                          class="input mono-select"
                          value={entry.model}
                          on:input={(e) => setEntry(workflow, index, { model: inputValue(e) })}
                          placeholder="No models listed. Fetch them above, or type the id."
                        />
                      {/if}
                    </div>

                    <div class="route-tools">
                      <button
                        type="button"
                        class="btn btn-inverted btn-sm"
                        on:click={() => previewEntry(workflow, index)}
                        disabled={routeBusy[key]}
                        title="Make one real call to this provider and model"
                      >
                        {routeBusy[key] ? 'Testing…' : 'Test'}
                      </button>
                      <button
                        type="button"
                        class="btn btn-inverted btn-sm"
                        on:click={() => moveEntry(workflow, index, -1)}
                        disabled={index === 0}
                        aria-label={`Move ${entry.model || 'model'} up`}
                      >↑</button>
                      <button
                        type="button"
                        class="btn btn-inverted btn-sm"
                        on:click={() => moveEntry(workflow, index, 1)}
                        disabled={index === entries.length - 1}
                        aria-label={`Move ${entry.model || 'model'} down`}
                      >↓</button>
                      <button
                        type="button"
                        class="btn btn-inverted btn-sm"
                        on:click={() => removeEntry(workflow, index)}
                        aria-label={`Remove ${entry.model || 'model'}`}
                      >×</button>
                    </div>
                  </div>

                  {#if routeTest[key]}
                    <p class="meta route-result">{routeTest[key]}</p>
                  {/if}
                </li>
              {/each}
            </ol>
          {/if}

          <div class="route-actions">
            <button type="button" class="btn btn-sm" on:click={() => addEntry(workflow)}>
              + Add model
            </button>
            <button type="button" class="btn btn-primary btn-sm" on:click={() => saveRoute(workflow)}>
              Save {workflow.replace('_', ' ')}
            </button>
          </div>
        </div>
      {/each}
    </div>

    <p class="meta" style="margin-top: var(--s4)">
      Add as many rows as you like. The first one is tried first; if it fails the router falls through to the
      next and tells you why. Save each workflow after changing it.
    </p>
  {/if}
</section>

<section class="block">
  <Rule text="This machine" />

  <div class="grid-rule stats">
    <div class="cell">
      <span class="label">Backend resident memory</span>
      <div class="stat-figure">{$memory?.rssMb ?? '—'}<span class="stat-unit">MB</span></div>
      <p class="meta">Target: under 60 MB idle</p>
    </div>
    <div class="cell">
      <span class="label">Managed heap</span>
      <div class="stat-figure">{$memory?.managedHeapMb ?? '—'}<span class="stat-unit">MB</span></div>
      <p class="meta">Workstation GC, no server heap</p>
    </div>
    <div class="cell">
      <span class="label">Peak resident</span>
      <div class="stat-figure">{$memory?.peakRssMb ?? '—'}<span class="stat-unit">MB</span></div>
      <p class="meta">Since process start</p>
    </div>
    <div class="cell">
      <span class="label">Operations running</span>
      <div class="stat-figure">{$memory?.opsActive ?? 0}</div>
      <p class="meta">{$memory?.threads ?? 0} threads</p>
    </div>
  </div>

  <p class="meta" style="margin-top: var(--s3)">
    Live figures come from <span class="mono">GET /api/metrics/mem</span>. The same numbers are printed by
    <span class="mono">npm run mem</span>.
  </p>
</section>

<section class="block">
  <Rule text="Security note" />
  <Notice
    tone="info"
    title="Where your keys live"
    message="Provider keys are encrypted before they touch the database and are never returned by any endpoint, including the ones that list providers. Prefer an environment variable in .env when you can: the key then never touches the database at all."
    hint="The server binds to 127.0.0.1 by default and is open. Set RESUCLEAN_API_KEY before exposing the port to anything."
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

  .job-title {
    display: block;
    font-family: var(--font-ui);
    font-size: 0.9375rem;
    font-weight: 500;
  }

  .company {
    display: block;
    margin-top: 2px;
  }

  .url {
    font-size: 0.75rem;
    word-break: break-all;
    max-width: 26ch;
  }

  .models {
    max-width: 20ch;
  }

  .actions {
    display: flex;
    gap: var(--s2);
    flex-wrap: wrap;
  }

  .msg-row td {
    padding-top: 0;
    border-bottom: var(--rule-heavy);
  }

  .editor {
    border: var(--hair);
    border-left: var(--rule-heavy);
    padding: var(--s5);
    max-width: 74ch;
    display: grid;
    gap: var(--s4);
  }

  .form-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    gap: var(--s4);
  }

  .mono-input {
    font-family: var(--font-mono);
    font-size: 0.8125rem;
    min-height: 120px;
  }

  .label-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--s3);
    flex-wrap: wrap;
  }

  .filter {
    max-width: 200px;
    min-height: 36px;
  }

  .preset-note {
    margin: var(--s2) 0 0;
    max-width: 72ch;
  }

  .linkish {
    font-family: var(--font-ui);
    font-size: 0.8125rem;
    color: var(--ink);
    text-decoration: underline;
    text-decoration-color: var(--accent);
    text-underline-offset: 4px;
  }

  /* Discovered model picker: a dense, scrollable column of tick boxes. */
  .discovered {
    border: var(--hair);
    border-left: var(--rule-heavy);
    padding: var(--s4);
    margin: var(--s2) 0;
  }

  .model-list {
    list-style: none;
    padding: 0;
    margin: var(--s3) 0;
    max-height: 260px;
    overflow-y: auto;
    border-top: var(--hair-soft);
  }

  .model-list li {
    border-bottom: var(--hair-soft);
  }

  .model-pick {
    display: flex;
    align-items: center;
    gap: var(--s3);
    padding: var(--s2) 0;
    min-height: 36px;
    cursor: pointer;
    font-size: 0.8125rem;
  }

  .model-pick input {
    width: 16px;
    height: 16px;
    accent-color: var(--ink);
    flex-shrink: 0;
  }

  .model-pick span {
    word-break: break-all;
  }

  .editor-actions,
  .route-actions {
    display: flex;
    gap: var(--s3);
    flex-wrap: wrap;
  }

  /* Inverted section: numbered steps in editorial red. */
  .inner {
    padding: var(--s6) 0;
  }

  .steps {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
    border: 1px solid var(--paper);
  }

  .step {
    padding: var(--s5);
    border-right: 1px solid rgba(255, 255, 255, 0.2);
  }

  .step-number {
    font-family: var(--font-mono);
    font-size: 1.75rem;
    font-weight: 700;
    color: var(--accent);
    display: block;
    margin-bottom: var(--s2);
  }

  .step-title {
    font-size: 1.125rem;
    margin-bottom: var(--s2);
  }

  .step-body {
    font-family: var(--font-ui);
    font-size: 0.875rem;
    color: var(--neutral-500);
    margin-bottom: var(--s2);
  }

  .step-meta {
    font-family: var(--font-mono);
    font-size: 0.6875rem;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    color: var(--neutral-500);
    margin: 0;
  }

  .inverted-hint {
    font-family: var(--font-ui);
    font-size: 0.875rem;
    color: var(--neutral-500);
    max-width: 70ch;
    margin-top: var(--s5);
  }

  .route-grid {
    grid-template-columns: repeat(auto-fit, minmax(360px, 1fr));
  }

  .route-cell {
    padding: var(--s5);
  }

  .route-note {
    margin: 0 0 var(--s4);
  }

  /* Ordered list of chosen models: number, provider, model, then the row tools. */
  .route-entries {
    list-style: none;
    padding: 0;
    margin: 0 0 var(--s4);
    display: grid;
    gap: var(--s3);
  }

  .route-entry {
    border: var(--hair);
    border-left: var(--rule-heavy);
    padding: var(--s3);
  }

  .route-line {
    display: grid;
    grid-template-columns: 24px 1fr;
    gap: var(--s2) var(--s3);
    align-items: center;
  }

  .route-pos {
    font-family: var(--font-mono);
    font-size: 0.9375rem;
    font-weight: 700;
    color: var(--accent);
    text-align: center;
  }

  .route-field {
    min-width: 0;
  }

  .mono-select {
    font-family: var(--font-mono);
    font-size: 0.75rem;
  }

  .route-tools {
    grid-column: 2;
    display: flex;
    gap: var(--s2);
    flex-wrap: wrap;
  }

  .route-tools .btn {
    padding: 4px 8px;
    min-width: 32px;
  }

  .route-result {
    margin: var(--s3) 0 0;
    word-break: break-word;
  }

  /* Visible to screen readers only, so each dropdown still has a real label. */
  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    padding: 0;
    margin: -1px;
    overflow: hidden;
    clip: rect(0, 0, 0, 0);
    white-space: nowrap;
    border: 0;
  }

  .route-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: var(--s3);
    margin-bottom: var(--s3);
    flex-wrap: wrap;
  }

  .route-head .card-title {
    margin: 0;
    font-family: var(--font-mono);
    font-size: 0.9375rem;
    letter-spacing: 0.08em;
    text-transform: uppercase;
  }

  .btn-inverted {
    background: var(--paper);
    color: var(--ink);
    border-color: var(--paper);
  }

  .btn-inverted:hover:not(:disabled) {
    background: transparent;
    color: var(--paper);
  }

  .stats {
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  }

  .cell {
    padding: var(--s5);
  }

  .stat-figure {
    font-family: var(--font-display);
    font-size: 2.5rem;
    font-weight: 900;
    line-height: 1;
    margin: var(--s2) 0;
  }

  .stat-unit {
    font-size: 1rem;
    color: var(--neutral-500);
    margin-left: 4px;
  }

  @media (max-width: 767px) {
    .step {
      border-right: none;
      border-bottom: 1px solid rgba(255, 255, 255, 0.2);
    }
  }
</style>