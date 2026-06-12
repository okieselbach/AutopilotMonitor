/**
 * Unit tests for the cross-session paginated fan-out used by search_events.
 * These are deterministic and run WITHOUT a backend token: fetchEventsViaIndex
 * takes an injectable page fetcher and clock, so we drive it with canned pages
 * instead of the live API.
 */
import { describe, it, expect } from 'vitest';
import {
  fetchEventsViaIndex,
  normalizeRawEvent,
  scoreEvent,
  queryHasProblemIntent,
  isProblemIntentKeyword,
  extractEventTypeCandidates,
  selectEventTypeCandidates,
  diversifyBySession,
  prioritizeFailureTypes,
  extractErrorCodeNeedles,
  computeKeywordSpecificity,
  orderBySelectivity,
  dedupeSynonymConcepts,
  entityRecallHint,
  resolveQueryKeywords,
} from '../tools/search.js';
import { scanLexical } from '../search-provider.js';
import type { SearchDocument } from '../search-provider.js';
import { isBenignHealthDetectionReport } from '../tools/shared.js';

describe('isBenignHealthDetectionReport', () => {
  const det = (over: Record<string, unknown> = {}) =>
    ({ scriptType: 'remediation', scriptPart: 'detection', complianceResult: 'True', ...over });

  it('true for a compliant remediation detection stamped script_failed', () => {
    expect(isBenignHealthDetectionReport('script_failed', det())).toBe(true);
  });
  it('true for a non-compliant detection too (verdict, not crash)', () => {
    expect(isBenignHealthDetectionReport('script_failed', det({ complianceResult: 'False', exitCode: '1' }))).toBe(true);
  });
  it('true for post-detection phase', () => {
    expect(isBenignHealthDetectionReport('script_failed', det({ scriptPart: 'post-detection' }))).toBe(true);
  });
  it('false when IME explicitly reported result=Failed (authoritative failure)', () => {
    expect(isBenignHealthDetectionReport('script_failed', det({ result: 'Failed' }))).toBe(false);
  });
  it('false for the remediation phase itself (a real crash there is a failure)', () => {
    expect(isBenignHealthDetectionReport('script_failed', det({ scriptPart: 'remediation' }))).toBe(false);
  });
  it('false for platform scripts', () => {
    expect(isBenignHealthDetectionReport('script_failed', { scriptType: 'platform', exitCode: '1' })).toBe(false);
  });
  it('false for non-failure event types (only guards mis-stamped script_failed rows)', () => {
    expect(isBenignHealthDetectionReport('script_completed', det())).toBe(false);
  });
  it('tolerates snake_case keys and missing data', () => {
    expect(isBenignHealthDetectionReport('script_failed', { script_type: 'remediation', script_part: 'detection' })).toBe(true);
    expect(isBenignHealthDetectionReport('script_failed', undefined)).toBe(false);
  });
});

type Page = { events?: Array<Record<string, unknown>>; nextLink?: string };

/** Build a fake page fetcher from a path -> page map; throws for unknown paths. */
function fakeFetcher(pages: Record<string, Page>): (path: string) => Promise<Page> {
  return async (path: string) => {
    if (!(path in pages)) throw new Error(`unexpected path: ${path}`);
    return pages[path];
  };
}

/** Monotonic fake clock: each call advances by `stepMs`. */
function fakeClock(startMs = 0, stepMs = 0): () => number {
  let t = startMs;
  return () => {
    const now = t;
    t += stepMs;
    return now;
  };
}

const BASE = '/api/raw/events';

// Keep the test's notion of "rare" in sync with the implementation gate (RARE_THRESHOLD = 3).
const RARE_THRESHOLD_FOR_TEST = 3;

describe('fetchEventsViaIndex', () => {
  it('drains a single event type across pages and reports truncated=false', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=app_install_failed&pageSize=200`]: {
        events: [{ sessionId: 's1', eventType: 'app_install_failed' }],
        nextLink: `${BASE}?pageSize=200&continuation=tok2&eventType=app_install_failed`,
      },
      [`${BASE}?pageSize=200&continuation=tok2&eventType=app_install_failed`]: {
        events: [{ sessionId: 's2', eventType: 'app_install_failed' }],
        // no nextLink → drained
      },
    };

    const res = await fetchEventsViaIndex(
      ['app_install_failed'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events).toHaveLength(2);
    expect(res.anySucceeded).toBe(true);
    expect(res.truncated).toBe(false);
    expect(res.events.map((e) => e._sessionId)).toEqual(['s1', 's2']);
  });

  it('merges multiple event types and never duplicates events across single-type walks', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=app_install_failed&pageSize=200`]: {
        events: [{ sessionId: 's1', eventType: 'app_install_failed' }],
      },
      [`${BASE}?eventType=error_detected&pageSize=200`]: {
        // same session, different event type — a distinct event, NOT a dup
        events: [{ sessionId: 's1', eventType: 'error_detected' }],
      },
    };

    const res = await fetchEventsViaIndex(
      ['app_install_failed', 'error_detected'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events).toHaveLength(2);
    expect(res.truncated).toBe(false);
    // Both belong to s1; the caller unions sessionIds into a Set downstream.
    expect(new Set(res.events.map((e) => e._sessionId))).toEqual(new Set(['s1']));
  });

  it('stops at maxPagesPerType and flags truncated=true (genuine recall gap)', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=log_entry&pageSize=200`]: {
        events: [{ sessionId: 's1', eventType: 'log_entry' }],
        nextLink: `${BASE}?pageSize=200&continuation=more&eventType=log_entry`,
      },
      // The 2nd page exists but the budget (1 page) stops us before fetching it.
      [`${BASE}?pageSize=200&continuation=more&eventType=log_entry`]: {
        events: [{ sessionId: 's2', eventType: 'log_entry' }],
      },
    };

    const res = await fetchEventsViaIndex(
      ['log_entry'], BASE, undefined,
      { maxPagesPerType: 1, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events).toHaveLength(1);
    expect(res.truncated).toBe(true);
    expect(res.anySucceeded).toBe(true);
  });

  it('stops at the wall-clock deadline and flags truncated=true', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=a&pageSize=200`]: { events: [{ sessionId: 's1' }] },
      [`${BASE}?eventType=b&pageSize=200`]: { events: [{ sessionId: 's2' }] },
    };

    // Clock ticks: #1 deadline-init=0 (deadline=150), #2 type 'a' top=100
    // (<=150, runs), #3 type 'b' top=200 (>150, cut off). Step 100, budget 150.
    const res = await fetchEventsViaIndex(
      ['a', 'b'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 150 },
      fakeFetcher(pages), fakeClock(0, 100),
    );

    expect(res.truncated).toBe(true);
    // First type ran; second was cut off by the deadline before fetching.
    expect(res.events).toHaveLength(1);
    expect(res.events[0]._sessionId).toBe('s1');
  });

  it('preserves accumulated events when a later type errors, sets truncated, keeps anySucceeded', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=good&pageSize=200`]: { events: [{ sessionId: 's1', eventType: 'good' }] },
      // 'bad' has no entry → fetcher throws for it.
    };

    const res = await fetchEventsViaIndex(
      ['good', 'bad'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events).toHaveLength(1); // 'good' preserved despite 'bad' failing
    expect(res.anySucceeded).toBe(true);
    expect(res.truncated).toBe(true);
  });

  it('reports anySucceeded=false when every type errors on its first page (→ caller falls back to legacy)', async () => {
    const res = await fetchEventsViaIndex(
      ['x', 'y'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher({}), fakeClock(), // empty map → every fetch throws
    );

    expect(res.events).toHaveLength(0);
    expect(res.anySucceeded).toBe(false);
    expect(res.truncated).toBe(true);
  });

  it('treats an empty-but-successful page as drained (anySucceeded=true, not truncated)', async () => {
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=none&pageSize=200`]: { events: [] }, // no matches, no nextLink
    };

    const res = await fetchEventsViaIndex(
      ['none'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events).toHaveLength(0);
    expect(res.anySucceeded).toBe(true);
    expect(res.truncated).toBe(false);
  });

  // Regression lock for the cross-session "0 matches" bug: /api/raw/events returns rows in
  // VERBATIM PascalCase (EventType/Severity-as-int/DataJson-as-string/PartitionKey), not the
  // enriched camelCase shape. fetchEventsViaIndex must normalize them so _sessionId is extracted
  // (sessionsSearchedCount) and the fields are present for scoreEvent (eventsMatched). The former
  // camelCase-only fixtures masked the defect.
  it('normalizes verbatim PascalCase raw rows so _sessionId and scorable fields survive', async () => {
    const tenant = '11111111-1111-1111-1111-111111111111';
    const sess = '22222222-2222-2222-2222-222222222222';
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=app_install_failed&pageSize=200`]: {
        events: [
          {
            PartitionKey: `${tenant}_${sess}`,
            RowKey: '20260531120000000_0000000042',
            SessionId: sess,
            TenantId: tenant,
            EventType: 'app_install_failed',
            Severity: 3, // int → "Error"
            Source: 'IME',
            Phase: 2,
            Message: 'Installation is timeout',
            DataJson: '{"AppName":"Contoso VPN","ErrorCode":"0x87D13B66"}',
          },
        ],
      },
    };

    const res = await fetchEventsViaIndex(
      ['app_install_failed'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events).toHaveLength(1);
    const ev = res.events[0];
    expect(ev._sessionId).toBe(sess); // was undefined pre-fix → sessionsSearchedCount:0
    expect(ev.eventType).toBe('app_install_failed');
    expect(ev.severity).toBe('Error'); // int 3 mapped back to the enum name
    expect(ev.message).toBe('Installation is timeout');
    // The normalized event must actually score (was 0 matches pre-fix).
    const scored = scoreEvent(ev, ['app', 'install', 'timeout'], true);
    expect(scored).not.toBeNull();
    expect(scored!.score).toBeGreaterThan(0);
  });

  it('recovers sessionId from the PartitionKey when the SessionId column is absent', async () => {
    const tenant = '33333333-3333-3333-3333-333333333333';
    const sess = '44444444-4444-4444-4444-444444444444';
    const pages: Record<string, Page> = {
      [`${BASE}?eventType=error_detected&pageSize=200`]: {
        events: [{ PartitionKey: `${tenant}_${sess}`, EventType: 'error_detected', Severity: 3 }],
      },
    };

    const res = await fetchEventsViaIndex(
      ['error_detected'], BASE, undefined,
      { maxPagesPerType: 10, wallClockMs: 60_000 },
      fakeFetcher(pages), fakeClock(),
    );

    expect(res.events[0]._sessionId).toBe(sess);
  });
});

describe('normalizeRawEvent (raw PascalCase → enriched camelCase)', () => {
  it('maps every stored column and recovers the session id', () => {
    const ev = normalizeRawEvent({
      PartitionKey: 'tenant_abc_sess',
      SessionId: 'abc-session',
      EventType: 'system_reboot_detected',
      Severity: 1,
      Source: 'Agent',
      Phase: 4,
      Message: 'machine restarted',
      DataJson: '{"Reason":"WindowsUpdate"}',
      Timestamp: '2026-05-31T12:00:00Z',
    });
    expect(ev.eventType).toBe('system_reboot_detected');
    expect(ev.severity).toBe('Info'); // 1 → Info
    expect(ev.source).toBe('Agent');
    expect(ev.phase).toBe(4);
    expect(ev.message).toBe('machine restarted');
    expect(ev.data).toEqual({ Reason: 'WindowsUpdate' });
    expect(ev._sessionId).toBe('abc-session'); // explicit column wins over PartitionKey
    expect(ev.sessionId).toBe('abc-session');
  });

  it('maps the full severity int range to enum names', () => {
    const sev = (n: number) => normalizeRawEvent({ Severity: n }).severity;
    expect(sev(-1)).toBe('Trace');
    expect(sev(0)).toBe('Debug');
    expect(sev(1)).toBe('Info');
    expect(sev(2)).toBe('Warning');
    expect(sev(3)).toBe('Error');
    expect(sev(4)).toBe('Critical');
  });

  it('tolerates malformed DataJson (kept searchable as raw text)', () => {
    expect(normalizeRawEvent({ DataJson: '{not json' }).data).toEqual({ raw: '{not json' });
    expect(normalizeRawEvent({ DataJson: '' }).data).toBeUndefined();
  });

  it('falls back to camelCase keys (already-enriched row passes through unchanged)', () => {
    const ev = normalizeRawEvent({ eventType: 'log_entry', severity: 'Warning', sessionId: 's9', message: 'x' });
    expect(ev.eventType).toBe('log_entry');
    expect(ev.severity).toBe('Warning'); // name preserved as-is
    expect(ev._sessionId).toBe('s9');
  });
});

describe('resolveQueryKeywords (caller keywords are additive + lowercased)', () => {
  it('lowercases caller keywords so they can match the lowercased corpus', () => {
    // Regression: `keywords: ["BitLocker"]` used to flow through verbatim and match
    // nothing — silent empty result.
    expect(resolveQueryKeywords('encryption stuck', ['BitLocker'])).toEqual(['bitlocker', 'encryption', 'stuck']);
  });

  it('merges caller keywords with auto-extraction instead of replacing it', () => {
    // Regression: `??` replaced extraction entirely, discarding query-derived recall.
    const out = resolveQueryKeywords('bitlocker encryption failed', ['0x87D1041C']);
    expect(out).toEqual(['0x87d1041c', 'bitlocker', 'encryption', 'failed']);
  });

  it('dedupes a caller keyword already extracted from the query', () => {
    expect(resolveQueryKeywords('bitlocker failed', ['BitLocker'])).toEqual(['bitlocker', 'failed']);
  });

  it('drops empty / whitespace-only caller keywords', () => {
    expect(resolveQueryKeywords('bitlocker failed', ['  ', '', ' TPM '])).toEqual(['tpm', 'bitlocker', 'failed']);
  });

  it('falls back to pure auto-extraction when no keywords are supplied', () => {
    expect(resolveQueryKeywords('bitlocker failed')).toEqual(['bitlocker', 'failed']);
    expect(resolveQueryKeywords('bitlocker failed', [])).toEqual(['bitlocker', 'failed']);
  });
});

describe('queryHasProblemIntent', () => {
  it('detects failure-intent stems (incl. inflections)', () => {
    expect(queryHasProblemIntent(['error'])).toBe(true);
    expect(queryHasProblemIntent(['failed'])).toBe(true);   // "fail" stem
    expect(queryHasProblemIntent(['failure'])).toBe(true);
    expect(queryHasProblemIntent(['stuck'])).toBe(true);
    expect(queryHasProblemIntent(['timeout'])).toBe(true);
    expect(queryHasProblemIntent(['certificate', 'error', 'enrollment'])).toBe(true);
  });

  it('returns false for purely benign queries', () => {
    expect(queryHasProblemIntent(['certificate', 'enrollment'])).toBe(false);
    expect(queryHasProblemIntent(['desktop', 'arrived'])).toBe(false);
    expect(queryHasProblemIntent([])).toBe(false);
  });
});

describe('scoreEvent — severity-intent alignment', () => {
  const keywords = ['certificate', 'error', 'enrollment'];
  // Benign success: matches "enrollment" (eventType) + "certificate" (data).
  const benign = { eventType: 'enrollment_complete', severity: 'Info', data: { note: 'certificate ok' } };
  // Real failure: matches "error" (eventType) + "certificate" (data).
  const failure = { eventType: 'error_detected', severity: 'Error', data: { detail: 'certificate chain failed' } };

  it('scores benign and failure events equally when intent is absent', () => {
    const a = scoreEvent(benign, keywords, false);
    const b = scoreEvent(failure, keywords, false);
    expect(a?.score).toBeCloseTo(0.522, 2);
    expect(b?.score).toBeCloseTo(0.522, 2);
  });

  it('lifts the failure above the benign success when intent is present', () => {
    const a = scoreEvent(benign, keywords, true);
    const b = scoreEvent(failure, keywords, true);
    expect(a!.score).toBeLessThan(b!.score);
    expect(b?.score).toBeCloseTo(0.783, 2); // 0.522 * 1.5
    expect(a?.score).toBeCloseTo(0.313, 2); // 0.522 * 0.6 (benign Info damped)
  });

  it('clamps a boosted score into [0, 1]', () => {
    const strong = { eventType: 'enrollment_failed', severity: 'Error' }; // "enrollment" + "error"? only "enrollment"
    const s = scoreEvent({ eventType: 'error_enrollment_certificate' }, keywords, true);
    expect(s!.score).toBeLessThanOrEqual(1);
    expect(scoreEvent(strong, keywords, true)!.score).toBeLessThanOrEqual(1);
  });

  it('returns null when no keyword matches', () => {
    expect(scoreEvent({ eventType: 'network_state_change', severity: 'Info' }, keywords, true)).toBeNull();
  });
});

describe('scoreEvent — benign health-detection report is damped, not boosted', () => {
  // A compliant health-script detection mis-stamped script_failed/Error must not ride the
  // failure boost: it should rank like an Info event so it stops polluting problem queries.
  const keywords = ['compliance', 'script', 'failed'];
  const benignDetection = {
    eventType: 'script_failed', severity: 'Error',
    data: { scriptType: 'remediation', scriptPart: 'detection', complianceResult: 'True', exitCode: '0' },
    message: 'Remediation script 55f2e743: compliance=True (exit: 0)',
  };
  const realScriptFailure = {
    eventType: 'script_failed', severity: 'Error',
    data: { scriptType: 'remediation', scriptPart: 'remediation', exitCode: '1' },
    message: 'Remediation script crashed',
  };

  it('ranks the benign compliant detection below a real script failure on a problem query', () => {
    const benign = scoreEvent(benignDetection, keywords, true)!;
    const real = scoreEvent(realScriptFailure, keywords, true)!;
    expect(benign.score).toBeLessThan(real.score);
  });

  it('damps the benign detection by the Info factor (0.6) not the failure factor (1.5)', () => {
    // Single low-weight keyword keeps the unboosted score < 1 so the ×0.6 damp is exact
    // (a multi-keyword match would clamp the unboosted score to 1.0 first).
    const kw = ['compliance'];
    const boosted = scoreEvent(benignDetection, kw, true)!;
    const unboosted = scoreEvent(benignDetection, kw, false)!;
    expect(unboosted.score).toBeLessThan(1);
    expect(boosted.score).toBeCloseTo(unboosted.score * 0.6, 5);
  });
});

describe('extractEventTypeCandidates — ranking + broad-catalog coverage', () => {
  it('ranks the type matching the most keywords first', () => {
    const ranked = extractEventTypeCandidates(['hello', 'provisioning', 'failed']);
    // hello_provisioning_failed matches all three keywords → must rank #1.
    expect(ranked[0]).toBe('hello_provisioning_failed');
  });

  it('returns the full ranked set (not pre-capped) so a broad keyword surfaces many types', () => {
    // "hello" alone matches the whole hello_* family — well beyond the per-walk cap.
    // The caller (fetchSessionEvents) caps to the top-N; this fn must NOT pre-truncate,
    // otherwise the catalog-completion regression (legacy fallback) reappears.
    const ranked = extractEventTypeCandidates(['hello']);
    expect(ranked.length).toBeGreaterThan(10);
    expect(ranked).toContain('hello_provisioning_failed');
    expect(ranked.every((t) => t.includes('hello'))).toBe(true);
  });

  it('returns [] when no keyword maps to any event type (→ caller uses legacy path)', () => {
    expect(extractEventTypeCandidates(['zzzznomatch'])).toEqual([]);
  });
});

describe('selectEventTypeCandidates (lexical + semantic blend)', () => {
  // Minimal fake vector provider returning canned semantic hits.
  const provider = (eventTypes: string[], opts: { throws?: boolean; size?: number; semanticCapable?: boolean } = {}) => ({
    name: 'fake',
    semanticCapable: opts.semanticCapable ?? true,
    size: opts.size ?? eventTypes.length,
    index: async () => {},
    search: async () => {
      if (opts.throws) throw new Error('embed failed');
      return eventTypes.map((et, i) => ({ id: et, text: et, metadata: { eventType: et }, score: 1 - i * 0.01 }));
    },
  });

  it('keeps lexical matches first, then appends semantic-only additions', async () => {
    const { candidates } = await selectEventTypeCandidates('bitlocker', ['bitlocker'], provider(['tpm_status', 'secureboot_status']));
    expect(candidates[0]).toBe('bitlocker_status'); // lexical, precise → first
    expect(candidates).toContain('tpm_status');
    expect(candidates).toContain('secureboot_status');
  });

  it('returns the cosine score for each semantically-selected type', async () => {
    const { semanticTypeScores } = await selectEventTypeCandidates('bitlocker', ['bitlocker'], provider(['tpm_status', 'secureboot_status']));
    // provider scores are 1 - i*0.01 → tpm_status=1.0, secureboot_status=0.99.
    expect(semanticTypeScores.get('tpm_status')).toBeCloseTo(1.0, 5);
    expect(semanticTypeScores.get('secureboot_status')).toBeCloseTo(0.99, 5);
  });

  it('surfaces semantically-related types even when NO keyword matches lexically', async () => {
    // "encryption" matches no event-type name lexically; semantic maps it to bitlocker_status.
    expect(extractEventTypeCandidates(['encryption'])).toEqual([]);
    const { candidates, semanticTypeScores } = await selectEventTypeCandidates('disk encryption', ['encryption'], provider(['bitlocker_status']));
    expect(candidates).toEqual(['bitlocker_status']);
    expect(semanticTypeScores.get('bitlocker_status')).toBeCloseTo(1.0, 5);
  });

  it('degrades to keyword-only (empty semantic scores) when no vector index is available', async () => {
    const noIndex = await selectEventTypeCandidates('bitlocker', ['bitlocker'], undefined);
    expect(noIndex.candidates).toEqual(['bitlocker_status']);
    expect(noIndex.semanticTypeScores.size).toBe(0);
    const emptyIndex = await selectEventTypeCandidates('bitlocker', ['bitlocker'], provider([], { size: 0 }));
    expect(emptyIndex.candidates).toEqual(['bitlocker_status']);
    expect(emptyIndex.semanticTypeScores.size).toBe(0);
  });

  it('falls back to keyword-only if the embedder throws', async () => {
    const out = await selectEventTypeCandidates('bitlocker', ['bitlocker'], provider(['tpm_status'], { throws: true }));
    expect(out.candidates).toEqual(['bitlocker_status']);
    expect(out.semanticTypeScores.size).toBe(0);
  });

  it('skips semantic selection for a non-semantic (fuse) backend — its scores are not cosines', async () => {
    // Regression: gating on index PRESENCE alone let SEARCH_BACKEND=fuse feed inverted
    // fuzzy-match scores through cosine-calibrated thresholds as fake cosines.
    const out = await selectEventTypeCandidates('bitlocker', ['bitlocker'], provider(['tpm_status'], { semanticCapable: false }));
    expect(out.candidates).toEqual(['bitlocker_status']);
    expect(out.semanticTypeScores.size).toBe(0);
  });
});

describe('scoreEvent — semantic floor (the core recall fix)', () => {
  it('surfaces a semantically-selected event with ZERO keyword overlap instead of dropping it', () => {
    // The regression: query maps to system_reboot_detected via the vector index, but no
    // query word matches the event lexically. Pre-fix this returned null → 0 results.
    const semanticTypeScores = new Map([['system_reboot_detected', 0.45]]);
    const ev = { eventType: 'system_reboot_detected', severity: 'Info' };
    const s = scoreEvent(ev, ['machine', 'unexpectedly'], false, semanticTypeScores);
    expect(s).not.toBeNull();
    expect(s!.matchedKeywords).toEqual([]);
    expect(s!.bestFields).toEqual(['eventType(semantic)']);
    expect(s!.score).toBeGreaterThanOrEqual(0.15); // always clears the fast-tier minScore (0.1)
  });

  it('still returns null with no keyword match AND no semantic score (single-session / legacy)', () => {
    expect(scoreEvent({ eventType: 'system_reboot_detected' }, ['machine'], false)).toBeNull();
    expect(scoreEvent({ eventType: 'system_reboot_detected' }, ['machine'], false, new Map())).toBeNull();
  });

  it('ranks a strong lexical match above a semantic-only one', () => {
    const semanticTypeScores = new Map([['network_state_change', 0.4]]);
    const lexical = scoreEvent({ eventType: 'certificate_error', severity: 'Error' }, ['certificate', 'error'], false, semanticTypeScores);
    const semanticOnly = scoreEvent({ eventType: 'network_state_change', severity: 'Info' }, ['certificate', 'error'], false, semanticTypeScores);
    expect(lexical!.score).toBeGreaterThan(semanticOnly!.score);
  });

  it('scales the floor with cosine — a closer type outranks a looser one', () => {
    const close = scoreEvent({ eventType: 'a' }, ['x'], false, new Map([['a', 0.9]]));
    const loose = scoreEvent({ eventType: 'b' }, ['x'], false, new Map([['b', 0.31]]));
    expect(close!.score).toBeGreaterThan(loose!.score);
  });
});

describe('scoreEvent — synonym-aware matching', () => {
  it('matches a query word to its synonym in the event type (restarted → reboot)', () => {
    // "restarted" is not a literal substring of system_reboot_detected, but "reboot" (its
    // synonym) is — so the event matches lexically, no semantic floor needed.
    const s = scoreEvent({ eventType: 'system_reboot_detected', severity: 'Info' }, ['restarted'], false);
    expect(s).not.toBeNull();
    expect(s!.matchedKeywords).toEqual(['restarted']);
    expect(s!.bestFields).toContain('eventType');
  });

  it('matches "stuck" to a timeout/stall event', () => {
    const s = scoreEvent({ eventType: 'app_install_timeout' }, ['stuck'], false);
    expect(s).not.toBeNull();
    expect(s!.matchedKeywords).toEqual(['stuck']);
  });

  it('does NOT let a short synonym match mid-word ("stall"⊂"install", "hang"⊂"change")', () => {
    // Regression: "timeout"→synonym "stall" must not match app_install_started via the "stall"
    // inside "install"; likewise "hang" must not match network_state_change via "change".
    expect(scoreEvent({ eventType: 'app_install_started', severity: 'Info' }, ['timeout'], false)).toBeNull();
    expect(scoreEvent({ eventType: 'network_state_change', severity: 'Info' }, ['timeout'], false)).toBeNull();
  });
});

describe('prioritizeFailureTypes', () => {
  it('moves failure-signal types ahead of benign ones, preserving relative order', () => {
    // The "app install timeout" trap: app_install_started (benign, high-volume) is walked
    // first and starves app_install_failed of the budget. Reorder → failure first.
    const out = prioritizeFailureTypes([
      'app_install_started', 'app_install_failed', 'app_install_completed', 'error_detected',
    ]);
    expect(out).toEqual([
      'app_install_failed', 'error_detected', 'app_install_started', 'app_install_completed',
    ]);
  });

  it('is a no-op when no type is a failure signal', () => {
    const input = ['tpm_status', 'download_progress', 'desktop_arrived'];
    expect(prioritizeFailureTypes(input)).toEqual(input);
  });
});

describe('isProblemIntentKeyword', () => {
  it('flags failure-intent stems, not entity words', () => {
    expect(isProblemIntentKeyword('failed')).toBe(true);
    expect(isProblemIntentKeyword('timeout')).toBe(true);
    expect(isProblemIntentKeyword('stuck')).toBe(true);
    expect(isProblemIntentKeyword('bitlocker')).toBe(false);
    expect(isProblemIntentKeyword('download')).toBe(false);
  });
});

describe('computeKeywordSpecificity', () => {
  it('rates a named entity as far rarer than a generic failure word', () => {
    const spec = computeKeywordSpecificity(['bitlocker', 'failed', 'download']);
    // "bitlocker" → only bitlocker_status; "failed" → the whole *_failed family.
    expect(spec.get('bitlocker')).toBe(1);
    expect(spec.get('download')!).toBeLessThanOrEqual(RARE_THRESHOLD_FOR_TEST);
    expect(spec.get('failed')!).toBeGreaterThan(spec.get('bitlocker')!);
    expect(spec.get('failed')!).toBeGreaterThan(RARE_THRESHOLD_FOR_TEST);
  });
});

describe('dedupeSynonymConcepts', () => {
  it('collapses two query words from the SAME synonym group to one concept', () => {
    // "stuck" and "timeout" are one concept — must not double-count coverage.
    expect(dedupeSynonymConcepts(['app', 'download', 'stuck', 'timeout'])).toEqual(['app', 'download', 'stuck']);
  });
  it('is a no-op when there are no intra-group duplicates', () => {
    expect(dedupeSynonymConcepts(['bitlocker', 'encryption', 'failed'])).toEqual(['bitlocker', 'encryption', 'failed']);
  });
});

describe('orderBySelectivity (fetch-fairness - rare named entity walked first)', () => {
  it('puts the type selected by the RAREST keyword first, so it is not starved', () => {
    const kws = ['bitlocker', 'encryption', 'failed'];
    const spec = computeKeywordSpecificity(kws);
    // enrollment_failed (generic "failed") must NOT precede bitlocker_status (rare "bitlocker").
    const ordered = orderBySelectivity(['enrollment_failed', 'bitlocker_status'], kws, spec);
    expect(ordered[0]).toBe('bitlocker_status');
  });

  it('preserves failure-priority as the within-selectivity tie-break', () => {
    const kws = ['app', 'install'];
    const spec = computeKeywordSpecificity(kws);
    // app_install_failed / _started have the same selectivity → failure first (old behavior).
    const ordered = orderBySelectivity(['app_install_started', 'app_install_failed'], kws, spec);
    expect(ordered).toEqual(['app_install_failed', 'app_install_started']);
  });
});

describe('scoreEvent — intra-query synonym de-dup', () => {
  it('does not let a doubled timeout concept inflate an off-topic type above the named entity', () => {
    const kws = ['app', 'download', 'stuck', 'timeout'];
    const spec = computeKeywordSpecificity(kws);
    // hello_wait_timeout matches only the timeout concept (stuck≡timeout → counted once);
    // app_download_started matches the named entity "download" (+"app").
    const hello = scoreEvent({ eventType: 'hello_wait_timeout', severity: 'Info' }, kws, true, undefined, spec)!;
    const dl = scoreEvent({ eventType: 'app_download_started', severity: 'Info' }, kws, true, undefined, spec)!;
    expect(dl.score).toBeGreaterThan(hello.score);
  });
});

describe('scoreEvent — specificity-gated named-entity boost', () => {
  it('lifts a rare Info entity (bitlocker_status) ABOVE a generic-failure Error on a problem query', () => {
    const kws = ['bitlocker', 'encryption', 'failed'];
    const spec = computeKeywordSpecificity(kws);
    const entity = scoreEvent({ eventType: 'bitlocker_status', severity: 'Info' }, kws, true, undefined, spec)!;
    const generic = scoreEvent({ eventType: 'enrollment_failed', severity: 'Error' }, kws, true, undefined, spec)!;
    expect(entity.score).toBeGreaterThan(generic.score);
  });

  it('does NOT boost a high-volume benign type matched only by GENERIC words (no flood)', () => {
    const kws = ['app', 'install', 'error'];
    const spec = computeKeywordSpecificity(kws);
    // "app"/"install" are not rare (>3 types) → app_install_started stays Info-damped, so a real
    // failure still outranks it on a problem query.
    const benign = scoreEvent({ eventType: 'app_install_started', severity: 'Info' }, kws, true, undefined, spec)!;
    const failure = scoreEvent({ eventType: 'app_install_failed', severity: 'Error' }, kws, true, undefined, spec)!;
    expect(failure.score).toBeGreaterThan(benign.score);
  });

  it('is inert without the specificity map (existing callers unaffected)', () => {
    const kws = ['bitlocker', 'failed'];
    const withMap = scoreEvent({ eventType: 'bitlocker_status', severity: 'Info' }, kws, true, undefined, computeKeywordSpecificity(kws))!;
    const without = scoreEvent({ eventType: 'bitlocker_status', severity: 'Info' }, kws, true)!;
    expect(withMap.score).toBeGreaterThan(without.score); // boost only applies when the map is present
  });
});

describe('starvation repro — rare entity is captured within a tight budget once ordered first', () => {
  it('orderBySelectivity + a 1-type budget fetches bitlocker_status, not the generic flood', async () => {
    const kws = ['bitlocker', 'failed'];
    const spec = computeKeywordSpecificity(kws);
    const ordered = orderBySelectivity(['enrollment_failed', 'bitlocker_status'], kws, spec);

    const pages: Record<string, Page> = {
      [`${BASE}?eventType=bitlocker_status&pageSize=200`]: {
        events: [{ sessionId: 'b1', eventType: 'bitlocker_status' }],
        // drains in one page
      },
      [`${BASE}?eventType=enrollment_failed&pageSize=200`]: {
        events: [{ sessionId: 'e1', eventType: 'enrollment_failed' }],
      },
    };

    // Wall-clock expires after the FIRST type's page (deadline=10: pre-loop now()=0 sets it,
    // the bitlocker_status check at now()=8 passes, the enrollment_failed check at now()=16 trips).
    const res = await fetchEventsViaIndex(
      ordered, BASE, undefined,
      { maxPagesPerType: 5, wallClockMs: 10 },
      fakeFetcher(pages), fakeClock(0, 8),
    );

    // Pre-fix (failure-first order) this would fetch enrollment_failed and starve bitlocker_status.
    expect(res.events.map((e) => e.eventType)).toContain('bitlocker_status');
    expect(res.truncated).toBe(true); // budget genuinely cut the walk short
  });
});

describe('entityRecallHint', () => {
  it('names the entity event type for exhaustive recall', () => {
    const kws = ['bitlocker', 'failed'];
    const hint = entityRecallHint(kws, computeKeywordSpecificity(kws));
    expect(hint).toContain('bitlocker_status');
    expect(hint).toContain('search_sessions_by_event');
  });
  it('returns undefined when no rare named entity is present', () => {
    const kws = ['enrollment', 'failed'];
    expect(entityRecallHint(kws, computeKeywordSpecificity(kws))).toBeUndefined();
  });
});

describe('diversifyBySession', () => {
  const ev = (sid: string, score: number) => ({ event: { _sessionId: sid }, score });

  it('surfaces a second session instead of a third event from the same one', () => {
    // Score order: A, A, A, B, C. guaranteedTop=2 → lock A,A then per-session cap surfaces B.
    const scored = [ev('A', 9), ev('A', 8), ev('A', 7), ev('B', 6), ev('C', 5)];
    const out = diversifyBySession(scored, 3, 2, 2);
    expect(out.map((s) => s.event._sessionId)).toEqual(['A', 'A', 'B']);
  });

  it('backfills (never drops) when distinct sessions run out', () => {
    const scored = [ev('A', 9), ev('A', 8), ev('A', 7)];
    const out = diversifyBySession(scored, 3);
    expect(out).toHaveLength(3); // single session → same as a plain top-K slice
    expect(out.map((s) => s.event._sessionId)).toEqual(['A', 'A', 'A']);
  });

  it('respects topK and preserves score order within the picked set', () => {
    const scored = [ev('A', 9), ev('B', 8), ev('A', 7), ev('C', 6)];
    const out = diversifyBySession(scored, 2);
    expect(out.map((s) => s.event._sessionId)).toEqual(['A', 'B']);
  });

  it('guaranteedTop locks the strongest hits to the head, exempt from the per-session cap', () => {
    // Same-session A holds the 3 best scores; guaranteedTop=3 keeps all three on top.
    const scored = [ev('A', 9), ev('A', 8), ev('A', 7), ev('B', 6), ev('C', 5)];
    const out = diversifyBySession(scored, 4, 2, 3);
    expect(out.map((s) => s.event._sessionId)).toEqual(['A', 'A', 'A', 'B']);
  });

  it('guaranteedTop >= topK disables diversification (pure score order)', () => {
    const scored = [ev('A', 9), ev('A', 8), ev('A', 7), ev('A', 6), ev('B', 5)];
    const out = diversifyBySession(scored, 4, 2, 4);
    expect(out.map((s) => s.event._sessionId)).toEqual(['A', 'A', 'A', 'A']);
  });

  it('guaranteedTop=0 leans fully on per-session diversity', () => {
    const scored = [ev('A', 9), ev('A', 8), ev('A', 7), ev('B', 6), ev('C', 5)];
    const out = diversifyBySession(scored, 3, 2, 0);
    // No locked head; per-session cap (2) still surfaces B at rank 3.
    expect(out.map((s) => s.event._sessionId)).toEqual(['A', 'A', 'B']);
  });
});

describe('extractErrorCodeNeedles', () => {
  it('extracts a 0x-prefixed HRESULT and strips the prefix, lowercased', () => {
    expect(extractErrorCodeNeedles('why did 0x87D1041C fail?')).toEqual(['87d1041c']);
  });

  it('extracts a bare HRESULT-shaped hex token (has an a-f letter)', () => {
    expect(extractErrorCodeNeedles('error 87D1041C during ESP')).toEqual(['87d1041c']);
  });

  it('does not double-count the same code written with the 0x prefix', () => {
    expect(extractErrorCodeNeedles('0x80070002')).toEqual(['80070002']);
  });

  it('ignores pure-decimal numbers (dates, counts, build numbers)', () => {
    expect(extractErrorCodeNeedles('20260531 sessions on build 12345678')).toEqual([]);
  });

  it('returns nothing for a plain natural-language query', () => {
    expect(extractErrorCodeNeedles('TPM not ready / BitLocker issues')).toEqual([]);
  });

  it('extracts multiple distinct codes, deduplicated', () => {
    expect(extractErrorCodeNeedles('0x87D1041C and 0x80070002 and 0x87d1041c').sort())
      .toEqual(['80070002', '87d1041c']);
  });
});

describe('scanLexical (error-code fallback scan)', () => {
  const docs: SearchDocument[] = [
    { id: 'ANALYZE-ENRL-001', text: 'Enrollment Failed. When paired with HRESULT 0x87D1041C the detection rule did not match.', metadata: { type: 'analyze-rule' } },
    { id: 'ANALYZE-DEV-001', text: 'TPM not ready. BitLocker cannot proceed until the TPM is provisioned.', metadata: { type: 'analyze-rule' } },
  ];

  it('finds the doc that names the code verbatim, scored 1.0, case-insensitively', () => {
    const hits = scanLexical(docs, ['87d1041c']);
    expect(hits.map((h) => h.id)).toEqual(['ANALYZE-ENRL-001']);
    expect(hits[0].score).toBe(1);
  });

  it('returns nothing when no needle is present', () => {
    expect(scanLexical(docs, ['deadbeef'])).toEqual([]);
  });

  it('returns nothing for an empty needle list', () => {
    expect(scanLexical(docs, [])).toEqual([]);
  });
});
