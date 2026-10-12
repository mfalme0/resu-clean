<script lang="ts">
  import { createEventDispatcher } from 'svelte';
  import type { RewriteOutcome } from '../lib/api';
  import Rule from './Rule.svelte';

  /**
   * The measured verdict on a model rewrite. Shows the before and after numbers, and when the
   * rewrite was refused it offers it back rather than only reporting the refusal — the gate exists
   * to stop a blind leap of faith, not to remove the user's judgement from the loop.
   */
  export let outcome: RewriteOutcome;

  const dispatch = createEventDispatcher<{ keep: string }>();

  $: delta = outcome.candidateScore - outcome.baselineScore;
  $: keywordDelta = outcome.candidateMissing - outcome.baselineMissing;
</script>

<div class="verdict" class:refused={!outcome.accepted}>
  <Rule text={outcome.accepted ? 'Rewrite measured and accepted' : 'Rewrite refused'} />

  <div class="numbers">
    <div class="side">
      <span class="label">Before</span>
      <span class="figure">{outcome.baselineScore}</span>
      <span class="sub">{outcome.baselineMissing} keyword{outcome.baselineMissing === 1 ? '' : 's'} missing</span>
    </div>
    <span class="arrow" aria-hidden="true">&rarr;</span>
    <div class="side">
      <span class="label">After</span>
      <span class="figure" class:down={delta < 0}>{outcome.candidateScore}</span>
      <span class="sub">{outcome.candidateMissing} keyword{outcome.candidateMissing === 1 ? '' : 's'} missing</span>
    </div>
  </div>

  <p class="reason">{outcome.reason}</p>

  {#if !outcome.accepted && outcome.rejectedText}
    <details>
      <summary>Read the refused rewrite anyway</summary>
      <pre class="rejected">{outcome.rejectedText}</pre>
      <button type="button" class="btn" on:click={() => dispatch('keep', outcome.rejectedText ?? '')}>
        Save it as a new version
      </button>
      <p class="meta">
        This overrules the measurement, not the guard: the text was already checked for invented
        numbers, dates and names before it was scored.
      </p>
    </details>
  {/if}
</div>

<style>
  .verdict {
    border: var(--hair);
    border-left: 4px solid var(--neutral-700);
    padding: var(--s5);
    margin-top: var(--s4);
  }

  .verdict.refused {
    border-left-color: var(--accent);
  }

  .numbers {
    display: flex;
    align-items: center;
    gap: var(--s5);
    margin: var(--s3) 0;
  }

  .side {
    display: grid;
  }

  .figure {
    font-family: var(--font-display);
    font-size: 2.25rem;
    font-weight: 900;
    line-height: 1;
  }

  .figure.down {
    color: var(--accent);
  }

  .sub {
    font-size: 0.8rem;
    opacity: 0.7;
  }

  .arrow {
    font-size: 1.5rem;
    opacity: 0.5;
  }

  .reason {
    margin-bottom: var(--s3);
  }

  details {
    border-top: var(--hair);
    padding-top: var(--s3);
  }

  summary {
    cursor: pointer;
    font-family: var(--font-ui);
  }

  .rejected {
    font-family: var(--font-mono);
    font-size: 0.8rem;
    white-space: pre-wrap;
    max-height: 22rem;
    overflow-y: auto;
    background: var(--paper-sunk, rgba(0, 0, 0, 0.04));
    padding: var(--s3);
  }
</style>