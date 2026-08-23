import { describe, it, expect } from 'vitest';
import { shapeVerdictCalibration, type VerdictCalibrationPathRow } from '../verdict-calibration-shape.js';

function row(verdictPath: string, count: number, sharePct: number, extra: Partial<VerdictCalibrationPathRow> = {}): VerdictCalibrationPathRow {
  return {
    verdictPath,
    status: 'Succeeded',
    count,
    sharePct,
    overriddenByAdmin: 0,
    overriddenByLateCompletion: 0,
    overriddenOther: 0,
    window7: { count, sessions: 1000, sharePct },
    baseline28: { count, sessions: 2000, sharePct },
    ...extra,
  };
}

function payload(paths: VerdictCalibrationPathRow[]) {
  return {
    success: true,
    totals: { sessions: 1000 },
    trend: { windowSessions: 1000, baselineSessions: 2000 },
    paths,
    alerts: [],
  };
}

describe('shapeVerdictCalibration', () => {
  it('drops the per-row denominators and makes withheld values explicit null', () => {
    const out = shapeVerdictCalibration(payload([row('agent:complete', 500, 50)])) as any;
    expect(out.paths[0].window7).toEqual({ count: 500, sharePct: 50 });
    expect(out.paths[0].baseline28).toEqual({ count: 500, sharePct: 50 });
    expect(out.paths[0]).toHaveProperty('reEnrollRatePct', null);
    expect(out.paths[0]).toHaveProperty('lift', null);
    expect(out.omitted).toBeUndefined();
    expect(out.trend).toEqual({ windowSessions: 1000, baselineSessions: 2000 });
  });

  it('keeps real numeric values (0 is not null)', () => {
    const out = shapeVerdictCalibration(payload([row('legacy:r3', 4, 0.1, { reEnrollRatePct: 0, lift: 0 })])) as any;
    expect(out.paths[0].reEnrollRatePct).toBe(0);
    expect(out.paths[0].lift).toBe(0);
  });

  it('minSharePct trims the long tail and reports what was omitted', () => {
    const out = shapeVerdictCalibration(payload([
      row('agent:complete', 900, 90),
      row('legacy:r4', 10, 1),
      row('manual:succeeded', 1, 0.1),
    ]), { minSharePct: 1 }) as any;
    expect(out.paths.map((p: any) => p.verdictPath)).toEqual(['agent:complete', 'legacy:r4']);
    expect(out.omitted).toEqual({ paths: 1, sessions: 1, reason: expect.stringContaining('share < 1%') });
  });

  it('top keeps the N largest rows by backend order', () => {
    const out = shapeVerdictCalibration(payload([
      row('a', 50, 50), row('b', 30, 30), row('c', 20, 20),
    ]), { top: 2 }) as any;
    expect(out.paths.map((p: any) => p.verdictPath)).toEqual(['a', 'b']);
    expect(out.omitted.paths).toBe(1);
    expect(out.omitted.reason).toContain('rank > 2');
  });

  it('never trims a row that carries overrides — that is the signal', () => {
    const out = shapeVerdictCalibration(payload([
      row('agent:complete', 999, 99.9),
      row('sweep:r5_incomplete', 1, 0.1, { overriddenByLateCompletion: 3 }),
    ]), { minSharePct: 5, top: 1 }) as any;
    expect(out.paths.map((p: any) => p.verdictPath)).toEqual(['agent:complete', 'sweep:r5_incomplete']);
    expect(out.omitted).toBeUndefined();
  });

  it('does not mutate the input and passes non-matrix payloads through', () => {
    const input = payload([row('x', 1, 100)]);
    const snapshot = JSON.stringify(input);
    shapeVerdictCalibration(input, { top: 0 });
    expect(JSON.stringify(input)).toBe(snapshot);
    const err = { success: false, message: 'nope' };
    expect(shapeVerdictCalibration(err)).toBe(err);
    expect(shapeVerdictCalibration(null)).toBeNull();
  });
});
