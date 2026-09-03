// Classification for the autopilot-device-validation "access-check" probe — the rights-less
// admin reconcile path where the multi-tenant app was pre-approved by someone else in the tenant.

export type AccessCheckOutcome = "reconciled" | "transient" | "absent";

export interface AccessCheckPayload {
  accessPresent?: boolean;
  isTransient?: boolean;
  /**
   * Dual app-reg self-service migration: true when this probe just auto-flipped the tenant's
   * homing to the new (primary) app registration (flag-gated, consent-verified server-side).
   * Orthogonal to the access classification — handled as a side signal, never part of
   * classifyAccessCheck.
   */
  homingFlipped?: boolean;
  /** The homing auto-flip was deferred (consent on the new app still propagating) — another probe converges. */
  appHomingPending?: boolean;
  /**
   * Optional Graph add-on roles the previous app holds in this tenant but the new app lacks;
   * the flip stays refused until the admin grants exactly these on the new app.
   */
  appHomingMissingRoles?: string[] | null;
}

/**
 * Classifies an access-check probe response into a reconcile outcome.
 *
 * Security-critical: an inconclusive (transient) or missing-permission (absent) probe must
 * NEVER reconcile to "enabled". Reconciling persists the validation gate bool, which opens the
 * agent hard gate — if we did that while the role is actually absent, the agent would pass the
 * first gate and then get stuck in an endless 503 at the real Graph call. Only an authoritative
 * `accessPresent === true` (with `isTransient` falsy) flips to "reconciled".
 *
 * @param ok       whether the HTTP response was ok (non-2xx => transient/retryable, not "absent")
 * @param payload  parsed JSON body (may be undefined when `ok` is false)
 */
export function classifyAccessCheck(
  ok: boolean,
  payload: AccessCheckPayload | undefined,
): AccessCheckOutcome {
  if (!ok) return "transient";
  if (!payload || payload.isTransient) return "transient";
  if (!payload.accessPresent) return "absent";
  return "reconciled";
}
