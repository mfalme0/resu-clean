<script lang="ts">
  /** A failure or an action. Always says what to do next, never just "error". */
  export let title: string;
  export let message: string = '';
  export let hint: string = '';
  export let tone: 'error' | 'info' = 'error';
</script>

<div class="notice" class:notice-error={tone === 'error'} role={tone === 'error' ? 'alert' : 'status'}>
  <h3>{title}</h3>
  {#if message}<p>{message}</p>{/if}
  {#if hint}<p class="hint"><span class="label">Next</span>{hint}</p>{/if}
  <slot />
</div>

<style>
  .hint {
    margin-bottom: 0;
    font-family: var(--font-ui);
    font-size: 0.875rem;
  }

  .hint .label {
    display: inline-block;
    margin-right: var(--s2);
  }
</style>