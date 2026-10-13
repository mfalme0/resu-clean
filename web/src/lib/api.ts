/**
 * Thin typed fetch wrapper for the resu-clean API.
 *
 * One place that knows about: the base path, the optional X-API-Key header, the
 * standard error envelope, and the 202 + opId polling pattern.
 *
 * One convention worth knowing before reading the types below: the server omits null
 * properties rather than serialising them, so `string | null` in a type here means
 * "absent when there is nothing". Every consumer either tests truthiness or renders
 * the value directly, so the two behave identically — but nothing may rely on the key
 * being present.
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

/**
 * How a model rewrite was judged. The rejected text comes back with it, because a gate that only
 * says "no" leaves the user with nothing to do about it.
 */
export interface RewriteOutcome {
  accepted: boolean;
  baselineScore: number;
  candidateScore: number;
  baselineMissing: number;
  candidateMissing: number;
  reason: string;
  rejectedText: string | null;
}

export interface CleanResult {
  versionId: string;
  text: string;
  usedModel: boolean;
  model: string | null;
  changes: CleanChange[];
  warnings: string[];
  rewrite: RewriteOutcome | null;
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

// ---- Grounding ---------------------------------------------------------

/** One claim the resume makes, and whether a verified fact backs it. */
export interface FactClaim {
  text: string;
  category: string;
  factIds: string[];
  grounded: boolean;
  note: string;
}

export interface FactCheckReport {
  versionId: string;
  checked: number;
  grounded: number;
  ungrounded: number;
  claims: FactClaim[];
  usedModel: boolean;
  model: string | null;
  /** "model" or "deterministic", so the screen never implies more was checked than was. */
  method: 'model' | 'deterministic';
  queued: PendingFact[];
}

/** One approved extra keyword. Added only through the approval queue, never by the tool itself. */
export interface SkillTerm {
  id: string;
  term: string;
  normalized: string;
  occurrences: number;
  origin: string;
  createdAt: string;
}

export interface ProposeDictionaryResult {
  queued: PendingFact[];
  proposed: number;
}

/** One model rewrite, measured before it was shown and kept or discarded after. */
export interface RewriteEvaluation {
  id: string;
  workflow: string;
  model: string;
  baselineScore: number;
  candidateScore: number;
  baselineMissing: number;
  candidateMissing: number;
  accepted: boolean;
  reason: string;
  createdAt: string;
}

/** Per workflow and model: how often a rewrite survived its own gate. */
export interface RewriteLedgerRow {
  workflow: string;
  model: string;
  total: number;
  accepted: number;
  avgDelta: number;
}

export interface RewriteLedger {
  rows: RewriteLedgerRow[];
  recent: RewriteEvaluation[];
}

export interface UpdateResult {
  versionId: string | null;
  text: string;
  usedModel: boolean;
  model: string | null;
  pending: PendingFact[];
  blockedByPending: boolean;
  notes: string[];
  rewrite: RewriteOutcome | null;
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
  /** Who is authoritative about a posting from this source still existing. Absent means unknown. */
  provenance?: Provenance;
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
  provenance?: Provenance;
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
  /** From the host. Absent when the server predates the field, which reads as unknown. */
  provenance?: Provenance;
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
  /** Blockers the posting states that nothing in your profile covers. */
  unmetBlockers: number;
  unmetRequired: number;
  /** False until the posting has been read. Unchecked is not the same as clear. */
  requirementsChecked: boolean;
  /** When this was last confirmed open. Absent means never confirmed, which is not the same as open. */
  lastVerifiedAt?: string;
  /** A closing date the posting states. Absent means it states none. */
  deadlineAt?: string;
  /** The recorded outcome of the last check: open, closed or unverifiable. */
  status: string;
  /** What can be acted on today: open | closed | expired | unknown. */
  effectiveStatus: 'open' | 'closed' | 'expired' | 'unverifiable' | 'unknown';
}

export interface JobVerification {
  jobId: string;
  status: string;
  lastVerifiedAt?: string;
  deadlineAt?: string;
  effectiveStatus: string;
}

/**
 * Who is authoritative about a posting still existing.
 *
 * `employer_ats` is the employer's own board, and only that can return a meaningful 404. An
 * aggregator republishes listings it did not author and may serve one for weeks after the
 * employer's board dropped it, so a 200 there is not evidence of anything. `unknown` is a real
 * value rather than a placeholder: we could not place the host, so anything found there is
 * unverified.
 */
export type Provenance = 'employer_ats' | 'aggregator' | 'board' | 'unknown';

/** The one sentence each provenance is shown with, so the difference is never left implicit. */
export const PROVENANCE_NOTE: Record<Provenance, string> = {
  employer_ats: "The employer's own board. Only this can tell you the posting is gone.",
  aggregator:
    'A third-party index. A listing here can outlive the posting on the employer’s own board, so it is not evidence the role is open.',
  board: 'A general listings site. The employer’s own posting is elsewhere and may already be closed.',
  unknown: 'Unknown where this came from, so treat it as unverified.'
};

/** Narrowing an untrusted server value rather than trusting it into a union type. */
export function asProvenance(value: unknown): Provenance {
  return value === 'employer_ats' || value === 'aggregator' || value === 'board' ? value : 'unknown';
}

export interface JobRequirement {
  text: string;
  class: 'blocker' | 'required' | 'preferred' | 'implicit';
  met: boolean;
  missing: string[];
  evidence: string | null;
}

export interface JobRequirements {
  jobId: string;
  requirements: JobRequirement[];
  unmetBlockers: number;
  unmetRequired: number;
  usedModel: boolean;
  model: string | null;
  method: 'model' | 'deterministic' | 'stored';
}

export interface JobFlag {
  kind: string;
  severity: 'info' | 'warn';
  detail: string;
  /** The posting's own words, when the flag is a judgement. Null for pure arithmetic. */
  quote: string | null;
}

export interface JobQuality {
  jobId: string;
  flags: JobFlag[];
  hasWarnings: boolean;
  usedModel: boolean;
  method: 'model' | 'deterministic';
}

export interface GapSuggestion {
  text: string;
  /** The verified fact this builds on. Never empty: an uncited suggestion is discarded server-side. */
  buildsOnFactId: string;
  buildsOnFactText: string;
  why: string;
}

export interface GapRamp {
  requirement: string;
  class: string;
  missing: string[];
  suggestions: GapSuggestion[];
  /** The closest verified fact to the gap, whether or not a model was available. */
  nearestFact: string | null;
  /** False for gaps this week cannot close, such as a years requirement. */
  closable: boolean;
  usedModel: boolean;
  method: 'model' | 'deterministic';
}

export interface InterviewQuestion {
  text: string;
  /** What in the posting prompted this question. */
  probes: string;
  /** The resume or posting phrase it is anchored to. */
  groundedIn: string;
  risk: boolean;
  riskNote: string;
}

export interface InterviewPrep {
  jobId: string;
  versionId: string;
  questions: InterviewQuestion[];
  usedModel: boolean;
  model: string | null;
  method: 'model' | 'deterministic';
  /** Claims on the resume that nothing in the profile backs. Always present, model or not. */
  notes: string[];
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

export interface SearchHit {
  title: string;
  url: string;
  snippet: string;
  source: string;
}

export interface SearchAnswer {
  answer: string;
  results: SearchHit[];
  /** Which backend answered: native | api+model | api | none. */
  tier: string;
  usedModel: boolean;
  provider: string | null;
  model: string | null;
  notes: string[];
}

export interface SearchCapabilities {
  nativeConfigured: boolean;
  searchApiConfigured: boolean;
  searchApiName: string | null;
  canSearch: boolean;
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

/** What one tracker import did, including every entry it deliberately left alone. */
export interface TrackerImportResult {
  imported: number;
  created: number;
  updated: number;
  skipped: string[];
}

/** What the model calls have cost, per workflow and in total. */
export interface ModelUsageSummaryRow {
  workflow: string;
  calls: number;
  cachedCalls: number;
  tokens: number;
  avgMs: number;
}

export interface ModelUsage {
  byWorkflow: ModelUsageSummaryRow[];
  recent: {
    id: string;
    workflow: string;
    provider: string;
    model: string;
    promptTokens: number;
    completionTokens: number;
    cached: boolean;
    elapsedMs: number;
    createdAt: string;
  }[];
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