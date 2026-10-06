/**
 * Thin typed fetch wrapper for the resu-clean API.
 *
 * One place that knows about: the base path, the optional X-API-Key header, the
 * standard error envelope, and the 202 + opId polling pattern.
 */

const BASE = '/api';

let apiKey = '';
try {
  apiKey = localStorage.getItem('resu-clean.apiKey') ?? '';
} catch {
  apiKey = '';
}

/** Called once at startup when the user supplies a key (auth is optional). */
export function setApiKey(key: string): void {
  apiKey = key.trim();
  try {
    if (apiKey) localStorage.setItem('resu-clean.apiKey', apiKey);
    else localStorage.removeItem('resu-clean.apiKey');
  } catch {
    /* private browsing: the key simply is not remembered */
  }
}

export function hasApiKey(): boolean {
  return apiKey.length > 0;
}

export class ApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly details: unknown;

  constructor(status: number, code: string, message: string, details?: unknown) {
    super(message);
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

function headers(extra: Record<string, string> = {}): Record<string, string> {
  const out: Record<string, string> = { ...extra };
  if (apiKey) out['X-API-Key'] = apiKey;
  return out;
}

async function parse<T>(res: Response): Promise<T> {
  const text = await res.text();
  let body: unknown = null;
  if (text.length > 0) {
    try {
      body = JSON.parse(text);
    } catch {
      body = text;
    }
  }

  if (!res.ok) {
    const envelope = body as { error?: { code?: string; message?: string; details?: unknown } };
    throw new ApiError(
      res.status,
      envelope?.error?.code ?? 'unknown',
      envelope?.error?.message ?? `Request failed with status ${res.status}.`,
      envelope?.error?.details
    );
  }
  return body as T;
}

function qs(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === '') continue;
    search.set(key, String(value));
  }
  const text = search.toString();
  return text.length > 0 ? `?${text}` : '';
}

export async function get<T>(path: string, params: Record<string, string | number | boolean | undefined | null> = {}): Promise<T> {
  const res = await fetch(`${BASE}${path}${qs(params)}`, { headers: headers() });
  return parse<T>(res);
}

export async function post<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: headers(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  return parse<T>(res);
}

export async function put<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: 'PUT',
    headers: headers(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  return parse<T>(res);
}

export async function patch<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: 'PATCH',
    headers: headers(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  return parse<T>(res);
}

export async function del<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { method: 'DELETE', headers: headers() });
  return parse<T>(res);
}

/** Namespaced facade so callers can write `api.get(...)` without importing each helper. */
export const api = { get, post, put, patch, del, upload, waitForOp, hasApiKey, setApiKey };

/** Multipart upload for resume intake. */
export async function upload<T>(path: string, form: FormData): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { method: 'POST', headers: headers(), body: form });
  return parse<T>(res);
}

// ---- Types mirrored from the backend DTOs ----------------------------------

export interface Paged<T> {
  items: T[];
  page: number;
  size: number;
  total: number;
  pages: number;
  hasMore: boolean;
}

export interface ErrorBody {
  code: string;
  message: string;
  details?: unknown;
}

export interface OpAccepted {
  opId: string;
  status: string;
  kind: string;
}

export interface OpStatus<T = unknown> {
  opId: string;
  kind: string;
  state: 'queued' | 'running' | 'done' | 'failed';
  progress: number;
  message: string | null;
  result: T | null;
  error: string | null;
  createdAt: string;
  finishedAt: string | null;
}

export interface ResumeSummary {
  id: string;
  label: string;
  createdAt: string;
  updatedAt: string;
  currentVersionId: string | null;
  versionCount: number;
  bestFitVersionId: string | null;
}

export interface ResumeVersion {
  id: string;
  resumeId: string;
  label: string;
  sourceKind: string;
  parentId: string | null;
  bestFit: boolean;
  createdAt: string;
  charCount: number;
  hasDocx: boolean;
  hasPdf: boolean;
}

export interface ResumeDetail {
  summary: ResumeSummary;
  versions: ResumeVersion[];
}

export interface CleanChange {
  kind: string;
  detail: string;
  count: number;
}

export interface CleanResult {
  versionId: string;
  text: string;
  usedModel: boolean;
  model: string | null;
  changes: CleanChange[];
  warnings: string[];
}

export interface AtsCheck {
  id: string;
  label: string;
  category: string;
  passed: boolean;
  detail: string;
  tip: string;
  weight: number;
}

export interface KeywordMatch {
  matchPct: number;
  matchedCount: number;
  missingCount: number;
  matched: string[];
  missing: string[];
  extra: string[];
}

export interface AtsReport {
  versionId: string;
  score: number;
  verdict: string;
  usedModel: boolean;
  model: string | null;
  checks: AtsCheck[];
  keywords: KeywordMatch | null;
  prioritizedFixes: string[];
  wordCount: number;
  lineCount: number;
}

export interface PendingFact {
  id: string;
  kind: string;
  text: string;
  meta: Record<string, string>;
  origin: string;
  context: string;
  status: string;
  createdAt: string;
  decidedAt: string | null;
}

export interface UpdateResult {
  versionId: string | null;
  text: string;
  usedModel: boolean;
  model: string | null;
  pending: PendingFact[];
  blockedByPending: boolean;
  notes: string[];
}

export interface Profile {
  voice: string;
  preferences: string;
  contact: Record<string, string>;
  updatedAt: string;
}

export interface ProfileFact {
  id: string;
  kind: string;
  text: string;
  meta: Record<string, string>;
  origin: string;
  verifiedAt: string;
}

export interface Source {
  id: string;
  name: string;
  type: 'rss' | 'atom' | 'json' | 'html' | 'link';
  url: string | null;
  urlTemplate: string | null;
  regions: string[];
  enabled: boolean;
  rateLimitMs: number;
  fieldMap: Record<string, string>;
  selectors: Record<string, string>;
  requiresKey: string;
  notes: string;
  createdAt: string;
}

export interface PackEntry {
  key: string;
  name: string;
  type: string;
  url: string | null;
  urlTemplate: string | null;
  regions: string[];
  enabledByDefault: boolean;
  fieldMap: Record<string, string>;
  selectors: Record<string, string>;
  requiresKey: string;
  notes: string;
}

export interface SourcePack {
  id: string;
  name: string;
  description: string;
  installed: boolean;
  entries: PackEntry[];
}

export interface PreviewItem {
  title: string;
  company: string;
  location: string;
  url: string;
  postedAt: string;
  description: string;
  matchPct: number;
}

export interface DetectResult {
  url: string;
  suggestedType: string;
  confidence: string;
  feedUrl: string | null;
  candidates: string[];
  preview: PreviewItem[];
  notes: string[];
  ok: boolean;
  error: string | null;
}

export interface TestResult {
  ok: boolean;
  type: string;
  count: number;
  items: PreviewItem[];
  elapsedMs: number;
  fromCache: boolean;
  error: string | null;
  robots: string;
}

export interface Job {
  id: string;
  title: string;
  company: string;
  location: string;
  url: string;
  postedAt: string | null;
  description: string;
  sourceId: string;
  sourceName: string;
  matchPct: number;
  remote: boolean;
  isManual: boolean;
  bestVersionId: string | null;
  createdAt: string;
}

export interface SearchSummary {
  runId: string;
  query: string;
  location: string;
  remoteOnly: boolean;
  fetched: number;
  afterDedupe: number;
  saved: number;
  sourcesUsed: string[];
  sourcesSkipped: string[];
  errors: { source: string; message: string }[];
  resumeVersionId: string | null;
}

export interface SearchOutcome {
  summary: SearchSummary;
  jobs: Job[];
}

export interface Kit {
  id: string;
  jobId: string;
  jobTitle: string;
  company: string;
  versionId: string;
  versionLabel: string;
  subject: string;
  body: string;
  coverLetter: string;
  toAddress: string;
  applyUrl: string;
  generatedBy: string;
  createdAt: string;
  wordCount: number;
  factsUsed: string[];
  attachments: string[];
  mailtoLink: string;
  readyToSend: boolean;
}

export interface TrackerEntry {
  id: string;
  jobId: string;
  jobTitle: string;
  company: string;
  kitId: string;
  resumeVersionId: string;
  resumeLabel: string;
  status: 'prepared' | 'sent' | 'replied' | 'interview' | 'rejected';
  notes: string;
  createdAt: string;
  updatedAt: string;
}

export interface Provider {
  id: string;
  name: string;
  type: 'anthropic' | 'openai';
  baseUrl: string;
  envVar: string;
  hasKey: boolean;
  keySource: string;
  models: string[];
  enabled: boolean;
  createdAt: string;
  reachable: boolean;
}

/** A built-in provider template: base URL, key hint and env var, ready to fill in. */
export interface ProviderPreset {
  id: string;
  name: string;
  type: 'anthropic' | 'openai';
  baseUrl: string;
  envVar: string;
  keyless: boolean;
  keyHint: string;
  docsUrl: string;
  notes: string;
  suggestedModels: string[];
}

export interface DiscoverModelsResult {
  ok: boolean;
  models: string[];
  source: string;
  elapsedMs: number;
  error: string | null;
  hint: string | null;
}

export interface RouteEntry {
  ordinal: number;
  providerId: string;
  providerName: string;
  model: string;
}

export interface Route {
  workflow: string;
  entries: RouteEntry[];
  activeModel: string | null;
  activeProvider: string | null;
}

export interface Capabilities {
  anyModelConfigured: boolean;
  needsModel: Record<string, boolean>;
  availableWorkflows: string[];
  authMode: string;
  maxUploadMb: number;
  version: string;
}

export interface MemMetrics {
  rssMb: number;
  managedHeapMb: number;
  peakRssMb: number;
  uptimeSeconds: number;
  requests: number;
  opsActive: number;
  threads: number;
}

export interface Health {
  status: string;
  version: string;
  uptimeSeconds: number;
}

// ---- Polling for long operations -------------------------------------------

const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Polls /api/ops/{id} until the operation finishes. `onTick` fires on every poll so the UI
 * can show progress. Rejects with an ApiError when the operation itself failed.
 */
export async function waitForOp<T>(
  opId: string,
  onTick?: (status: OpStatus<T>) => void,
  opts: { intervalMs?: number; timeoutMs?: number } = {}
): Promise<T> {
  const interval = opts.intervalMs ?? 600;
  const deadline = Date.now() + (opts.timeoutMs ?? 10 * 60 * 1000);

  for (;;) {
    const status = await get<OpStatus<T>>(`/ops/${opId}`);
    onTick?.(status);
    if (status.state === 'done') {
      if (status.result === null) throw new ApiError(500, 'empty_result', 'The operation finished but returned no result.');
      return status.result;
    }
    if (status.state === 'failed') {
      throw new ApiError(500, 'op_failed', status.error ?? 'The operation failed. See the server log for details.');
    }
    if (Date.now() > deadline) {
      throw new ApiError(408, 'timeout', 'This took longer than expected. It is still running in the background.');
    }
    await sleep(interval);
  }
}