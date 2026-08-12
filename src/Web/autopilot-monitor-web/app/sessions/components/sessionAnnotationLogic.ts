/**
 * Pure state helpers for the session-detail "Annotations" card. No React/JSX so
 * vitest can pin the role/lane matrix without rendering. Mirrors the backend gates:
 * UpsertSessionAnnotationFunction.IsLaneWritableByCaller (write matrix, tenant-role
 * lanes bound to the caller's OWN tenant) and
 * GetSessionAnnotationsFunction.FilterLanesForCaller (globaladmin lane is
 * platform-internal).
 */

export const ANNOTATION_LANES = ["operator", "tenantadmin", "globaladmin"] as const;
export type AnnotationLane = (typeof ANNOTATION_LANES)[number];

export const ANNOTATION_VERDICTS = [
  "root_cause_confirmed",
  "analysis_wrong",
  "different_problem",
  "inconclusive",
] as const;
export type AnnotationVerdict = (typeof ANNOTATION_VERDICTS)[number];

/** Matches the backend cap (SessionAnnotation.MaxNoteLength). */
export const ANNOTATION_MAX_NOTE_LENGTH = 4096;

/** Wire shape of one lane row. Backend omits null fields (WhenWritingNull) — treat null and undefined alike. */
export interface SessionAnnotationDto {
  lane: string;
  verdict?: string | null;
  note?: string | null;
  authorUpn?: string | null;
  authorDisplayName?: string | null;
  createdByUpn?: string | null;
  createdAtUtc?: string | null;
  updatedAtUtc?: string | null;
  ruleIds?: string[] | null;
}

/** The auth-context fields the matrix needs (subset of UserInfo). */
export interface AnnotationUser {
  isGlobalAdmin?: boolean;
  isGlobalReader?: boolean;
  isTenantAdmin?: boolean;
  role?: "Admin" | "Operator" | "Viewer" | null;
}

export const VERDICT_LABELS: Record<AnnotationVerdict, string> = {
  root_cause_confirmed: "Root cause confirmed",
  analysis_wrong: "Analysis wrong",
  different_problem: "Different problem",
  inconclusive: "Inconclusive",
};

/** Native-tooltip explanations (title attribute) — same wording as the customer docs. */
export const VERDICT_DESCRIPTIONS: Record<AnnotationVerdict, string> = {
  root_cause_confirmed: "The analysis was right",
  analysis_wrong: "The analysis pointed at the wrong cause",
  different_problem: "The real issue was something the rules did not cover",
  inconclusive: "The investigation ended without a clear answer",
};

/** Shared verdict pill styling (session card + annotations overview page). */
export const VERDICT_PILL_CLASSES: Record<string, string> = {
  root_cause_confirmed: "bg-green-100 text-green-800",
  analysis_wrong: "bg-red-100 text-red-800",
  different_problem: "bg-amber-100 text-amber-800",
  inconclusive: "bg-slate-100 text-slate-600",
};

export const LANE_LABELS: Record<AnnotationLane, string> = {
  operator: "Operator",
  tenantadmin: "Tenant Admin",
  // Only ever rendered for platform-scope users — the backend filters this lane out of
  // every tenant response. The label must read as INTERNAL, not as a message channel
  // from the vendor to the customer.
  globaladmin: "Internal (Autopilot Monitor)",
};

/**
 * Lanes the caller may SEE. The backend already filters the globaladmin lane for
 * non-global callers — this only decides which (possibly empty) lane sections the
 * card renders at all.
 */
export function visibleLanes(user: AnnotationUser | null | undefined): AnnotationLane[] {
  const hasGlobalScope = !!user?.isGlobalAdmin || !!user?.isGlobalReader;
  return hasGlobalScope
    ? ["operator", "tenantadmin", "globaladmin"]
    : ["operator", "tenantadmin"];
}

/**
 * Whether the caller may WRITE a lane. `isCrossTenantView` = the session belongs to
 * a different tenant than the caller's own (fleet/MSP/GA drill-in) — tenant-role
 * lanes bind to the caller's own tenant, so only the globaladmin lane stays
 * writable there (and only for a real Global Admin; Global Reader never writes).
 */
export function canWriteLane(
  lane: AnnotationLane,
  user: AnnotationUser | null | undefined,
  isCrossTenantView: boolean,
): boolean {
  if (!user) return false;
  if (lane === "globaladmin") return !!user.isGlobalAdmin;
  if (isCrossTenantView) return false;
  if (lane === "tenantadmin") return !!user.isTenantAdmin || !!user.isGlobalAdmin;
  // operator lane: Operator or Tenant Admin (admins supervise operator notes)
  return user.role === "Operator" || !!user.isTenantAdmin || !!user.isGlobalAdmin;
}

/** Null-safe note validation. Returns an error string or null when valid. */
export function validateNote(note: string): string | null {
  if (note.length > ANNOTATION_MAX_NOTE_LENGTH) {
    return `Note must be at most ${ANNOTATION_MAX_NOTE_LENGTH} characters (currently ${note.length}).`;
  }
  return null;
}

/**
 * PUT body for one lane. Empty strings become null; a body with BOTH fields null
 * clears the lane server-side (isClear tells the card to confirm/relabel the action).
 */
export function buildPutBody(
  verdict: string | null | undefined,
  note: string,
): { body: { verdict: string | null; note: string | null }; isClear: boolean } {
  const trimmedNote = note.trim();
  const normalizedVerdict = verdict == null || verdict === "" ? null : verdict;
  const body = {
    verdict: normalizedVerdict,
    note: trimmedNote === "" ? null : trimmedNote,
  };
  return { body, isClear: body.verdict == null && body.note == null };
}

/** True when the lane row carries any content (verdict or note). */
export function hasContent(annotation: SessionAnnotationDto | null | undefined): boolean {
  return !!annotation && (annotation.verdict != null || !!annotation.note);
}
