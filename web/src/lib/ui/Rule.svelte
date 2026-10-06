<script lang="ts">
  /** Section eyebrow: uppercase monospace label with an editorial rule. */
  export let text: string;
  export let note = '';
</script>

<div class="rule">
  <span class="label label-ink">{text}</span>
  {#if note}<span class="meta">{note}</span>{/if}
</div>

<style>
  .rule {
    display: flex;
    align-items: baseline;
    gap: var(--s3);
    border-bottom: var(--rule-heavy);
    padding-bottom: var(--s2);
    margin-bottom: var(--s4);
  }

  .meta {
    margin-left: auto;
  }
</style>