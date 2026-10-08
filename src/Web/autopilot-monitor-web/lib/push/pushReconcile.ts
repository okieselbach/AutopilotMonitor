/**
 * The pure core of the reconcile-on-open routine (K14, lib/push/pushClient.ts reconcileDevice):
 * given what the server says about the device and what the browser holds, decide what to do.
 * No browser API is touched here so the decision is unit-tested (lib/__tests__/pushReconcile.test.ts).
 *
 * "Gone" (GET push/device answered 404/401) is decided by the caller before this runs: there is
 * no device to reconcile against.
 */

export type ReconcileAction =
  /** Nothing to send: the server knows this subscription. */
  | "none"
  /** PUT the held subscription's keys as they are (endpoint drift, or re-arming a Stale row). */
  | "put"
  /** Drop the held subscription (if any), subscribe again with the stored key and PUT the result. */
  | "resubscribe"
  /** No subscription and no way to create one (permission not granted / no key): the status page says so. */
  | "repair-required";

export interface ReconcileInput {
  /** PushDeviceStatusResponse.status: Pending · Active · Paused · Stale. */
  serverStatus: string;
  /** Endpoint of the browser's current subscription, null when it holds none. */
  heldEndpoint: string | null;
  /** The endpoint stored after the last successful pairing/PUT (what the server knows). */
  metaEndpoint: string | null;
  permission: NotificationPermission | "unsupported";
  /** A VAPID public key is stored and a PushManager exists, so subscribe() can be called. */
  canSubscribe: boolean;
}

/**
 * - Stale: the backend set it on 404/410 or vapid_key_mismatch and re-arms only on PUT. The held
 *   subscription is dead, so it is replaced when possible and otherwise PUT as it is; without any
 *   subscription the device needs a new permission grant.
 * - Paused: nothing special — the server resumes it on the owner's next portal sign-in; the
 *   subscription itself is handled like Active.
 * - Otherwise: a missing subscription is recreated when permission allows, a drifted endpoint
 *   is PUT, a matching one needs nothing.
 */
export function decideReconcileAction(input: ReconcileInput): ReconcileAction {
  const canResubscribe = input.canSubscribe && input.permission === "granted";
  if (input.serverStatus === "Stale") {
    if (canResubscribe) return "resubscribe";
    return input.heldEndpoint ? "put" : "repair-required";
  }
  if (!input.heldEndpoint) return canResubscribe ? "resubscribe" : "repair-required";
  return input.heldEndpoint !== input.metaEndpoint ? "put" : "none";
}
