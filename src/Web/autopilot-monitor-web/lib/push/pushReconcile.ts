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
  /**
   * Drop the held subscription (if any), subscribe with the server's active key and PUT the
   * result with the active kid: the platform rotated its VAPID key (B-y4z).
   */
  | "rekey"
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
  /** A VAPID public key is available and a PushManager exists, so subscribe() can be called. */
  canSubscribe: boolean;
  /** The key id stored at pairing or the last re-key (the key the held subscription was made with), null when missing. */
  metaKid: string | null;
  /** PushDeviceStatusResponse.activeKid: the platform's current key id; null or "" while the channel is unconfigured. */
  activeKid: string | null;
  /** The server sent its active public key, a PushManager exists and permission is granted — the caller computes it. */
  canRekey: boolean;
}

/**
 * - Key rotation (B-y4z): the server names its current key (activeKid). When it differs from the
 *   kid the held subscription was made with (metaKid) and a fresh subscription can be made with
 *   the server's active key (canRekey), the device re-registers itself: "rekey" wins over every
 *   other decision, whatever the status or the endpoint comparison says, and the device token
 *   stays — a rotation is never a re-pair. Without canRekey (permission not granted, no key sent)
 *   the old key keeps working while the server retires it gracefully, so the decisions below
 *   stand. An empty activeKid (unconfigured channel, older server) never triggers it.
 * - Stale: the backend set it on 404/410 or vapid_key_mismatch and re-arms only on PUT. The held
 *   subscription is dead, so it is replaced when possible and otherwise PUT as it is; without any
 *   subscription the device needs a new permission grant.
 * - Paused: nothing special — the server resumes it on the owner's next portal sign-in; the
 *   subscription itself is handled like Active.
 * - Otherwise: a missing subscription is recreated when permission allows, a drifted endpoint
 *   is PUT, a matching one needs nothing.
 */
export function decideReconcileAction(input: ReconcileInput): ReconcileAction {
  if (input.activeKid && input.activeKid !== input.metaKid && input.canRekey) return "rekey";
  const canResubscribe = input.canSubscribe && input.permission === "granted";
  if (input.serverStatus === "Stale") {
    if (canResubscribe) return "resubscribe";
    return input.heldEndpoint ? "put" : "repair-required";
  }
  if (!input.heldEndpoint) return canResubscribe ? "resubscribe" : "repair-required";
  return input.heldEndpoint !== input.metaEndpoint ? "put" : "none";
}
