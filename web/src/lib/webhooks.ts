/**
 * The rules the webhook editor applies before it saves.
 *
 * This mirrors `WebhookService.Save` exactly, and that duplication is deliberate: the editor has to
 * be able to tell you that "data eng" will be dropped while you are still typing, which is only
 * possible before anything is stored. The cost of duplicating a rule is that the two copies drift,
 * so the parity is pinned by tests here and there rather than left to review.
 */

export interface MentionRule {
  /** The mention as it will actually be stored and sent. */
  kept: string[];
  /** Entries the user typed that will be silently discarded, and why. */
  dropped: string[];
}

/**
 * One mention per comma-separated entry: trimmed, stripped of a leading @, rejected if it contains
 * whitespace, de-duplicated case-insensitively.
 *
 * A mention containing a space is dropped rather than repaired. Every receiver treats an @handle as
 * a single token, so "data eng" would arrive as two broken mentions and notify nobody — and quietly
 * rewriting it to "dataeng" would guess at a name the user did not choose. Two-word roles are
 * written dataeng or data-engineers.
 */
export function cleanMentions(raw: string): MentionRule {
  const kept: string[] = [];
  const dropped: string[] = [];
  const seen = new Set<string>();

  for (const part of raw.split(',')) {
    const mention = part.trim().replace(/^@+/, '').trim();
    if (mention.length === 0) continue;

    if (/\s/.test(mention)) {
      dropped.push(mention);
      continue;
    }

    const key = mention.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    kept.push(mention);
  }

  return { kept, dropped };
}

/** How the editor renders the kept mentions back to the user, e.g. "@jobseekers @dataeng". */
export function renderMentions(kept: readonly string[]): string {
  return kept.map((m) => '@' + m).join(' ');
}