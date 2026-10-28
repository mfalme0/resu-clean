<script lang="ts">
  import { onMount } from 'svelte';
  import {
    api,
    type AnalyticsSummary,
    type AnalyticsSourceRow,
    type AnalyticsMonthlyRow,
    type AnalyticsFunnelRow
  } from '../lib/api';
  import { flash } from '../lib/store';
  import Rule from '../lib/ui/Rule.svelte';
  import Empty from '../lib/ui/Empty.svelte';
  import Notice from '../lib/ui/Notice.svelte';

  let summary: AnalyticsSummary | null = null;
  let bySource: AnalyticsSourceRow[] = [];
  let monthly: AnalyticsMonthlyRow[] = [];
  let funnel: AnalyticsFunnelRow[] = [];
  let loading = true;

  async function load() {
    try {
      summary = await api.get<AnalyticsSummary>('/analytics/summary');
      bySource = await api.get<AnalyticsSourceRow[]>('/analytics/by-source');
      monthly = await api.get<AnalyticsMonthlyRow[]>('/analytics/monthly');
      funnel = await api.get<AnalyticsFunnelRow[]>('/analytics/funnel');
    } catch (err) {
      flash(err instanceof Error ? err.message : String(err), 'error');
    } finally {
      loading = false;
    }
  }

  onMount(() => load());

  function pct(value: number): string {
    return value.toFixed(0);
  }

  function days(value: number): string {
    return value.toFixed(1);
  }

  function rate(n: number, d: number): string {
    return d === 0 ? '—' : pct((n / d) * 100);
  }

  function labelOf(stage: string): string {
    switch (stage) {
      case 'Applied': return 'Applications tracked';
      case 'Sent': return 'Kits you sent';
      case 'Replied': return 'Employer replied';
      case 'Interview': return 'Interview stage';
      case 'Outcome (rejected/closed)': return 'No longer open';
      default: return stage;
    }
  }
</script>

<svelte:head><title>Analytics &middot; resu-clean</title></svelte:head>

<section class="intro">
  <span class="label">Section seven</span>
  <h1>What your applications add up to.</h1>
  <p class="justify lede dropcap">
    Every number below comes from the tracker alone — nothing is fetched, nothing is modelled, and
    nothing leaves this machine. If you have not moved a row past "sent" yet, most of this reads as zero,
    which is honest rather than misleading.
  </p>
</section>

<div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>

{#if loading}
  <p class="meta loading">Computing&hellip;</p>
{:else if summary === null}
  <Notice tone="error" title="Could not load analytics" message="Check the backend log." />
{:else}
  <section class="block">
    <Rule text="At a glance" note="{summary.totalApplications} tracked &middot; {pct(summary.responseRatePct)}% response rate" />

    <div class="stats">
      <div class="stat">
        <span class="stat-figure">{summary.totalApplications}</span>
        <span class="stat-label">tracked</span>
      </div>
      <div class="stat">
        <span class="stat-figure">{summary.sent}</span>
        <span class="stat-label">kits sent</span>
      </div>
      <div class="stat">
        <span class="stat-figure">{summary.replied}</span>
        <span class="stat-label">employer replies</span>
      </div>
      <div class="stat">
        <span class="stat-figure">{summary.interview}</span>
        <span class="stat-label">interview stage</span>
      </div>
      <div class="stat">
        <span class="stat-figure">{pct(summary.responseRatePct)}%</span>
        <span class="stat-label">response rate</span>
      </div>
      {#if summary.withResponseTime > 0}
        <div class="stat">
          <span class="stat-figure">{days(summary.avgDaysToFirstResponse)} d</span>
          <span class="stat-label">avg. days to first response</span>
        </div>
      {/if}
    </div>

    <p class="meta">
      <strong>prepared</strong> {summary.prepared} &middot;
      <strong>sent</strong> {summary.sent} &middot;
      <strong>replied</strong> {summary.replied} &middot;
      <strong>interview</strong> {summary.interview} &middot;
      <strong>rejected</strong> {summary.rejected} &middot;
      <strong>closed</strong> {summary.closed}
    </p>
  </section>

  {#if bySource.length > 0}
    <section class="block">
      <Rule text="By source" note="{bySource.length} source{bySource.length === 1 ? '' : 's'}" />
      <table class="data">
        <thead>
          <tr>
            <th>Source</th>
            <th class="num">Total</th>
            <th class="num">Sent</th>
            <th class="num">Replied</th>
            <th class="num">Interview</th>
            <th class="num">Response rate</th>
          </tr>
        </thead>
        <tbody>
          {#each bySource as row (row.sourceName)}
            <tr>
              <td>{row.sourceName}</td>
              <td class="num">{row.total}</td>
              <td class="num">{row.sent}</td>
              <td class="num">{row.replied}</td>
              <td class="num">{row.interview}</td>
              <td class="num">{rate(row.replied, row.sent)}%</td>
            </tr>
          {/each}
        </tbody>
      </table>
      <p class="meta">
        Source names come from the posting itself — a board may republish the same role under its own
        name, and a manual save reads "(manual)". A source with zero sent rows has no rate to show.
      </p>
    </section>
  {/if}

  {#if monthly.length > 0}
    <section class="block">
      <Rule text="Monthly trend" note="{monthly.length} month{monthly.length === 1 ? '' : 's'}" />
      <table class="data">
        <thead>
          <tr>
            <th>Month</th>
            <th class="num">Tracked</th>
            <th class="num">Sent</th>
            <th class="num">Replied</th>
            <th class="num">Interview</th>
          </tr>
        </thead>
        <tbody>
          {#each monthly as row (row.month)}
            <tr>
              <td>{row.month}</td>
              <td class="num">{row.applications}</td>
              <td class="num">{row.sent}</td>
              <td class="num">{row.replied}</td>
              <td class="num">{row.interview}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </section>
  {/if}

  <section class="block">
    <Rule text="Funnel" note="{pct(summary.totalApplications === 0 ? 0 : (summary.sent / summary.totalApplications) * 100)}% of tracked applications went out" />
    <table class="data">
      <thead>
        <tr>
          <th>Stage</th>
          <th class="num">Count</th>
          <th class="num">Of all tracked</th>
        </tr>
      </thead>
      <tbody>
        {#each funnel as row (row.stage)}
          <tr>
            <td>{labelOf(row.stage)}</td>
            <td class="num">{row.count}</td>
            <td class="num">{pct(row.conversionPct)}%</td>
          </tr>
        {/each}
      </tbody>
    </table>
    <p class="meta">
      The funnel is one direction: an application only moves forward when you update its status.
      "Outcome" means the posting is closed or you were turned down — neither says anyone read your
      resume, and neither is counted as a reply.
    </p>
  </section>

  <section class="block">
    <Rule text="What this means" />
    <Notice
      tone="info"
      title="Response rate is the only number that tells you whether a source is worth your time"
      message="A board that saves fifty postings and gets zero replies is costing you afternoons. A source that sends five and gets two replies is doing more for you. Use the by-source table to decide which boards to keep enabling and which to drop."
    />
  </section>

  <section class="block">
    <Rule text="Where these numbers come from" />
    <p>
      Each row in the tracker is one application you created a kit for. When you move its status from
      <em>prepared</em> to <em>sent</em>, that is an application you made yourself — the tool never sends
      anything. When it moves to <em>replied</em>, an employer came back to you. The response rate is
      replies divided by sent. Days-to-first-response is measured from <span class="mono">applied_at</span>
      (the date you recorded) to the last status change, which is an upper bound rather than an exact
      first-reply date.
    </p>
    <p>
      Nothing here calls out to a job board, a search API, or a model. It is entirely local, computed
      from the SQLite database in your <span class="mono">data/</span> folder.
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

  .block {
    margin-top: var(--s7);
  }

  .stats {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
    gap: var(--s4);
    margin-bottom: var(--s5);
  }

  .stat {
    border: var(--hair);
    border-left: var(--rule-heavy);
    padding: var(--s5);
    background: var(--paper);
  }

  .stat-figure {
    font-family: var(--font-display);
    font-size: 2rem;
    font-weight: 900;
    line-height: 1;
    color: var(--ink);
  }

  .stat-label {
    font-family: var(--font-mono);
    font-size: 0.75rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--neutral-600);
    margin-top: var(--s1);
  }

  .data {
    width: 100%;
    border-collapse: collapse;
    margin-top: var(--s3);
    font-size: 0.875rem;
  }

  .data th {
    text-align: left;
    color: var(--neutral-600);
    font-family: var(--font-mono);
    font-size: 0.6875rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    padding: var(--s2) var(--s3);
    border-bottom: var(--hair);
  }

  .data td {
    padding: var(--s2) var(--s3);
    border-bottom: var(--hair-soft);
  }

  .data tr:last-child td {
    border-bottom: none;
  }

  .data .num {
    text-align: right;
    font-family: var(--font-mono);
  }

  .mono {
    font-family: var(--font-mono);
  }

  .loading {
    font-family: var(--font-mono);
    letter-spacing: 0.18em;
    text-transform: uppercase;
  }
</style>