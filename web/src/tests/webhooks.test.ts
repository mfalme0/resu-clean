import { describe, expect, it } from 'vitest';
import { cleanMentions, renderMentions } from '../lib/webhooks';

/**
 * The mention rules, duplicated from `WebhookService.Save` so the editor can tell you what will be
 * discarded before anything is stored.
 *
 * These cases are deliberately the same ones pinned in `WebhookDigestTests` on the server. If the
 * two implementations ever disagree, the editor starts promising something the API will not do —
 * which is worse than either rule being wrong alone, because it looks correct right up until the
 * message arrives with no mention on it.
 */
describe('cleanMentions', () => {
  it('keeps a single mention as typed', () => {
    expect(cleanMentions('jobseekers').kept).toEqual(['jobseekers']);
  });

  it('reads several mentions from one comma-separated field', () => {
    expect(cleanMentions('jobseekers, dataeng').kept).toEqual(['jobseekers', 'dataeng']);
  });

  it('strips a stray @ and surrounding whitespace', () => {
    expect(cleanMentions('@jobseekers,   dataeng  ').kept).toEqual(['jobseekers', 'dataeng']);
  });

  it('drops empty entries instead of storing a blank mention', () => {
    expect(cleanMentions('jobseekers, ,  , dataeng').kept).toEqual(['jobseekers', 'dataeng']);
  });

  it('de-duplicates case-insensitively, keeping the first spelling', () => {
    expect(cleanMentions('dataeng, DataEng, DATAENG').kept).toEqual(['dataeng']);
  });

  it('drops a two-word mention rather than guessing a repair for it', () => {
    const result = cleanMentions('jobseekers, data eng');
    expect(result.kept).toEqual(['jobseekers']);
    // And says so, rather than discarding it where the user cannot see.
    expect(result.dropped).toEqual(['data eng']);
  });

  it('drops a tab-separated pair for the same reason a space-separated one is dropped', () => {
    const result = cleanMentions('data\teng');
    expect(result.kept).toEqual([]);
    // Reported back exactly as typed, trimmed and de-@-ed but otherwise untouched, so the user
    // recognises it in the warning rather than wondering what they wrote.
    expect(result.dropped).toEqual(['data\teng']);
  });

  it('keeps the punctuation a real handle can contain', () => {
    expect(cleanMentions('data-eng, data.engine, ops_team').kept).toEqual([
      'data-eng',
      'data.engine',
      'ops_team'
    ]);
  });

  it('handles an empty field without inventing anything', () => {
    expect(cleanMentions('')).toEqual({ kept: [], dropped: [] });
    expect(cleanMentions('   ')).toEqual({ kept: [], dropped: [] });
  });
});

describe('renderMentions', () => {
  it('prepends one @ per mention, in the order given', () => {
    expect(renderMentions(['jobseekers', 'dataeng'])).toBe('@jobseekers @dataeng');
  });

  it('renders nothing for no mentions rather than a bare @', () => {
    expect(renderMentions([])).toBe('');
  });
});