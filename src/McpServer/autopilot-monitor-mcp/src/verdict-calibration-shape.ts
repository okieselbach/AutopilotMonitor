/**
 * LLM-facing shaping of the verdict-calibration matrix (GET /api/global/metrics/verdict-calibration).
 *
 * The backend wire shape is shared with the admin metrics page and stays untouched; this layer only
 * trims what a model does not need and makes absent values explicit:
 *  - `window7.sessions` / `baseline28.sessions` repeat `trend.windowSessions` / `trend.baselineSessions`
 *    on every row — dropped (the denominators live once in `trend`).
 *  - `reEnrollRatePct` / `lift` are omitted by the backend's WhenWritingNull serializer when null;
 *    a missing key is easy to misread as "not applicable", so both are always present (null = withheld).
 *  - `minSharePct` / `top` cut the long tail of one-session paths. Rows carrying overrides are never
 *    cut (an override on a rare path is the signal the tool exists for). What was dropped is reported
 *    in `omitted` so a filtered response never reads as complete coverage.
 */

interface TrendCell {
  count: number;
  sessions?: number;
  sharePct: number;
}

export interface VerdictCalibrationPathRow {
  verdictPath: string;
  status: string;
  count: number;
  sharePct: number;
  overriddenByAdmin: number;
  overriddenByLateCompletion: number;
  overriddenOther: number;
  reEnrollRatePct?: number | null;
  lift?: number | null;
  window7: TrendCell;
  baseline28: TrendCell;
  [key: string]: unknown;
}

export interface VerdictCalibrationShapeOptions {
  /** Drop rows whose share (in the window) is below this percentage. */
  minSharePct?: number;
  /** Keep only the first N rows (the backend orders by count descending). */
  top?: number;
}

export interface VerdictCalibrationOmitted {
  paths: number;
  sessions: number;
  reason: string;
}

function hasOverrides(row: VerdictCalibrationPathRow): boolean {
  return (row.overriddenByAdmin ?? 0) + (row.overriddenByLateCompletion ?? 0) + (row.overriddenOther ?? 0) > 0;
}

function trimCell(cell: TrendCell | undefined): TrendCell {
  if (!cell) return { count: 0, sharePct: 0 };
  const { sessions: _sessions, ...rest } = cell;
  return rest;
}

/**
 * Pure: returns a new object; the input is not mutated. Non-object or unsuccessful payloads
 * (error envelopes) pass through unchanged so the caller's error handling still sees them.
 */
export function shapeVerdictCalibration(data: unknown, options: VerdictCalibrationShapeOptions = {}): unknown {
  if (!data || typeof data !== 'object' || !Array.isArray((data as { paths?: unknown }).paths)) return data;
  const payload = data as { paths: VerdictCalibrationPathRow[] } & Record<string, unknown>;

  const { minSharePct, top } = options;
  const kept: VerdictCalibrationPathRow[] = [];
  let omittedPaths = 0;
  let omittedSessions = 0;

  payload.paths.forEach((row, index) => {
    const belowShare = minSharePct != null && row.sharePct < minSharePct;
    const beyondTop = top != null && index >= top;
    if ((belowShare || beyondTop) && !hasOverrides(row)) {
      omittedPaths++;
      omittedSessions += row.count ?? 0;
      return;
    }
    kept.push({
      ...row,
      reEnrollRatePct: row.reEnrollRatePct ?? null,
      lift: row.lift ?? null,
      window7: trimCell(row.window7),
      baseline28: trimCell(row.baseline28),
    });
  });

  const shaped: Record<string, unknown> = { ...payload, paths: kept };
  if (omittedPaths > 0) {
    const criteria = [
      minSharePct != null ? `share < ${minSharePct}%` : null,
      top != null ? `rank > ${top}` : null,
    ].filter(Boolean).join(' or ');
    shaped.omitted = {
      paths: omittedPaths,
      sessions: omittedSessions,
      reason: `${omittedPaths} path(s) with ${criteria} dropped (rows carrying overrides are always kept); totals and trend still cover the full window.`,
    } satisfies VerdictCalibrationOmitted;
  }
  return shaped;
}
