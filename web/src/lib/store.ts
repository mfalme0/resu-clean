/**
 * Shared UI state. Small and explicit: a handful of writable stores plus the
 * global banner for errors that are not tied to one screen.
 */
import { writable, derived, type Readable } from 'svelte/store';
import * as api from './api';
import type { Capabilities, Health, MemMetrics } from './api';

export const pendingCount = writable(0);
export const capabilities = writable<Capabilities | null>(null);
export const memory = writable<MemMetrics | null>(null);

export const banner = writable<{ tone: 'error' | 'info'; text: string } | null>(null);

export function flash(text: string, tone: 'error' | 'info' = 'info'): void {
  banner.set({ tone, text });
  setTimeout(() => banner.set(null), tone === 'error' ? 9000 : 4000);
}

export const modelConfigured: Readable<boolean> = derived(capabilities, ($c) => $c?.anyModelConfigured ?? false);

/** True when the named feature has a working model route behind it. */
export function needsModel(feature: string): Readable<boolean> {
  return derived(capabilities, ($c) => $c?.needsModel?.[feature] === false);
}

/** Polls the backend. Health on load, memory on the mem screen and the nav footer. */
export async function loadSystem(): Promise<void> {
  try {
    capabilities.set(await api.get<Capabilities>('/capabilities'));
  } catch (err) {
    flash(err instanceof Error ? err.message : 'Could not reach the backend.', 'error');
  }
}

export async function refreshPendingCount(): Promise<void> {
  try {
    const result = await api.get<{ count: number }>('/profile/pending/count');
    pendingCount.set(result.count);
  } catch {
    pendingCount.set(0);
  }
}

export async function refreshMemory(): Promise<void> {
  try {
    memory.set(await api.get<MemMetrics>('/metrics/mem'));
  } catch {
    memory.set(null);
  }
}

export async function health(): Promise<Health | null> {
  try {
    return await api.get<Health>('/health');
  } catch {
    return null;
  }
}