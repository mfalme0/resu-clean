import { describe, expect, it, vi } from 'vitest';
import { routes, currentPath, onLinkClick } from '../lib/router';
import { hasApiKey, setApiKey, ApiError, waitForOp, type OpStatus } from '../lib/api';

describe('route table', () => {
  it('covers every screen the brief asks for', () => {
    expect(routes.map((r) => r.title)).toEqual(['Resume', 'Profile', 'Jobs', 'Kits', 'Tracker', 'Models']);
    expect(routes.map((r) => r.path)).toEqual(['/', '/profile', '/jobs', '/kits', '/tracker', '/models']);
  });

  it('lazily loads every screen', () => {
    for (const route of routes) expect(typeof route.load).toBe('function');
  });
});

describe('currentPath', () => {
  it('falls back to the resume screen for unknown paths', () => {
    window.history.pushState({}, '', '/nowhere');
    expect(currentPath()).toBe('/');
  });

  it('resolves a known path and ignores a trailing slash', () => {
    window.history.pushState({}, '', '/jobs/');
    expect(currentPath()).toBe('/jobs');
    window.history.pushState({}, '', '/');
  });
});

describe('link interception', () => {
  function anchor(attrs: Record<string, string> = {}): HTMLAnchorElement {
    const a = document.createElement('a');
    a.href = '/profile';
    for (const [k, v] of Object.entries(attrs)) a.setAttribute(k, v);
    document.body.appendChild(a);
    return a;
  }

  function clickOn(a: HTMLAnchorElement, init: MouseEventInit = {}) {
    const event = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0, ...init });
    Object.defineProperty(event, 'target', { value: a });
    return event;
  }

  it('intercepts a plain internal left click', () => {
    const a = anchor();
    let captured: string | null = null;
    const event = clickOn(a);
    onLinkClick((p) => (captured = p))(event);
    expect(captured).toBe('/profile');
    expect(event.defaultPrevented).toBe(true);
  });

  it('leaves modified clicks alone so the browser handles them', () => {
    const a = anchor();
    let called = false;
    onLinkClick(() => (called = true))(clickOn(a, { metaKey: true }));
    expect(called).toBe(false);
  });

  it('leaves external and download links alone', () => {
    let called = false;
    const handler = onLinkClick(() => (called = true));

    const external = anchor({ href: 'https://example.org', target: '_blank' });
    handler(clickOn(external));
    expect(called).toBe(false);

    const download = anchor({ href: '/api/kits/kit_1/download/zip', download: '' });
    handler(clickOn(download));
    expect(called).toBe(false);
  });
});

describe('api key handling', () => {
  it('round-trips the optional key through local storage', () => {
    setApiKey('test-key');
    expect(hasApiKey()).toBe(true);
    setApiKey('');
    expect(hasApiKey()).toBe(false);
  });
});

describe('error shape', () => {
  it('exposes the server error code and message', () => {
    const error = new ApiError(422, 'unprocessable', 'Nothing was changed.', { field: 'instruction' });
    expect(error.status).toBe(422);
    expect(error.code).toBe('unprocessable');
    expect(error.message).toBe('Nothing was changed.');
    expect(error.details).toEqual({ field: 'instruction' });
    expect(error).toBeInstanceOf(Error);
  });
});

describe('op polling', () => {
  it('resolves once the operation is done', async () => {
    const states: OpStatus<{ ok: boolean }>[] = [
      { opId: 'op_1', kind: 'clean', state: 'queued', progress: 10, message: null, result: null, error: null, createdAt: '', finishedAt: null },
      { opId: 'op_1', kind: 'clean', state: 'done', progress: 100, message: null, result: { ok: true }, error: null, createdAt: '', finishedAt: null }
    ];
    let call = 0;

    vi.stubGlobal('fetch', async () => {
      const body = JSON.stringify(states[call++]);
      return new Response(body, { status: 200, headers: { 'Content-Type': 'application/json' } });
    });

    const seen: string[] = [];
    const result = await waitForOp<{ ok: boolean }>('op_1', (s) => seen.push(s.state), { intervalMs: 1 });

    expect(result).toEqual({ ok: true });
    expect(seen).toEqual(['queued', 'done']);
    vi.unstubAllGlobals();
  });

  it('rejects with the server message when the operation failed', async () => {
    vi.stubGlobal(
      'fetch',
      async () =>
        new Response(
          JSON.stringify({
            opId: 'op_2',
            kind: 'find_jobs',
            state: 'failed',
            progress: 100,
            message: null,
            result: null,
            error: 'robots.txt disallows /jobs',
            createdAt: '',
            finishedAt: null
          }),
          { status: 200, headers: { 'Content-Type': 'application/json' } }
        )
    );

    await expect(waitForOp('op_2', undefined, { intervalMs: 1 })).rejects.toThrow(/robots\.txt/);
    vi.unstubAllGlobals();
  });
});