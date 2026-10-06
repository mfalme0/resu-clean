<script lang="ts">
  import { onMount } from 'svelte';
  import { api, type Profile, type ProfileFact, type PendingFact } from '../lib/api';
  import { flash, refreshPendingCount } from '../lib/store';
  import Rule from '../lib/ui/Rule.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Notice from '../lib/ui/Notice.svelte';

  const FACT_KINDS = ['role', 'skill', 'achievement', 'education', 'cert', 'contact', 'other'];

  let profile: Profile | null = null;
  let facts: ProfileFact[] = [];
  let pending: PendingFact[] = [];

  let voice = '';
  let preferences = '';
  let contact: { name: string; email: string; phone: string; location: string; links: string } = {
    name: '',
    email: '',
    phone: '',
    location: '',
    links: ''
  };

  let newKind = 'achievement';
  let newText = '';
  let saving = false;
  let pendingBusy = new Set<string>();

  onMount(load);

  async function load() {
    try {
      [profile, facts, pending] = await Promise.all([
        api.get<Profile>('/profile'),
        api.get<ProfileFact[]>('/profile/facts'),
        api.get<PendingFact[]>('/profile/pending')
      ]);
      voice = profile.voice;
      preferences = profile.preferences;
      contact = {
        name: profile.contact.name ?? '',
        email: profile.contact.email ?? '',
        phone: profile.contact.phone ?? '',
        location: profile.contact.location ?? '',
        links: profile.contact.links ?? ''
      };
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function saveProfile() {
    saving = true;
    try {
      const contactMap: Record<string, string> = {};
      for (const [key, value] of Object.entries(contact)) {
        if (value.trim()) contactMap[key] = value.trim();
      }
      profile = await api.put<Profile>('/profile', {
        voice,
        preferences,
        contact: contactMap
      });
      flash('Voice and contact details saved.');
      await load();
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      saving = false;
    }
  }

  async function addFact() {
    if (newText.trim().length < 4) {
      flash('Write the fact first.', 'error');
      return;
    }
    try {
      const result = await api.post<{ added: boolean; duplicate: boolean; message: string }>('/profile/facts', {
        kind: newKind,
        text: newText.trim()
      });
      newText = '';
      await load();
      await refreshPendingCount();
      flash(result.message, result.duplicate ? 'info' : 'info');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function approve(item: PendingFact) {
    pendingBusy = new Set([...pendingBusy, item.id]);
    try {
      await api.post(`/profile/pending/${item.id}/approve`);
      await load();
      await refreshPendingCount();
      flash(`Approved: ${item.text}`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      const next = new Set(pendingBusy);
      next.delete(item.id);
      pendingBusy = next;
    }
  }

  async function reject(item: PendingFact) {
    pendingBusy = new Set([...pendingBusy, item.id]);
    try {
      await api.post(`/profile/pending/${item.id}/reject`, { approve: false });
      await load();
      await refreshPendingCount();
      flash('Rejected. It will not be added to your profile.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      const next = new Set(pendingBusy);
      next.delete(item.id);
      pendingBusy = next;
    }
  }

  async function removeFact(fact: ProfileFact) {
    if (!confirm(`Remove this verified fact?\n\n${fact.text}`)) return;
    try {
      await api.del(`/profile/facts/${fact.id}`);
      await load();
      flash('Fact removed.');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  async function approveAll() {
    try {
      const result = await api.post<{ approved: number }>('/profile/pending/approve-all', {});
      await load();
      await refreshPendingCount();
      flash(`Approved ${result.approved} items.`);
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  $: grouped = FACT_KINDS.map((kind) => ({ kind, items: facts.filter((f) => f.kind === kind) })).filter(
    (group) => group.items.length > 0
  );
  $: orphanFacts = facts.filter((f) => !FACT_KINDS.includes(f.kind));
</script>

<svelte:head><title>Profile &middot; resu-clean</title></svelte:head>

<section class="intro">
  <span class="label">Section two</span>
  <h1>The only<br />truth.</h1>
  <p class="justify lede dropcap">
    Your character profile is the source of truth. Every model workflow reads it, and nothing enters it
    silently: new information is queued for approval, exactly matched so you are never asked about the
    same thing twice.
  </p>
</section>

<div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

{#if pending.length > 0}
  <section class="block">
    <Rule text="Waiting for your approval" note="{pending.length} item{pending.length === 1 ? '' : 's'}" />

    <Notice
      title="Nothing here is saved yet"
      message="These facts came from an edit you made or from an instruction you gave. Only the ones you approve join your profile."
      hint="Approve a fact if it is true and you can defend it. Reject anything you cannot."
    />

    <div class="pending-list">
      {#each pending as item (item.id)}
        <div class="pending">
          <div class="pending-body">
            <div class="pending-head">
              <span class="badge">{item.kind}</span>
              <span class="meta">from {item.origin}</span>
            </div>
            <p class="pending-text">{item.text}</p>
            {#if item.meta.fuzzy_similar_to}
              <p class="meta">Similar to something already waiting: "{item.meta.fuzzy_similar_to}"</p>
            {/if}
            {#if item.context}
              <details>
                <summary class="meta">See the instruction it came from</summary>
                <pre class="context">{item.context}</pre>
              </details>
            {/if}
          </div>
          <div class="pending-actions">
            <button type="button" class="btn btn-primary btn-sm" on:click={() => approve(item)} disabled={pendingBusy.has(item.id)}>
              Approve
            </button>
            <button type="button" class="btn btn-sm" on:click={() => reject(item)} disabled={pendingBusy.has(item.id)}>
              Reject
            </button>
          </div>
        </div>
      {/each}
    </div>

    <button type="button" class="btn btn-sm" style="margin-top: var(--s4)" on:click={approveAll}>
      Approve all {pending.length}
    </button>
  </section>
{:else}
  <section class="block">
    <Notice
      tone="info"
      title="No facts waiting"
      message="Your profile is fully verified right now."
      hint="Anything new you add below goes into this same approval queue, not straight into the profile."
    />
  </section>
{/if}

<section class="block">
  <Rule text="Add a fact" note="goes to the approval queue" />

  <div class="add-row">
    <label class="sr-only" for="kind">Kind</label>
    <select id="kind" class="select kind" bind:value={newKind}>
      {#each FACT_KINDS as kind (kind)}
        <option value={kind}>{kind}</option>
      {/each}
    </select>
    <label class="sr-only" for="fact">Fact</label>
    <input
      id="fact"
      class="input"
      bind:value={newText}
      placeholder="e.g. Reduced payment reconciliation time from 6 days to 2"
      on:keydown={(e) => e.key === 'Enter' && addFact()}
    />
    <button type="button" class="btn btn-primary" on:click={addFact}>Queue it</button>
  </div>
  <p class="meta">
    Write it as you would say it. Exact duplicates are caught automatically; near-duplicates are flagged
    so you do not approve the same thing twice.
  </p>
</section>

<section class="block">
  <Rule text="Verified facts" note="{facts.length} on file" />

  {#if facts.length === 0}
    <Empty
      title="Nothing verified yet"
      message="Your profile is what every model workflow is allowed to assert. With it empty, the tool can only reorganise what is already in a resume."
      action="Start with two or three achievements you would happily be asked about in an interview."
    />
  {:else}
    <div class="grid-rule facts">
      {#each grouped as group (group.kind)}
        <div class="cell fact-cell">
          <span class="label label-ink">{group.kind}</span>
          <ul class="fact-list">
            {#each group.items as fact (fact.id)}
              <li>
                <span class="fact-text">{fact.text}</span>
                <button type="button" class="btn btn-ghost btn-sm" on:click={() => removeFact(fact)} title="Remove">
                  Remove
                </button>
              </li>
            {/each}
          </ul>
        </div>
      {/each}
      {#if orphanFacts.length > 0}
        <div class="cell fact-cell">
          <span class="label label-ink">other</span>
          <ul class="fact-list">
            {#each orphanFacts as fact (fact.id)}
              <li>
                <span class="fact-text">{fact.text}</span>
                <button type="button" class="btn btn-ghost btn-sm" on:click={() => removeFact(fact)}>Remove</button>
              </li>
            {/each}
          </ul>
        </div>
      {/if}
    </div>
  {/if}
</section>

<section class="block inverted newsprint-texture">
  <div class="inner">
    <Rule text="Voice and contact" note="used in every written output" />

    <div class="voice-grid">
      <div class="field">
        <label class="label" for="voice">How you want to sound</label>
        <textarea
          id="voice"
          class="textarea inverted-input"
          bind:value={voice}
          placeholder="Direct and plain. Short sentences. No buzzwords. Confident without adjectives."
        ></textarea>
      </div>
      <div class="field">
        <label class="label" for="prefs">Preferences</label>
        <textarea
          id="prefs"
          class="textarea inverted-input"
          bind:value={preferences}
          placeholder="British or American spelling. British. Always metric units. No cover letter unless a role is senior."
        ></textarea>
      </div>
    </div>

    <span class="label" style="margin-top: var(--s5)">Contact details</span>
    <div class="contact-grid">
      <input class="input inverted-input" bind:value={contact.name} placeholder="Full name" aria-label="Full name" />
      <input class="input inverted-input" bind:value={contact.email} placeholder="Email" aria-label="Email" />
      <input class="input inverted-input" bind:value={contact.phone} placeholder="Phone" aria-label="Phone" />
      <input class="input inverted-input" bind:value={contact.location} placeholder="Location" aria-label="Location" />
      <input class="input inverted-input" bind:value={contact.links} placeholder="LinkedIn or portfolio URL" aria-label="Links" />
    </div>

    <button type="button" class="btn inverted-btn" on:click={saveProfile} disabled={saving}>
      {saving ? 'Saving…' : 'Save voice and contact'}
    </button>
    <p class="inverted-hint">
      Your email here becomes the From address on generated .eml files, and the name signs the email body.
    </p>
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

  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
  }

  .pending-list {
    margin-top: var(--s5);
    border: var(--hair);
  }

  .pending {
    display: flex;
    gap: var(--s4);
    padding: var(--s4);
    border-bottom: var(--hair-soft);
    align-items: flex-start;
  }

  .pending:last-child {
    border-bottom: none;
  }

  .pending-body {
    flex: 1;
    min-width: 0;
  }

  .pending-head {
    display: flex;
    align-items: center;
    gap: var(--s2);
    margin-bottom: var(--s2);
  }

  .pending-text {
    font-family: var(--font-ui);
    font-size: 1rem;
    line-height: 1.5;
    margin: 0;
  }

  .pending-actions {
    display: flex;
    flex-direction: column;
    gap: var(--s2);
    flex-shrink: 0;
  }

  .context {
    font-family: var(--font-mono);
    font-size: 0.75rem;
    white-space: pre-wrap;
    background: var(--neutral-100);
    padding: var(--s3);
    border-left: 2px solid var(--rule);
    margin: var(--s2) 0 0;
  }

  .add-row {
    display: flex;
    gap: var(--s3);
    align-items: flex-end;
  }

  .kind {
    max-width: 160px;
    border: 1px solid var(--ink);
  }

  .add-row .input {
    flex: 1;
  }

  .facts {
    grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  }

  .fact-cell {
    padding: var(--s4);
  }

  .fact-list {
    list-style: none;
    padding: 0;
    margin: 0;
  }

  .fact-list li {
    display: flex;
    align-items: flex-start;
    gap: var(--s2);
    padding: var(--s2) 0;
    border-bottom: var(--hair-soft);
  }

  .fact-list li:last-child {
    border-bottom: none;
  }

  .fact-text {
    flex: 1;
    font-family: var(--font-ui);
    font-size: 0.875rem;
    line-height: 1.45;
  }

  /* Inverted section: the one place the design flips to ink. */
  .inner {
    padding: var(--s6) 0;
  }

  .voice-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: var(--s5);
    margin-top: var(--s4);
  }

  .contact-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    gap: var(--s3);
    margin: var(--s3) 0 var(--s5);
  }

  .inverted-input {
    color: var(--paper);
    border-bottom-color: var(--paper);
  }

  .inverted-input:focus-visible {
    background: rgba(255, 255, 255, 0.08);
  }

  .inverted-btn {
    border-color: var(--paper);
    color: var(--paper);
  }

  .inverted-btn:hover:not(:disabled) {
    background: var(--paper);
    color: var(--ink);
  }

  .inverted-hint {
    font-family: var(--font-ui);
    font-size: 0.8125rem;
    color: var(--neutral-400);
    margin: var(--s3) 0 0;
  }

  @media (max-width: 767px) {
    .pending {
      flex-direction: column;
    }

    .pending-actions {
      flex-direction: row;
      width: 100%;
    }

    .add-row {
      flex-direction: column;
      align-items: stretch;
    }

    .kind {
      max-width: none;
    }

    .voice-grid {
      grid-template-columns: 1fr;
    }
  }
</style>