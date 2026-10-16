<script lang="ts">
  import { onMount } from 'svelte';
  import {
    api,
    type Webhook,
    type WebhookDraft,
    type WebhookEvent,
    type WebhookPreview,
    type WebhookSendResult
  } from '../lib/api';
  import { flash } from '../lib/store';
  import Rule from '../lib/ui/Rule.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Notice from '../lib/ui/Notice.svelte';
  import Icon from '../lib/ui/Icon.svelte';
  import { cleanMentions, renderMentions } from '../lib/webhooks';

  /**
   * Every outbound notification this tool can send, and the only ones it can send.
   *
   * There is no per-posting webhook, and that is the point. A board sweep can save hundreds of
   * postings in a single run; a message for each produces a wall of noise that gets muted within
   * a day, which from where you are sitting is identical to having no webhook at all. One counted
   * line survives a notification channel.
   */
  const EVENTS: { id: WebhookEvent; label: string; note: string }[] = [
    {
      id: 'new_jobs',
      label: 'New prospects',
      note: 'Postings saved since the last digest went out.'
    },
    {
      id: 'verification_changed',
      label: 'Postings that changed',
      note: 'A verdict moving to closed or unverifiable, which is worth knowing even when nothing new arrived.'
    }
  ];

  let hooks: Webhook[] = [];
  let draft: WebhookDraft | null = null;
  let mentionsText = '';
  let previews: Record<string, WebhookPreview> = {};
  let results: Record<string, WebhookSendResult> = {};
  let busy = new Set<string>();
  let loaded = false;

  onMount(load);

  function selectValue(event: Event): string {
    return (event.currentTarget as HTMLInputElement).checked ? 'on' : 'off';
  }

  function labelForEvent(id: string): string {
    return EVENTS.find((e) => e.id === id)?.label ?? id;
  }

  /** "never", "just now", "3 minutes ago". Relative, because the point of the field is recency. */
  function ago(iso: string): string {
    const then = Date.parse(iso);
    if (Number.isNaN(then)) return iso;
    const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));
    if (seconds < 90) return 'just now';
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) return `${minutes} ${minutes === 1 ? 'minute' : 'minutes'} ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return `${hours} ${hours === 1 ? 'hour' : 'hours'} ago`;
    const days = Math.round(hours / 24);
    return `${days} ${days === 1 ? 'day' : 'days'} ago`;
  }

  async function load() {
    try {
      hooks = await api.get<Webhook[]>('/webhooks');
      loaded = true;
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    }
  }

  function busyOn(key: string): boolean {
    return busy.has(key);
  }

  async function withBusy<T>(key: string, work: () => Promise<T>): Promise<T | null> {
    busy = new Set([...busy, key]);
    try {
      return await work();
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
      return null;
    } finally {
      const next = new Set(busy);
      next.delete(key);
      busy = next;
    }
  }

  // ---- The editor --------------------------------------------------------

  function startNew() {
    draft = { url: '', mentions: [], senderName: '', events: [], urgentDays: 7, enabled: true };
    mentionsText = '';
  }

  /**
   * Clears a card's preview. The card keeps showing one while the form has unsaved edits on top of
   * it, and a preview that predates what you just typed is worse than no preview at all.
   */
  function clearPreview(id: string) {
    const next = { ...previews };
    delete next[id];
    previews = next;
  }

  function startEdit(hook: Webhook) {
    draft = {
      id: hook.id,
      label: hook.label,
      url: hook.url,
      enabled: hook.enabled,
      mentions: [...hook.mentions],
      senderName: hook.senderName,
      events: [...hook.events],
      urgentDays: hook.urgentDays
    };
    mentionsText = hook.mentions.join(', ');
    clearPreview(hook.id);
  }

  function cancel() {
    draft = null;
    mentionsText = '';
  }

  /**
   * Patch the open draft.
   *
   * One seam rather than spreading in markup: a spread of a narrowed-but-optional draft widens `url`
   * back to possibly-undefined and stops the object satisfying WebhookDraft, which is exactly the
   * sort of thing the checkbox handlers would otherwise get wrong silently.
   */
  function setDraft(patch: Partial<WebhookDraft>) {
    if (!draft) return;
    draft = { ...draft, ...patch };
  }

  function toggleEvent(id: WebhookEvent) {
    if (!draft) return;
    const current = draft.events ?? [];
    // Unticking the last box leaves an empty list, which the server reads as "both". There is
    // deliberately no "neither" state: a digest that counts nothing is a digest that says
    // "no new prospects" every single day and quietly stops being worth reading.
    setDraft({
      events: current.includes(id) ? current.filter((e) => e !== id) : [...current, id]
    });
  }

  // Reactive rather than functions called from markup, so the display, the warning line and the value
  // that gets saved can never disagree with each other. All three read these two.
  $: mentionRule = cleanMentions(mentionsText);

  function save() {
    if (!draft) return;
    const body: WebhookDraft = {
      ...draft,
      url: (draft.url ?? '').trim(),
      mentions: mentionRule.kept
    };
    void withBusy('save', async () => {
      const saved = await api.post<Webhook>('/webhooks', body);
      await load();
      draft = null;
      mentionsText = '';
      flash(saved.enabled ? 'Webhook saved.' : 'Webhook saved, switched off.', 'info');
    });
  }

  async function remove(hook: Webhook) {
    if (!confirm(`Delete "${hook.label || hook.url}"? Nothing else changes.`)) return;
    await withBusy(hook.id, async () => {
      await api.del(`/webhooks/${hook.id}`);
      await load();
      flash('Webhook deleted.', 'info');
    });
  }

  async function toggleEnabled(hook: Webhook, enabled: boolean) {
    await withBusy(hook.id, async () => {
      await api.post<Webhook>('/webhooks', {
        id: hook.id,
        url: hook.url,
        label: hook.label,
        enabled,
        mentions: hook.mentions,
        senderName: hook.senderName,
        events: hook.events,
        urgentDays: hook.urgentDays
      });
      await load();
    });
  }

  // ---- Preview and send --------------------------------------------------

  /**
   * Builds the real digest and sends nothing.
   *
   * This is the only honest way to configure one. The message is generated server-side from the
   * same code path that sends, so what is shown here is exactly what would arrive — there is no
   * second implementation in the browser to drift out of step with the sender.
   */
  async function doPreview(hook: Webhook) {
    await withBusy(hook.id, async () => {
      const result = await api.post<WebhookPreview>(`/webhooks/${hook.id}/preview`);
      previews = { ...previews, [hook.id]: result };
      // A preview and a send answer different questions, so they are never shown in the same slot.
      const next = { ...results };
      delete next[hook.id];
      results = next;
    });
  }

  async function doSend(hook: Webhook) {
    await withBusy(hook.id, async () => {
      const result = await api.post<WebhookSendResult>(`/webhooks/${hook.id}/send`);
      results = { ...results, [hook.id]: result };
      await load();
    });
  }

  /**
   * Previews what is already stored, not the unsaved edits in the form. Both are useful and they
   * are different questions, so they get different buttons.
   */
  function previewStored() {
    const id = draft?.id;
    if (!id) return;
    const stored = hooks.find((h) => h.id === id);
    if (!stored) {
      flash('That webhook is no longer here. It may have been deleted in another tab.', 'error');
      return;
    }
    void doPreview(stored);
  }

  async function sendAll() {
    await withBusy('all', async () => {
      const result = await api.post<{ sent: number; total: number; results: WebhookSendResult[] }>(
        '/webhooks/send-all'
      );
      flash(
        result.total === 0
          ? 'No enabled webhooks, so nothing was sent.'
          : `${result.sent} of ${result.total} sent.`,
        result.sent === result.total ? 'info' : 'error'
      );
      await load();
    });
  }
</script>

<svelte:head><title>Settings &middot; resu-clean</title></svelte:head>

<section class="intro">
  <span class="label">Section seven</span>
  <h1>Notifications,<br />and what leaves.</h1>
  <p class="justify lede dropcap">
    One message a day, addressed to the roles you name, saying how many prospects turned up and how
    many need a decision. Counts only — never your resume, your profile, or your notes. Everything
    here is optional; the tool sends nothing until you point it somewhere.
  </p>
</section>

<div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

{#if !loaded}
  <p class="meta loading">Loading&hellip;</p>
{/if}

<section class="block">
  <div class="label-row">
    <Rule text="Webhooks" note="one digest per destination" />
    <div class="actions">
      <button
        type="button"
        class="btn btn-sm"
        on:click={sendAll}
        disabled={busyOn('all') || hooks.every((h) => !h.enabled)}
      >
        {busyOn('all') ? 'Sending…' : 'Send all now'}
      </button>
      <button type="button" class="btn btn-primary btn-sm" on:click={startNew}>
        <Icon name="plus" size={14} /> Add a webhook
      </button>
    </div>
  </div>

  {#if hooks.length === 0}
    <Empty
      title="No webhooks yet"
      message="Nothing leaves this machine until you configure one. Add a destination, name the roles it should address, and it will send one line a day instead of a message per posting."
      action="Next: paste an incoming webhook URL from Slack, Discord, Teams, ntfy, or anything else that accepts a POST."
    >
      <button type="button" class="btn btn-primary" on:click={startNew}>Add a webhook</button>
    </Empty>
  {:else}
    <div class="hook-list">
      {#each hooks as hook (hook.id)}
        <article class="hook" class:off={!hook.enabled}>
          <header class="hook-head">
            <div class="hook-id">
              <span class="job-title">{hook.label || 'Unnamed digest'}</span>
              <span class="meta mono url">{hook.url}</span>
            </div>
            <label class="switch">
              <input
                type="checkbox"
                checked={hook.enabled}
                disabled={busyOn(hook.id)}
                on:change={(e) => toggleEnabled(hook, selectValue(e) === 'on')}
              />
              <span>{hook.enabled ? 'on' : 'off'}</span>
            </label>
          </header>

          <dl class="facts">
            <div>
              <dt>Addressed to</dt>
              <dd class="mono">{hook.mentions.length ? hook.mentions.map((m) => '@' + m).join(' ') : '—'}</dd>
            </div>
            <div>
              <dt>Reported as</dt>
              <dd class="mono">{hook.senderName || '—'}</dd>
            </div>
            <div>
              <dt>Counts</dt>
              <dd class="mono">{hook.events.map(labelForEvent).join(', ') || 'both'}</dd>
            </div>
            <div>
              <dt>Needs action within</dt>
              <dd class="mono">{hook.urgentDays} {hook.urgentDays === 1 ? 'day' : 'days'}</dd>
            </div>
            <div>
              <dt>Last sent</dt>
              <dd class="mono">{hook.lastSentAt ? ago(hook.lastSentAt) : 'never'}</dd>
            </div>
          </dl>

          <div class="actions">
            <button type="button" class="btn btn-sm" on:click={() => doPreview(hook)} disabled={busyOn(hook.id)}>
              {busyOn(hook.id) ? 'Working…' : 'Preview'}
            </button>
            <button
              type="button"
              class="btn btn-sm"
              on:click={() => doSend(hook)}
              disabled={busyOn(hook.id) || !hook.enabled}
            >
              Send now
            </button>
            <button type="button" class="btn btn-sm" on:click={() => startEdit(hook)}>Edit</button>
            <button type="button" class="btn btn-ghost btn-sm" on:click={() => remove(hook)}>Delete</button>
          </div>

          {#if previews[hook.id]}
            <div class="preview">
              <span class="label">Would send</span>
              <p class="digest">{previews[hook.id].message}</p>
              <p class="meta">
                {previews[hook.id].newCount} new · {previews[hook.id].urgentCount} need action ·
                {previews[hook.id].changedCount} changed · since {ago(previews[hook.id].since)}
              </p>
              {#if previews[hook.id].items.length > 0}
                <ul class="items">
                  {#each previews[hook.id].items.slice(0, 12) as item (item.title + item.company)}
                    <li class:urgent={item.urgent}>
                      <span class="job-title">{item.title}</span>
                      <span class="meta company">{item.company}</span>
                      {#if item.why}<span class="badge badge-accent">{item.why}</span>{/if}
                    </li>
                  {/each}
                </ul>
                {#if previews[hook.id].items.length > 12}
                  <p class="meta">and {previews[hook.id].items.length - 12} more in the structured payload.</p>
                {/if}
              {/if}
            </div>
          {/if}

          {#if results[hook.id]}
            <Notice
              tone={results[hook.id].ok ? 'info' : 'error'}
              title={results[hook.id].ok ? 'Sent' : 'Not sent'}
              message={results[hook.id].ok
                ? `Delivered. The next digest counts from now, so nothing is reported twice.`
                : `${results[hook.id].detail} Nothing was lost — the cursor did not move, so tomorrow's digest still counts today.`}
            >
              {#if results[hook.id].responseBody}
                <pre class="body">{results[hook.id].responseBody}</pre>
              {/if}
            </Notice>
          {/if}
        </article>
      {/each}
    </div>
  {/if}
</section>

{#if draft}
  <section class="block">
    <Rule
      text={draft.id ? 'Edit webhook' : 'New webhook'}
      note="preview before you point it anywhere real"
    />

    <div class="form-grid">
      <div class="field">
        <label class="label" for="wh-url">Webhook URL</label>
        <input
          id="wh-url"
          class="input mono"
          bind:value={draft.url}
          placeholder="https://hooks.slack.com/services/T000/B000/xxxx"
        />
        <p class="meta">An https URL that accepts a POST. Nothing is stored until you save.</p>
      </div>

      <div class="field">
        <label class="label" for="wh-label">Label</label>
        <input id="wh-label" class="input" bind:value={draft.label} placeholder="Daily digest" />
        <p class="meta">Only for you. It is never sent.</p>
      </div>

      <div class="field">
        <label class="label" for="wh-mentions">Address the message to</label>
        <input
          id="wh-mentions"
          class="input mono"
          bind:value={mentionsText}
          placeholder="jobseekers, dataeng"
        />
        <p class="meta">
          Roles, not people — this app has no accounts. Separate several with commas. Each becomes
          one @mention at the start of the message.
        </p>
        {#if mentionRule.kept.length > 0}
          <p class="meta">Will send as <span class="mono">{renderMentions(mentionRule.kept)}</span></p>
        {/if}
        {#if mentionRule.dropped.length > 0}
          <p class="meta warn-line">
            Ignored: {mentionRule.dropped.map((m) => `"${m}"`).join(', ')} — a mention cannot contain a
            space. Write it as {mentionRule.dropped[0].replace(/\s+/g, '')} instead.
          </p>
        {/if}
      </div>

      <div class="field">
        <label class="label" for="wh-sender">Report the digest as</label>
        <input id="wh-sender" class="input" bind:value={draft.senderName} placeholder="userx" />
        <p class="meta">
          The name in the sentence. Leave it empty and the message reads “@jobseekers has 10 new
          prospects”.
        </p>
      </div>

      <div class="field">
        <label class="label" for="wh-urgent">Needs action within</label>
        <input id="wh-urgent" class="input" type="number" min="0" max="90" bind:value={draft.urgentDays} />
        <p class="meta">
          A deadline inside this many days counts as needing action. So does a posting that has
          already closed, and one whose status cannot be established.
        </p>
      </div>

      <fieldset class="field checks">
        <legend class="label">What it counts</legend>
        {#each EVENTS as event (event.id)}
          <label class="check">
            <input type="checkbox" checked={(draft.events ?? []).includes(event.id)} on:change={() => toggleEvent(event.id)} />
            <span>
              <span class="job-title">{event.label}</span>
              <span class="meta">{event.note}</span>
            </span>
          </label>
        {/each}
        <p class="meta">
          {#if (draft.events ?? []).length === 0}
            Nothing ticked means both. They are counted separately in the message, because a posting
            that went closed is not a new prospect.
          {:else}
            Only what is ticked. Untick both to count both.
          {/if}
        </p>
      </fieldset>

      <div class="field">
        <label class="label" for="wh-enabled">Send automatically</label>
        <label class="switch">
          <input id="wh-enabled" type="checkbox" checked={draft.enabled !== false} on:change={(e) => setDraft({ enabled: selectValue(e) === 'on' })} />
          <span>{draft.enabled !== false ? 'enabled' : 'disabled'}</span>
        </label>
        <p class="meta">
          Off means the destination is kept but a scheduled run will skip it. Preview and Send now
          still work.
        </p>
      </div>
    </div>

    <Notice
      tone="info"
      title="What this will send"
      message="Job titles, companies, URLs, verdicts and counts. Never resume text, profile facts, tracker notes, or generated kits — that is what keeps “your resume never leaves this machine” literally true."
    />

    <div class="actions" style="margin-top: var(--s4)">
      <button type="button" class="btn btn-primary" on:click={save} disabled={busyOn('save') || !(draft.url ?? '').trim()}>
        {busyOn('save') ? 'Saving…' : draft.id ? 'Save changes' : 'Add webhook'}
      </button>
      <button type="button" class="btn" on:click={cancel}>Cancel</button>
      {#if draft.id}
        <button type="button" class="btn btn-sm" on:click={previewStored} disabled={busyOn(draft.id)}>
          Preview the saved version
        </button>
      {/if}
    </div>
  </section>
{/if}

<section class="block">
  <Rule text="Running it once a day" note="the scheduler is not built in" />
  <p class="justify">
    Nothing fires on a timer by itself. That is deliberate for a tool whose whole claim is that it
    stays quiet until you ask, and because a background timer inside a container is one more thing
    that can surprise you after a restart. Instead, point something you already run at the send
    endpoint.
  </p>

  <div class="cron">
    <span class="label">crontab</span>
    <pre class="body"># 07:05 every day: one digest, per configured webhook
5 7 * * * curl -fsS -X POST http://127.0.0.1:8080/api/webhooks/send-all</pre>
  </div>
  <pre class="body"># or as a systemd timer, or a scheduled task on Windows:
#   curl -fsS -X POST http://127.0.0.1:8080/api/webhooks/send-all</pre>

  <p class="meta">
    Add <span class="mono">-H "X-API-Key: $RESUCLEAN_API_KEY"</span> if you run the server with a key.
    The cursor only moves when a send succeeds, so a destination that is down leaves the day
    queued rather than dropping it.
  </p>
</section>

<section class="block">
  <Rule text="What crosses the boundary" />
  <div class="boundary">
    <div class="col in">
      <span class="label label-pass">Sent</span>
      <ul>
        <li>How many prospects turned up since the last digest</li>
        <li>How many need a decision, and how many postings changed status</li>
        <li>Job titles, companies and URLs, in the structured payload</li>
        <li>The @roles you named, and the name you chose</li>
      </ul>
    </div>
    <div class="col out">
      <span class="label label-accent">Never sent</span>
      <ul>
        <li>Any resume text, in any version</li>
        <li>Your profile facts, including anything pending approval</li>
        <li>Tracker notes and stage history</li>
        <li>Generated cover letters and application kits</li>
        <li>Your API keys, or the webhook destination's response, in the server log</li>
      </ul>
    </div>
  </div>
  <p class="meta">
    This is not a policy the page enforces. It is enforced where the payload is built, in
    <span class="mono">WebhookService</span>, and pinned by a test that fails if a profile fact ever
    appears in a digest.
  </p>
</section>

<style>
  .hook-list {
    display: grid;
    gap: var(--s4);
  }

  .hook {
    border: var(--hair);
    padding: var(--s4);
    background: var(--paper);
  }

  .hook.off {
    opacity: 0.62;
  }

  .hook-head {
    display: flex;
    align-items: flex-start;
    gap: var(--s3);
    flex-wrap: wrap;
  }

  .hook-id {
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }

  .hook-id .url {
    overflow-wrap: anywhere;
  }

  .switch {
    margin-left: auto;
    display: inline-flex;
    align-items: center;
    gap: var(--s2);
    font-family: var(--font-mono);
    font-size: 0.6875rem;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    color: var(--ink-soft);
    cursor: pointer;
    min-height: var(--tap);
  }

  .facts {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
    gap: var(--s3) var(--s4);
    margin: var(--s4) 0;
    padding: var(--s3) 0;
    border-top: var(--hair-soft);
    border-bottom: var(--hair-soft);
  }

  .facts div {
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }

  .facts dt {
    font-family: var(--font-mono);
    font-size: 0.625rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--ink-soft);
  }

  .facts dd {
    margin: 0;
    font-size: 0.8125rem;
  }

  .preview {
    margin-top: var(--s4);
    padding: var(--s3);
    background: var(--paper-2);
    border-left: 3px solid var(--accent);
  }

  .digest {
    font-family: var(--font-mono);
    font-size: 0.9375rem;
    line-height: 1.5;
    margin: var(--s2) 0;
    overflow-wrap: anywhere;
  }

  .items {
    list-style: none;
    padding: 0;
    margin: var(--s3) 0 0;
    display: grid;
    gap: var(--s1);
  }

  .items li {
    display: flex;
    align-items: baseline;
    gap: var(--s2);
    flex-wrap: wrap;
    font-size: 0.8125rem;
    padding: 2px 0;
  }

  .items li.urgent {
    font-weight: 600;
  }

  .items .company {
    margin: 0;
  }

  .label-row {
    display: flex;
    align-items: flex-end;
    gap: var(--s3);
    flex-wrap: wrap;
  }

  /* :global because .rule belongs to the Rule component's own scope, so a scoped descendant
     selector for it silently matches nothing. */
  .label-row :global(.rule) {
    flex: 1;
    min-width: 240px;
  }

  .checks {
    border: none;
    padding: 0;
    margin: 0;
    display: flex;
    flex-direction: column;
    gap: var(--s3);
  }

  .check {
    display: flex;
    gap: var(--s3);
    align-items: flex-start;
    cursor: pointer;
  }

  .check span {
    display: flex;
    flex-direction: column;
  }

  .check .meta {
    margin: 0;
  }

  .warn-line {
    color: var(--accent);
  }

  .cron {
    margin: var(--s4) 0 var(--s3);
  }

  .body {
    font-family: var(--font-mono);
    font-size: 0.75rem;
    line-height: 1.6;
    background: var(--paper-2);
    border: var(--hair-soft);
    padding: var(--s3);
    overflow-x: auto;
    white-space: pre;
    margin: 0;
  }

  .boundary {
    display: grid;
    grid-template-columns: 1fr 1fr;
    border: var(--hair);
    margin-bottom: var(--s3);
  }

  .boundary .col {
    padding: var(--s4);
  }

  .boundary .col + .col {
    border-left: var(--hair);
  }

  .boundary ul {
    margin: var(--s3) 0 0;
    padding-left: var(--s4);
    display: grid;
    gap: var(--s2);
    font-size: 0.875rem;
  }

  .loading {
    font-family: var(--font-mono);
    letter-spacing: 0.18em;
    text-transform: uppercase;
  }

  @media (max-width: 767px) {
    .boundary {
      grid-template-columns: 1fr;
    }

    .boundary .col + .col {
      border-left: none;
      border-top: var(--hair);
    }
  }
</style>