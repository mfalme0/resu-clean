<script lang="ts">
  /** Zero-state block: an ornament, a headline, one sentence, and the action to take. */
  export let title: string;
  export let message: string = '';
  export let action = '';
</script>

<div class="empty">
  <div class="ornament" aria-hidden="true">&#x2727; &#x2727; &#x2727;</div>
  <h3>{title}</h3>
  {#if message}<p>{message}</p>{/if}
  <slot />
  {#if action}<p class="action">{action}</p>{/if}
</div>

<style>
  .empty {
    text-align: center;
    padding: var(--s7) var(--s4);
    border: var(--hair);
    background: var(--paper);
  }

  .empty .ornament {
    padding: 0 0 var(--s4);
  }

  .empty h3 {
    margin-bottom: var(--s2);
  }

  .empty p {
    max-width: 52ch;
    margin-left: auto;
    margin-right: auto;
    color: var(--neutral-600);
  }

  .action {
    font-family: var(--font-mono);
    font-size: 0.8125rem;
    color: var(--ink);
    border-top: var(--hair-soft);
    padding-top: var(--s3);
    margin-top: var(--s4);
    margin-bottom: 0;
  }
</style>