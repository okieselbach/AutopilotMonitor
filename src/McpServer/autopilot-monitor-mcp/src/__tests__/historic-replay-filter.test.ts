/**
 * Historic-replay predicate (session eaf3d8c4): legacy agents replay IME log content from a
 * previous enrollment; the replayed events carry `data.rejectedSourceTimestamp` > 24 h older
 * than the event stamp. `get_session_summary` filters them out so appInstalls counts,
 * errorCount (replayed app_install_failed are Error-severity) and the keyEvents triage
 * timeline reflect only this enrollment's activity.
 */
import { describe, it, expect } from 'vitest';
import { isHistoricImeReplay } from '../tools/sessions.js';

const BASE = Date.UTC(2026, 6, 23, 15, 42, 0);
const DAY_MS = 24 * 60 * 60 * 1000;

const evt = (rejected?: unknown, timestamp: string = new Date(BASE).toISOString()): Record<string, unknown> => ({
  timestamp,
  eventType: 'app_install_completed',
  data: rejected === undefined ? {} : { rejectedSourceTimestamp: rejected },
});

describe('isHistoricImeReplay', () => {
  it('detects a rejected source timestamp > 24h older than the event stamp', () => {
    expect(isHistoricImeReplay(evt(new Date(BASE - 7 * DAY_MS).toISOString()))).toBe(true);
    expect(isHistoricImeReplay(evt(new Date(BASE - 25 * 60 * 60 * 1000).toISOString()))).toBe(true);
  });

  it('keeps within-24h rejections', () => {
    expect(isHistoricImeReplay(evt(new Date(BASE - 23 * 60 * 60 * 1000).toISOString()))).toBe(false);
  });

  it('keeps future-skew rejections (clock jump, not replay)', () => {
    expect(isHistoricImeReplay(evt(new Date(BASE + 2 * 60 * 60 * 1000).toISOString()))).toBe(false);
  });

  it('keeps events without or with malformed rejectedSourceTimestamp', () => {
    expect(isHistoricImeReplay(evt())).toBe(false);
    expect(isHistoricImeReplay(evt(''))).toBe(false);
    expect(isHistoricImeReplay(evt('not-a-date'))).toBe(false);
    expect(isHistoricImeReplay(evt(12345))).toBe(false);
    expect(isHistoricImeReplay({ timestamp: new Date(BASE).toISOString() })).toBe(false);
  });

  it('keeps events with an unparseable event timestamp', () => {
    expect(isHistoricImeReplay(evt(new Date(BASE - 7 * DAY_MS).toISOString(), 'garbage'))).toBe(false);
  });

  it('reads the snake_case wire variant', () => {
    const e = {
      timestamp: new Date(BASE).toISOString(),
      data: { rejected_source_timestamp: new Date(BASE - 7 * DAY_MS).toISOString() },
    };
    expect(isHistoricImeReplay(e)).toBe(true);
  });
});
