/** Small presentational primitives. Each one owns its Newsprint styling so screens stay declarative. */
<script lang="ts">
  export let label: string;
  export let value: string | number | null | undefined = null;
  export let kind: 'pass' | 'accent' | 'plain' = 'plain';
  export let title = '';
</script>

<span class="badge" class:badge-pass={kind === 'pass'} class:badge-accent={kind === 'accent'} {title}>{label}</span>
{#if value !== null && value !== undefined && value !== ''}<span class="mono v">{value}</span>{/if}

<style>
  .badge {
    display: inline-flex;
  }

  .v {
    font-size: 0.6875rem;
    color: var(--neutral-500);
    margin-left: 6px;
  }
</style>