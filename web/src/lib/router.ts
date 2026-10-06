/**
 * Tiny client-side router. Routes are lazily imported so the initial bundle only
 * carries the Resume screen; every other screen is a separate chunk.
 */

export interface RouteDef {
  path: string;
  title: string;
  load: () => Promise<{ default: unknown }>;
}

export const routes: RouteDef[] = [
  { path: '/', title: 'Resume', load: () => import('../routes/Resume.svelte') },
  { path: '/profile', title: 'Profile', load: () => import('../routes/Profile.svelte') },
  { path: '/jobs', title: 'Jobs', load: () => import('../routes/Jobs.svelte') },
  { path: '/kits', title: 'Kits', load: () => import('../routes/Kits.svelte') },
  { path: '/tracker', title: 'Tracker', load: () => import('../routes/Tracker.svelte') },
  { path: '/models', title: 'Models', load: () => import('../routes/Models.svelte') }
];

export function currentPath(): string {
  const path = window.location.pathname;
  const normalised = path.length > 1 ? path.replace(/\/+$/, '') : path;
  return routes.some((r) => r.path === normalised) ? normalised : '/';
}

export function navigate(path: string): void {
  if (path === currentPath()) return;
  window.history.pushState({}, '', path);
  window.dispatchEvent(new PopStateEvent('popstate'));
}

/** Intercept clicks on same-origin anchors so navigation stays in the SPA. */
export function onLinkClick(handler: (path: string) => void): (event: MouseEvent) => void {
  return (event: MouseEvent) => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    const anchor = (event.target as HTMLElement | null)?.closest('a');
    if (!anchor) return;
    const href = anchor.getAttribute('href');
    if (!href || !href.startsWith('/') || anchor.target === '_blank' || anchor.hasAttribute('download')) return;
    event.preventDefault();
    handler(href);
  };
}