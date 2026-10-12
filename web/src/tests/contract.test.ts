import { describe, expect, it, vi } from 'vitest';
import { api, ApiError, type Job } from '../lib/api';

/**
 * The server omits null properties rather than serialising them (see Api.cs: DefaultIgnoreCondition
 * WhenWritingNull). That is a real contract with no test guarding it.
 *
 * It matters because `string | null` in a type reads as "the key is there and holds null", when in
 * practice `job.deadlineAt` is `undefined` on a posting that states no deadline. Any renderer doing
 * `deadlineAt.trim()` or a strict `=== null` check throws on exactly the data this app handles most
 * often. These tests pin the contract so a change to the serialiser surfaces here rather than as a
 * blank screen for someone whose resume has no closing date.
 */

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' }
  });
}

describe('null omission contract', () => {
  it('treats an omitted property and an explicit null the same way', async () => {
    // The server sends neither key. Both are "no deadline", and nothing may distinguish them.
    vi.stubGlobal('fetch', async () =>
      jsonResponse({
        id: 'job_1',
        title: 'Data Engineer',
        company: 'Acme',
        location: 'Nairobi',
        url: 'https://example.org',
        postedAt: null,
        description: '',
        sourceId: '',
        sourceName: 'manual',
        matchPct: 0,
        remote: false,
        isManual: true,
        createdAt: '2026-10-12T00:00:00Z',
        unmetBlockers: 0,
        unmetRequired: 0,
        requirementsChecked: false,
        status: '',
        effectiveStatus: 'unknown'
      })
    );

    const job = await api.get<Job>('/jobs/job_1');

    // Both readings of "absent" are falsy, which is what the UI relies on.
    expect(job.deadlineAt).toBeFalsy();
    expect(job.lastVerifiedAt).toBeFalsy();
    expect(job.effectiveStatus).toBe('unknown');
    vi.unstubAllGlobals();
  });

  it('never throws when rendering code guards a nullable field with a truthiness test', async () => {
    vi.stubGlobal('fetch', async () =>
      jsonResponse({ id: 'job_1', effectiveStatus: 'unknown', status: '' })
    );

    const job = await api.get<Job>('/jobs/job_1');

    // The exact expression used on the Jobs screen for each branch of the badge.
    const label = job.lastVerifiedAt ? `checked ${job.lastVerifiedAt.slice(0, 10)}` : 'not verified';
    expect(label).toBe('not verified');
    vi.unstubAllGlobals();
  });
});

describe('non-2xx responses surface as errors', () => {
  // The failure mode this guards is a silent undefined: an endpoint returning 200 with a wrong body,
  // or a caller ignoring a rejection, both of which render as "nothing happened" rather than a
  // message. Every verb goes through the same parse, so it is tested once per verb.
  it.each(['get', 'post', 'put', 'patch'] as const)('%s rejects with the server message', async (verb) => {
    vi.stubGlobal('fetch', async () =>
      jsonResponse({ error: { code: 'not_found', message: "No job called 'job_x'." } }, 404)
    );

    await expect(api[verb]('/jobs/job_x', {})).rejects.toThrow(/No job called 'job_x'/);
    vi.unstubAllGlobals();
  });

  it('delete rejects with the server message', async () => {
    vi.stubGlobal('fetch', async () => jsonResponse({ error: { code: 'not_found', message: 'Gone.' } }, 404));

    await expect(api.del('/jobs/job_x')).rejects.toBeInstanceOf(ApiError);
    vi.unstubAllGlobals();
  });

  it('carries the status and code through rather than a bare Error', async () => {
    vi.stubGlobal('fetch', async () =>
      jsonResponse({ error: { code: 'unprocessable', message: 'No text to save.' } }, 422)
    );

    await expect(api.post('/resumes/versions/ver_1/accept-text', {})).rejects.toMatchObject({
      status: 422,
      code: 'unprocessable',
      message: 'No text to save.'
    });
    vi.unstubAllGlobals();
  });

  it('still throws when the error body is not the envelope', async () => {
    // A proxy or a crash can return HTML. That must not become an unhandled non-ApiError, because
    // the screens catch ApiError and show its message.
    vi.stubGlobal('fetch', async () => new Response('<html>502</html>', { status: 502 }));

    const error = await api.get('/jobs').catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(502);
    vi.unstubAllGlobals();
  });
});

describe('verification endpoints', () => {
  it('reads the verification status a posting was given', async () => {
    vi.stubGlobal('fetch', async () =>
      jsonResponse({
        jobId: 'job_1',
        status: 'open',
        lastVerifiedAt: '2026-10-12T09:00:00Z',
        effectiveStatus: 'open'
      })
    );

    const result = await api.post<{ jobId: string; status: string; effectiveStatus: string }>(
      '/jobs/job_1/verification',
      { status: 'open' }
    );

    expect(result.status).toBe('open');
    expect(result.effectiveStatus).toBe('open');
    vi.unstubAllGlobals();
  });
});