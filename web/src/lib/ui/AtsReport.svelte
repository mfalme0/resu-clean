<script lang="ts">
  import { createEventDispatcher } from 'svelte';

  export let ats: import('../api').AtsReport;

  const dispatch = createEventDispatcher<{ select: string }>();

  $: failed = ats.checks.filter((c) => !c.passed);
  $: passed = ats.checks.filter((c) => c.passed);
  $: scoreClass = ats.score >= 85 ? 'strong' : ats.score >= 70 ? 'good' : ats.score >= 55 ? 'mid' : 'weak';
</script>

<section class="report">
  <!-- Score block: the number is the headline, like a front-page figure. -->
  <header class="grid-rule head">
    <div class="cell score-cell">
      <span class="label">ATS score</span>
      <div class="score {scoreClass}">{ats.score}</div>
      <span class="meta">out of 100 &middot; {ats.verdict}</span>
    </div>
    <div class="cell">
      <span class="label">Checks</span>
      <p class="figure">
        <strong>{passed.length}</strong> passed &middot; <strong>{failed.length}</strong> failing
      </p>
      <p class="meta">
        {ats.wordCount} words &middot; {ats.lineCount} lines
        {#if ats.usedModel}&middot; fixes by {ats.model}{/if}
      </p>
    </div>
    {#if ats.keywords}
      <div class="cell">
        <span class="label">Keyword match</span>
        <div class="score {ats.keywords.matchPct >= 55 ? 'strong' : 'mid'}">{ats.keywords.matchPct}%</div>
        <span class="meta">
          {ats.keywords.matchedCount} matched &middot; {ats.keywords.missingCount} missing
        </span>
      </div>
    {/if}
  </header>

  {#if failed.length > 0}
    <div class="failures">
      <span class="label label-ink">Failing checks &mdash; each one with what to do</span>
      {#each failed as check (check.id)}
        <div class="check check-fail">
          <div class="check-mark" aria-hidden="true">&times;</div>
          <div>
            <div class="check-title">
              <strong>{check.label}</strong>
              <span class="badge badge-accent">{check.category}</span>
            </div>
            <p class="detail">{check.detail}</p>
            <p class="tip">{check.tip}</p>
          </div>
        </div>
      {/each}
    </div>
  {:else}
    <div class="clean">
      <span class="check-mark" aria-hidden="true">&#10003;</span>
      <p>Every check passed. Keep this version and move on to the tailoring step.</p>
    </div>
  {/if}

  {#if ats.prioritizedFixes.length > 0}
    <div class="fixes">
      <span class="label label-ink">Prioritised fixes</span>
      <ol>
        {#each ats.prioritizedFixes as fix, i (i)}
          <li>{fix}</li>
        {/each}
      </ol>
    </div>
  {/if}

  {#if ats.keywords && ats.keywords.missing.length > 0}
    <div class="keywords">
      <span class="label label-ink">Missing keywords</span>
      <p class="kw-intro">
        Use these truthfully, where you genuinely have the experience. Never add a skill just because
        a posting lists it.
      </p>
      <ul class="kw-list">
        {#each ats.keywords.missing.slice(0, 30) as word (word)}
          <li class="badge">{word}</li>
        {/each}
      </ul>
      {#if ats.keywords.matched.length > 0}
        <span class="label" style="margin-top: var(--s4)">Already matched</span>
        <ul class="kw-list">
          {#each ats.keywords.matched.slice(0, 30) as word (word)}
            <li class="badge badge-pass">{word}</li>
          {/each}
        </ul>
      {/if}
    </div>
  {/if}
</section>

<style>
  .head {
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    margin-bottom: var(--s5);
  }

  .cell {
    padding: var(--s5);
  }

  .score-cell {
    background: var(--neutral-100);
  }

  .score {
    font-family: var(--font-display);
    font-size: 3.5rem;
    font-weight: 900;
    line-height: 1;
    letter-spacing: -0.04em;
    margin: var(--s2) 0;
  }

  .score.strong {
    color: var(--pass);
  }

  .score.weak {
    color: var(--accent);
  }

  .figure {
    font-family: var(--font-display);
    font-size: 1.75rem;
    line-height: 1.1;
    margin: var(--s2) 0;
  }

  .failures,
  .fixes,
  .keywords {
    margin-top: var(--s6);
  }

  .check-title {
    display: flex;
    align-items: center;
    gap: var(--s2);
    flex-wrap: wrap;
    font-family: var(--font-ui);
    font-size: 0.9375rem;
  }

  .detail {
    margin: var(--s1) 0 0;
    font-family: var(--font-ui);
    font-size: 0.875rem;
    color: var(--neutral-600);
  }

  .clean {
    display: flex;
    align-items: center;
    gap: var(--s3);
    border: var(--hair);
    border-left: var(--rule-heavy);
    border-left-color: var(--pass);
    padding: var(--s4);
  }

  .clean p {
    margin: 0;
    font-family: var(--font-ui);
    font-size: 0.9375rem;
  }

  .fixes ol {
    font-family: var(--font-ui);
    font-size: 0.9375rem;
    padding-left: var(--s5);
    margin: var(--s3) 0 0;
  }

  .fixes li {
    margin-bottom: var(--s2);
  }

  .kw-intro {
    font-family: var(--font-ui);
    font-size: 0.875rem;
    color: var(--neutral-600);
    max-width: 70ch;
    margin: var(--s2) 0;
  }

  .kw-list {
    list-style: none;
    padding: 0;
    margin: var(--s3) 0 0;
    display: flex;
    flex-wrap: wrap;
    gap: var(--s2);
  }
</style>