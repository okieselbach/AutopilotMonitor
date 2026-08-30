// Pure view logic for the IME Pattern Health page (no React, unit-tested).
// Mirrors the backend contract of GET /api/metrics/ime-pattern-health
// (ImePatternHealthResponse in AutopilotMonitor.Shared.Models).

export interface ImePatternHealthVersion {
  version: string;
  sessions: number;
  firstSeenAt?: string | null;
  lastSeenAt?: string | null;
  fleetSessions?: number | null;
}

export interface ImePatternHealthPattern {
  patternId: string;
  category?: string | null;
  enabled: boolean;
  baselineRate?: number | null;
  expected: boolean;
}

export interface ImePatternHealthCell {
  version: string;
  patternId: string;
  sessions: number;
  sessionsWithHit: number;
  hits: number;
  rate: number;
  driftFlaggedAt?: string | null;
}

export interface ImePatternDriftAlert {
  version: string;
  patternId: string;
  baselineVersion: string;
  baselineRate: number;
  sessions: number;
  flaggedAt?: string | null;
}

export interface ImePatternHealthResponse {
  baselineVersion?: string | null;
  minBaselineSessions: number;
  expectedHitRate: number;
  minCandidateSessions: number;
  versions: ImePatternHealthVersion[];
  patterns: ImePatternHealthPattern[];
  cells: ImePatternHealthCell[];
  alerts: ImePatternDriftAlert[];
  generatedAt: string;
}

/** How a matrix cell reads at a glance. */
export type CellState = "drift" | "silent" | "low" | "ok" | "few" | "none";

export interface MatrixCell {
  cell: ImePatternHealthCell | null;
  state: CellState;
}

export interface MatrixRow {
  pattern: ImePatternHealthPattern;
  cells: MatrixCell[];
  /** Sort weight: drift first, then silent expected patterns, then rate drops. */
  weight: number;
}

export function cellKey(version: string, patternId: string): string {
  return `${version.toLowerCase()}|${patternId.toLowerCase()}`;
}

/**
 * Classifies one cell against the pattern's baseline expectation.
 * - drift: backend flagged it (one ops event per cell)
 * - silent: expected pattern, enough sessions, zero hits (drift candidate before the threshold, or unflagged)
 * - low: expected pattern whose rate fell below half the expected rate
 * - few: fewer sessions than the candidate threshold — not judged
 * - ok: everything else with data
 * - none: no data on that version
 */
export function classifyCell(
  cell: ImePatternHealthCell | null,
  pattern: ImePatternHealthPattern,
  minCandidateSessions: number,
  expectedHitRate: number,
): CellState {
  if (!cell) return "none";
  if (cell.driftFlaggedAt) return "drift";
  if (cell.sessions < minCandidateSessions) return "few";
  if (pattern.expected && cell.sessionsWithHit === 0) return "silent";
  if (pattern.expected && cell.rate < expectedHitRate / 2) return "low";
  return "ok";
}

const STATE_WEIGHT: Record<CellState, number> = { drift: 1000, silent: 100, low: 10, ok: 0, few: 0, none: 0 };

/** Builds the pattern × version matrix; rows sorted by severity, then pattern id. */
export function buildMatrix(data: ImePatternHealthResponse, includeDisabled = false): MatrixRow[] {
  const byKey = new Map<string, ImePatternHealthCell>();
  for (const c of data.cells) byKey.set(cellKey(c.version, c.patternId), c);

  const rows: MatrixRow[] = [];
  for (const pattern of data.patterns) {
    if (!includeDisabled && !pattern.enabled) continue;
    const cells: MatrixCell[] = data.versions.map((v) => {
      const cell = byKey.get(cellKey(v.version, pattern.patternId)) ?? null;
      return { cell, state: classifyCell(cell, pattern, data.minCandidateSessions, data.expectedHitRate) };
    });
    const weight = cells.reduce((acc, c) => acc + STATE_WEIGHT[c.state], 0);
    rows.push({ pattern, cells, weight });
  }
  rows.sort((a, b) => b.weight - a.weight || a.pattern.patternId.localeCompare(b.pattern.patternId));
  return rows;
}

export function formatRate(rate: number | null | undefined): string {
  if (rate === null || rate === undefined || Number.isNaN(rate)) return "—";
  return `${Math.round(rate * 100)}%`;
}

/** Versions ordered newest-first as delivered; the baseline is marked by the caller. */
export function countStates(rows: MatrixRow[]): Record<CellState, number> {
  const counts: Record<CellState, number> = { drift: 0, silent: 0, low: 0, ok: 0, few: 0, none: 0 };
  for (const r of rows) for (const c of r.cells) counts[c.state]++;
  return counts;
}
