<script lang="ts">
  import { onMount, tick } from 'svelte';
  import Icon from './lib/ui/Icon.svelte';
  import Notice from './lib/ui/Notice.svelte';
  import { routes, currentPath, navigate, onLinkClick } from './lib/router';
  import {
    banner,
    capabilities,
    health,
    loadSystem,
    memory,
    pendingCount,
    refreshMemory,
    refreshPendingCount
  } from './lib/store';
  import { hasApiKey, setApiKey } from './lib/api';

  type RouteComponent = Component<Record<string, never>>;

  let path = currentPath();
  let CurrentScreen: RouteComponent | null = null;
  let loading = true;
  let navOpen = false;
  let showKeyPrompt = false;
  let keyDraft = '';
  let online = true;
  let edition = '';

  const navIcons: Record<string, 'doc' | 'book' | 'search' | 'mail' | 'list' | 'gear'> = {
    '/': 'doc',
    '/profile': 'book',
    '/jobs': 'search',
    '/kits': 'mail',
    '/tracker': 'list',
    '/models': 'gear'
  };

  async function render(target: string) {
    loading = true;
    const route = routes.find((r) => r.path === target) ?? routes[0];
    const mod = await route.load();
    await tick();
    CurrentScreen = mod.default as RouteComponent;
    loading = false;
  }

  onMount(async () => {
    window.addEventListener('popstate', onPop);
    document.addEventListener('click', handleLink);

    edition = new Date().toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
    online = (await health()) !== null;
    await loadSystem();
    await refreshPendingCount();
    await refreshMemory();

    // The Models screen owns the API key when auth is enabled.
    showKeyPrompt = $capabilities?.authMode === 'api-key' && !hasApiKey();
    if (showKeyPrompt) keyDraft = '';

    await render(path);
    return () => {
      window.removeEventListener('popstate', onPop);
      document.removeEventListener('click', handleLink);
    };
  });

  function onPop() {
    path = currentPath();
    navOpen = false;
    void render(path);
  }

  function go(target: string) {
    navigate(target);
  }

  function handleLink(event: MouseEvent) {
    onLinkClick(go)(event);
  }

  function submitKey() {
    setApiKey(keyDraft);
    showKeyPrompt = false;
    void loadSystem();
    void refreshPendingCount();
  }

  $: pending = $pendingCount;
</script>

<svelte:head>
  <title>resu-clean &middot; personal resume toolkit</title>
  <meta name="description" content="Self-hosted resume toolkit: clean, ATS-scan, tailor, search jobs, prepare application kits." />
  <meta name="color-scheme" content="light dark" />
</svelte:head>

<!-- Masthead: newspaper nameplate plus edition line. -->
<header class="masthead">
  <div class="wrap masthead-inner">
    <a href="/" class="nameplate" on:click|preventDefault={() => go('/')}>
      <span class="wordmark">resu&#8209;clean</span>
      <span class="tagline">all the resume that's fit to print</span>
    </a>
    <div class="edition">
      <span class="label">Vol.&nbsp;1 &middot; {edition}</span>
      <span class="meta" class:offline={!online}>{online ? 'local &middot; offline capable' : 'backend unreachable'}</span>
    </div>
    <button
      type="button"
      class="btn btn-ghost nav-toggle"
      aria-expanded={navOpen}
      aria-controls="primary-nav"
      on:click={() => (navOpen = !navOpen)}
    >
      <Icon name="list" size={20} />
      <span class="sr-only">Menu</span>
    </button>
  </div>

  <nav id="primary-nav" class="nav" class:open={navOpen} aria-label="Primary">
    <div class="wrap nav-inner">
      {#each routes as route (route.path)}
        <a
          href={route.path}
          class="nav-link"
          class:active={route.path === path}
          aria-current={route.path === path ? 'page' : undefined}
          on:click|preventDefault={() => go(route.path)}
        >
          <Icon name={navIcons[route.path] ?? 'doc'} size={18} />
          <span>{route.title}</span>
          {#if route.path === '/profile' && pending > 0}
            <span class="badge badge-accent nav-badge">{pending}</span>
          {/if}
        </a>
      {/each}
    </div>
  </nav>
</header>

<!-- Pending approvals ticker: the one thing that needs your attention. -->
{#if pending > 0}
  <div class="ticker" role="status">
    <div class="ticker-track">
      {#each Array(2) as _}
        <span class="ticker-item">
          <span class="tick-accent">{pending}</span>
          pending profile {pending === 1 ? 'fact awaits' : 'facts await'} your approval
        </span>
        <span class="ticker-item">approve or reject each one in Profile</span>
        <span class="ticker-item">nothing is saved until you say yes</span>
      {/each}
    </div>
  </div>
{/if}

{#if $banner}
  <div class="wrap banner-slot">
    <Notice
      tone={$banner.tone === 'error' ? 'error' : 'info'}
      title={$banner.tone === 'error' ? 'Something went wrong' : 'Heads up'}
      message={$banner.text}
    />
  </div>
{/if}

{#if !$capabilities?.anyModelConfigured}
  <div class="wrap banner-slot">
    <Notice
      tone="info"
      title="No model configured"
      message="Deterministic clean, the ATS rule scan, job search and template application kits all work without a model."
      hint="Add a provider in Models &amp; Routes to enable model rewrites, prioritised fixes, tailoring and written emails."
    >
      <a class="btn btn-sm" href="/models" on:click|preventDefault={() => go('/models')}>Open Models &amp; Routes</a>
    </Notice>
  </div>
{/if}

<main class="wrap main" id="main">
  {#if showKeyPrompt}
    <Notice title="API key required" message="This server is running with RESUCLEAN_API_KEY set." hint="Paste the key. It is stored only in this browser.">
      <div class="key-row">
        <input
          class="input"
          type="password"
          bind:value={keyDraft}
          placeholder="X-API-Key"
          aria-label="API key"
          on:keydown={(e) => e.key === 'Enter' && submitKey()}
        />
        <button type="button" class="btn btn-primary" on:click={submitKey}>Unlock</button>
      </div>
    </Notice>
  {:else if loading}
    <p class="meta loading">Loading&hellip;</p>
  {:else if CurrentScreen}
    <svelte:component this={CurrentScreen} />
  {/if}
</main>

<footer class="footer">
  <div class="wrap footer-inner">
    <div class="col">
      <span class="serif footer-title">resu&#8209;clean</span>
      <p class="meta">Self-hosted. Your resume never leaves this machine except to the job boards you fetch.</p>
    </div>
    <div class="col">
      <span class="label">Screens</span>
      {#each routes as route (route.path)}
        <a
          href={route.path}
          on:click|preventDefault={() => go(route.path)}
        >{route.title}</a>
      {/each}
    </div>
    <div class="col">
      <span class="label">Colophon</span>
      <span class="meta">Edition: Vol 1.0</span>
      <span class="meta">Set in the reader's own faces.</span>
      {#if $memory}
        <span class="meta">Backend {$memory.rssMb} MB resident &middot; {$memory.opsActive} ops running</span>
      {/if}
    </div>
  </div>
  <div class="wrap footer-strip">
    <span class="meta">Nothing is ever sent on your behalf. Every kit is a file you send yourself.</span>
  </div>
</footer>

<style>
  .sr-only {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
    white-space: nowrap;
  }

  .masthead {
    position: sticky;
    top: 0;
    z-index: 40;
    background: var(--paper);
    border-bottom: var(--rule-heavy);
  }

  .masthead-inner {
    display: flex;
    align-items: center;
    gap: var(--s4);
    padding: var(--s3) var(--s4);
  }

  .nameplate {
    text-decoration: none;
    display: flex;
    flex-direction: column;
    line-height: 1;
  }

  .wordmark {
    font-family: var(--font-display);
    font-weight: 900;
    font-size: 1.75rem;
    letter-spacing: -0.04em;
  }

  .tagline {
    font-family: var(--font-mono);
    font-size: 0.625rem;
    letter-spacing: 0.18em;
    text-transform: uppercase;
    color: var(--accent);
    margin-top: 2px;
  }

  .edition {
    margin-left: auto;
    text-align: right;
    display: none;
  }

  .offline {
    color: var(--accent);
  }

  .nav {
    border-top: var(--hair);
  }

  .nav-inner {
    display: flex;
    overflow-x: auto;
  }

  .nav-link {
    display: inline-flex;
    align-items: center;
    gap: var(--s2);
    padding: var(--s3) var(--s4);
    min-height: var(--tap);
    font-family: var(--font-ui);
    font-size: 0.75rem;
    letter-spacing: 0.14em;
    text-transform: uppercase;
    color: var(--ink);
    text-decoration: none;
    border-right: var(--hair-soft);
    white-space: nowrap;
    transition: background-color 200ms ease-out, color 200ms ease-out;
  }

  .nav-link:hover {
    background: var(--ink);
    color: var(--paper);
  }

  .nav-link.active {
    background: var(--ink);
    color: var(--paper);
  }

  .nav-badge {
    border-color: currentColor;
  }

  .nav-toggle {
    display: none;
    margin-left: var(--s2);
  }

  .ticker-item {
    display: inline-flex;
    gap: var(--s2);
  }

  .tick-accent {
    color: var(--accent);
    font-weight: 700;
  }

  .banner-slot {
    margin-top: var(--s4);
  }

  .main {
    padding-top: var(--s6);
    padding-bottom: var(--s8);
    min-height: 60vh;
  }

  .loading {
    font-family: var(--font-mono);
    letter-spacing: 0.18em;
    text-transform: uppercase;
  }

  .key-row {
    display: flex;
    gap: var(--s3);
    margin-top: var(--s3);
  }

  .footer {
    border-top: var(--rule-heavy);
    background: var(--paper);
  }

  .footer-inner {
    display: grid;
    grid-template-columns: 2fr 1fr 1fr;
  }

  .col {
    padding: var(--s5) var(--s4);
    border-right: var(--hair-soft);
    display: flex;
    flex-direction: column;
    gap: var(--s2);
    min-width: 0;
  }

  .col:last-child {
    border-right: none;
  }

  .col a {
    font-family: var(--font-ui);
    font-size: 0.8125rem;
  }

  .footer-title {
    font-size: 1.25rem;
    font-weight: 900;
    letter-spacing: -0.03em;
  }

  .footer .meta {
    margin: 0;
  }

  .footer-strip {
    border-top: var(--hair-soft);
    padding: var(--s3) var(--s4);
  }

  @media (max-width: 767px) {
    .edition {
      display: none;
    }

    .nav-toggle {
      display: inline-flex;
    }

    .nav {
      display: none;
    }

    .nav.open {
      display: block;
    }

    .nav-inner {
      flex-direction: column;
      padding: 0;
    }

    .nav-link {
      border-right: none;
      border-bottom: var(--hair-soft);
    }

    .footer-inner {
      grid-template-columns: 1fr;
    }

    .col {
      border-right: none;
      border-bottom: var(--hair-soft);
      padding: var(--s4) var(--s3);
    }
  }
</style>