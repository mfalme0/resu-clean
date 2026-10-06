/** Inline SVG glyph set: 1.5px stroke, no icon library, no network request. */
<script lang="ts">
  export let name:
    | 'doc'
    | 'check'
    | 'search'
    | 'brush'
    | 'gear'
    | 'mail'
    | 'book'
    | 'list'
    | 'chevron'
    | 'download'
    | 'external'
    | 'plus'
    | 'trash'
    | 'chart' = 'doc';
  export let size = 24;
</script>

<svg
  width={size}
  height={size}
  viewBox="0 0 24 24"
  fill="none"
  stroke="currentColor"
  stroke-width="1.5"
  stroke-linecap="square"
  stroke-linejoin="miter"
  aria-hidden="true"
  focusable="false"
>
  {#if name === 'doc'}
    <path d="M14 3H6v18h12V7z" /><path d="M14 3v4h4" /><path d="M9 12h6M9 16h6" />
  {:else if name === 'check'}
    <path d="M4 13l5 5L20 6" />
  {:else if name === 'search'}
    <circle cx="11" cy="11" r="7" /><path d="M20 20l-4.5-4.5" />
  {:else if name === 'brush'}
    <path d="M4 20h16" /><path d="M6 16l9-9 3 3-9 9H6z" />
  {:else if name === 'gear'}
    <circle cx="12" cy="12" r="3" /><path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2 2M16.4 16.4l2 2M18.4 5.6l-2 2M7.6 16.4l-2 2" />
  {:else if name === 'mail'}
    <rect x="3" y="5" width="18" height="14" /><path d="M3 6l9 7 9-7" />
  {:else if name === 'book'}
    <path d="M4 4h7v16H4z" /><path d="M13 4h7v16h-7z" />
  {:else if name === 'list'}
    <path d="M4 6h16M4 12h16M4 18h10" />
  {:else if name === 'chevron'}
    <path d="M9 5l7 7-7 7" />
  {:else if name === 'download'}
    <path d="M12 3v12" /><path d="M7 11l5 5 5-5" /><path d="M4 20h16" />
  {:else if name === 'external'}
    <path d="M14 4h6v6" /><path d="M20 4l-9 9" /><path d="M18 14v6H4V6h6" />
  {:else if name === 'plus'}
    <path d="M12 5v14M5 12h14" />
  {:else if name === 'trash'}
    <path d="M4 7h16" /><path d="M9 7V4h6v3" /><path d="M6 7l1 13h10l1-13" />
  {:else if name === 'chart'}
    <path d="M4 20V10M10 20V4M16 20v-7M22 20H2" />
  {/if}
</svg>