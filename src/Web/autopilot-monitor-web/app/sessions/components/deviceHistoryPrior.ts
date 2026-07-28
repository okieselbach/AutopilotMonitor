/**
 * Pure selection logic behind the DeviceHistoryBanner header count — kept free of React/Next
 * imports so the contract is unit-testable (pattern: collectLogsLogic.ts).
 */

export interface DeviceSessionRefDto {
  sessionId: string;
  startedAt: string;
  completedAt: string | null;
  status: string;
  enrollmentType: string;
  isPreProvisioned: boolean;
  durationSeconds: number | null;
  adminMarked: boolean;
}

/**
 * Chain entries that are genuinely PREVIOUS enrollments relative to the viewed session —
 * viewing an older session must not count its successors as "previous" (Codex review:
 * "Attempt 1 · 2 previous enrollments"). The current session's own chain entry wins as the
 * time anchor (terminal sessions); live sessions fall back to the session row's startedAt.
 * Ties break on sessionId, mirroring the server's chain sort (StartedAt + SessionId).
 * Without any anchor (should not happen — the page always has the session row) every other
 * entry counts, matching the pre-fix behavior.
 */
export function selectPriorSessions(
  chain: DeviceSessionRefDto[],
  sessionId: string,
  sessionStartedAt?: string
): DeviceSessionRefDto[] {
  const anchorStartedAt =
    chain.find((r) => r.sessionId === sessionId)?.startedAt ?? sessionStartedAt;
  const others = chain.filter((r) => r.sessionId !== sessionId);
  if (!anchorStartedAt) return others;
  const anchorTime = new Date(anchorStartedAt).getTime();
  return others.filter((r) => {
    const t = new Date(r.startedAt).getTime();
    return t < anchorTime || (t === anchorTime && r.sessionId < sessionId);
  });
}
