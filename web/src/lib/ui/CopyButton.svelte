/** Copy-to-clipboard button with inline confirmation. No dependency, no toast library. */
<script lang="ts">
  export let text: string;
  export let label = 'Copy';
  export let small = false;

  let state: 'idle' | 'done' | 'failed' = 'idle';

  async function copy() {
    try {
      await navigator.clipboard.writeText(text);
      state = 'done';
    } catch {
      state = 'failed';
    }
    setTimeout(() => (state = 'idle'), 2000);
  }
</script>

<button type="button" class="btn btn-ghost {small ? 'btn-sm' : ''}" class:btn-accent={state === 'failed'} on:click={copy} disabled={!text}>
  {#if state === 'done'}
    <span aria-hidden="true">&#10003;</span> Copied
  {:else if state === 'failed'}
    Copy failed
  {:else}
    {label}
  {/if}
</button>

<style>
  span[aria-hidden] {
    font-family: var(--font-mono);
  }
</style>