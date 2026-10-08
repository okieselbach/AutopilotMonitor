/**
 * Browser-side helpers of the receiver pages: service-worker registration, the push
 * subscription, the Safari hand-off cookie and the reconcile-on-open routine (K14). Every
 * function here touches window/navigator and is called from effects or event handlers only.
 */
import { API_BASE_URL } from "@/utils/config";
import {
  base64UrlToUint8Array,
  bufferToBase64Url,
  clearPushDb,
  entryFromNotification,
  META_KEYS,
  PAIR_COOKIE_NAME,
  readHistory,
  readMeta,
  readTrace,
  type PlatformInput,
  type TraceRecord,
  writeHistoryEntry,
  writeMeta,
} from "./pushCore";
import {
  deleteDevice,
  describePushError,
  getDevice,
  PushApiError,
  updateDeviceSubscription,
  type PushDeviceResponse,
} from "./pushApi";
import { describeNotificationData } from "./pushFormat";
import { decideReconcileAction } from "./pushReconcile";

export const SW_URL = "/push/sw.js";
export const SW_SCOPE = "/push/";

export function isPushSupported(): boolean {
  return (
    typeof window !== "undefined" &&
    "serviceWorker" in navigator &&
    "PushManager" in window &&
    "Notification" in window
  );
}

/** Home-screen app (iOS) or installed PWA (Android/desktop). */
export function isStandaloneDisplay(): boolean {
  if (typeof window === "undefined") return false;
  const nav = navigator as Navigator & { standalone?: boolean };
  if (nav.standalone === true) return true;
  return window.matchMedia("(display-mode: standalone)").matches;
}

export function platformInput(): PlatformInput {
  return { userAgent: navigator.userAgent, maxTouchPoints: navigator.maxTouchPoints };
}

export function notificationPermission(): NotificationPermission | "unsupported" {
  if (typeof window === "undefined" || !("Notification" in window)) return "unsupported";
  return Notification.permission;
}

export async function registerPushWorker(): Promise<ServiceWorkerRegistration> {
  await navigator.serviceWorker.register(SW_URL, { type: "module", scope: SW_SCOPE });
  return navigator.serviceWorker.ready;
}

export async function getPushRegistration(): Promise<ServiceWorkerRegistration | null> {
  if (!isPushSupported()) return null;
  const registration = await navigator.serviceWorker.getRegistration(SW_SCOPE);
  return registration ?? null;
}

export interface SubscriptionKeys {
  endpoint: string;
  p256dh: string;
  auth: string;
}

export function subscriptionKeys(subscription: PushSubscription): SubscriptionKeys {
  return {
    endpoint: subscription.endpoint,
    p256dh: bufferToBase64Url(subscription.getKey("p256dh")),
    auth: bufferToBase64Url(subscription.getKey("auth")),
  };
}

export function subscribeToPush(registration: ServiceWorkerRegistration, vapidPublicKey: string): Promise<PushSubscription> {
  return registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: base64UrlToUint8Array(vapidPublicKey),
  });
}

// --- Safari hand-off cookie (K12) -----------------------------------------------------------

const PAIR_COOKIE_MAX_AGE_SECONDS = 600;

export function readPairCookie(): string | null {
  if (typeof document === "undefined") return null;
  for (const part of document.cookie.split(";")) {
    const [name, ...rest] = part.trim().split("=");
    if (name === PAIR_COOKIE_NAME) return decodeURIComponent(rest.join("=")) || null;
  }
  return null;
}

export function writePairCookie(code: string): void {
  document.cookie = `${PAIR_COOKIE_NAME}=${encodeURIComponent(code)}; Secure; SameSite=Strict; Path=${SW_SCOPE}; Max-Age=${PAIR_COOKIE_MAX_AGE_SECONDS}`;
}

export function clearPairCookie(): void {
  document.cookie = `${PAIR_COOKIE_NAME}=; Secure; SameSite=Strict; Path=${SW_SCOPE}; Max-Age=0`;
}

// --- Storage persistence (K26) --------------------------------------------------------------

export async function requestPersistentStorage(): Promise<boolean> {
  try {
    if (!("storage" in navigator) || !navigator.storage.persist) return false;
    return await navigator.storage.persist();
  } catch {
    return false;
  }
}

export async function isStoragePersisted(): Promise<boolean | null> {
  try {
    if (!("storage" in navigator) || !navigator.storage.persisted) return null;
    return await navigator.storage.persisted();
  } catch {
    return null;
  }
}

// --- Device lifecycle -----------------------------------------------------------------------

/**
 * The notifications still in the notification centre are the one record outside IndexedDB of
 * what was delivered. On every open the page imports the ones the history lacks — a push the
 * worker could not persist (killed early, a stuck database) is then caught up as long as the
 * notification has not been cleared. Returns how many entries were added.
 */
export async function importDisplayedNotifications(): Promise<number> {
  const registration = await getPushRegistration().catch(() => null);
  if (!registration || typeof registration.getNotifications !== "function") return 0;
  let notifications: Notification[];
  try {
    notifications = await registration.getNotifications();
  } catch {
    return 0;
  }
  let added = 0;
  for (const notification of notifications) {
    const entry = entryFromNotification(notification, Date.now());
    if (!entry) continue;
    try {
      if (await writeHistoryEntry(entry)) added++;
    } catch {
      // No IndexedDB: nothing to import into.
    }
  }
  return added;
}

export interface DisplayedNotification {
  /** The history id behind the notification, or null when the entry rule does not recognise it. */
  id: string | null;
  title: string;
  tag: string | null;
  /** The shape of the Notification's data member (describeNotificationData) — what an import could work with. */
  data: string;
  /** The Notification's own timestamp as ISO, when the browser exposes one. */
  timestamp: string | null;
}

/** What the status page shows under Diagnostics: the local facts behind a push that did or did not reach the history. */
export interface PushDiagnostics {
  /** Registration state: active, installing, waiting, registered (no worker yet), none or unsupported. */
  worker: string;
  /** Whether the worker controls this page (its "history" message can reach it). */
  controlled: boolean;
  /** The notifications still in the notification centre, or null when the browser does not answer. */
  displayed: DisplayedNotification[] | null;
  /** Rows in the local history, or null when IndexedDB is not readable. */
  historyCount: number | null;
  /** The worker's own trace, newest first; empty when unreadable. */
  trace: TraceRecord[];
}

export async function collectPushDiagnostics(): Promise<PushDiagnostics> {
  if (!isPushSupported()) return { worker: "unsupported", controlled: false, displayed: null, historyCount: null, trace: [] };
  const registration = await getPushRegistration().catch(() => null);
  const worker = !registration
    ? "none"
    : registration.active
      ? "active"
      : registration.installing
        ? "installing"
        : registration.waiting
          ? "waiting"
          : "registered";
  let displayed: DisplayedNotification[] | null = null;
  if (registration && typeof registration.getNotifications === "function") {
    try {
      const now = Date.now();
      displayed = (await registration.getNotifications()).map((n) => {
        const stamped = n as Notification & { timestamp?: unknown };
        const timestamp = typeof stamped.timestamp === "number" && Number.isFinite(stamped.timestamp) ? new Date(stamped.timestamp).toISOString() : null;
        return { id: entryFromNotification(n, now)?.id ?? null, title: n.title, tag: n.tag || null, data: describeNotificationData(n.data), timestamp };
      });
    } catch {
      displayed = null;
    }
  }
  const [historyCount, trace] = await Promise.all([
    readHistory().then(
      (entries) => entries.length,
      () => null,
    ),
    readTrace().catch((): TraceRecord[] => []),
  ]);
  return { worker, controlled: navigator.serviceWorker.controller !== null, displayed, historyCount, trace };
}

/** Drops the subscription and every local trace; used by unpair, the wipe command and "device gone". */
export async function wipeLocalDevice(): Promise<void> {
  try {
    const registration = await getPushRegistration();
    const subscription = await registration?.pushManager.getSubscription();
    if (subscription) await subscription.unsubscribe();
  } catch {
    // The browser may already have dropped it; the server copy is what matters.
  }
  await clearPushDb();
}

export async function unpairDevice(): Promise<void> {
  const meta = await readMeta().catch(() => ({}) as Record<string, string>);
  const token = meta[META_KEYS.deviceToken];
  if (token) {
    try {
      await deleteDevice(token);
    } catch (error) {
      // 404/401: the server already forgot us. Anything else still clears the device locally;
      // the owner can remove the row from the portal's device list.
      if (!(error instanceof PushApiError)) throw error;
    }
  }
  await wipeLocalDevice();
}

export type ReconcileResult =
  | { state: "unpaired" }
  | { state: "gone" }
  | {
      state: "ok";
      device: PushDeviceResponse;
      meta: Record<string, string>;
      subscriptionPresent: boolean;
      /** Why the subscription repair (subscribe or PUT) failed on this open, or null — the status page shows it; it is retried on the next open. */
      repairError: string | null;
    }
  | { state: "error"; message: string; meta: Record<string, string> };

/**
 * K14: on every open, compare the browser's subscription with what the server knows.
 * `pushsubscriptionchange` never fires on iOS, so this is the only repair path there.
 * The decision itself is pure (decideReconcileAction); this function carries it out: a Stale
 * row means the held subscription is dead, so it is replaced and the new keys are PUT whatever
 * the endpoint comparison says — the server re-arms a Stale row only on PUT. A rotated platform
 * key (device.activeKid differs from the stored kid, B-y4z) is carried out the same way with the
 * server's active key: subscribe again, PUT with the active kid, store key and kid — a
 * re-registration on the next open, never a re-pair; the device token stays as it is.
 */
export async function reconcileDevice(): Promise<ReconcileResult> {
  const meta = await readMeta().catch(() => ({}) as Record<string, string>);
  const token = meta[META_KEYS.deviceToken];
  if (!token) return { state: "unpaired" };

  let device: PushDeviceResponse;
  try {
    device = await getDevice(token);
  } catch (error) {
    if (isGone(error)) {
      await wipeLocalDevice().catch(() => {});
      return { state: "gone" };
    }
    return { state: "error", message: describePushError(error), meta };
  }

  let subscriptionPresent = false;
  let repairError: string | null = null;
  try {
    const registration = (await getPushRegistration()) ?? (isPushSupported() ? await registerPushWorker() : null);
    if (registration) {
      let subscription = await registration.pushManager.getSubscription();
      const permission = notificationPermission();
      const vapidPublicKey = meta[META_KEYS.vapidPublicKey];
      // Empty while the channel is unconfigured; absent from a server older than B-y4z.
      const activeVapidPublicKey = device.activeVapidPublicKey || "";
      const action = decideReconcileAction({
        serverStatus: device.status,
        heldEndpoint: subscription?.endpoint ?? null,
        metaEndpoint: meta[META_KEYS.endpoint] ?? null,
        permission,
        canSubscribe: Boolean(vapidPublicKey || activeVapidPublicKey),
        metaKid: meta[META_KEYS.kid] ?? null,
        activeKid: device.activeKid || null,
        canRekey: Boolean(activeVapidPublicKey) && permission === "granted",
      });
      // A fresh subscription uses the server's active key after a rotation, and also when the
      // stored key is lost; otherwise the stored key, which is what the server's row expects.
      const useActiveKey = action === "rekey" || (action === "resubscribe" && !vapidPublicKey);
      const kid = useActiveKey ? device.activeKid : (meta[META_KEYS.kid] ?? device.kid);
      if (action === "rekey" || action === "resubscribe") {
        // The held one is dead (Stale), absent or made with a retired key; a failed subscribe() below leaves none.
        const held = subscription;
        subscription = null;
        if (held) await held.unsubscribe().catch(() => false);
        subscription = await subscribeToPush(registration, useActiveKey ? activeVapidPublicKey : vapidPublicKey);
      }
      if (subscription) {
        subscriptionPresent = true;
        if (action === "put" || action === "resubscribe" || action === "rekey") {
          const keys = subscriptionKeys(subscription);
          device = await updateDeviceSubscription(token, { ...keys, kid });
          const written: Record<string, string> = useActiveKey
            ? { [META_KEYS.endpoint]: keys.endpoint, [META_KEYS.kid]: kid, [META_KEYS.vapidPublicKey]: activeVapidPublicKey }
            : { [META_KEYS.endpoint]: keys.endpoint };
          await writeMeta(written);
          // The result carries what is stored now, not the snapshot read before the repair —
          // the status page's "Server key" row compares the kid in it with the active one.
          Object.assign(meta, written);
        }
      }
    }
  } catch (error) {
    if (isGone(error)) {
      await wipeLocalDevice().catch(() => {});
      return { state: "gone" };
    }
    // Subscription repair is best effort: the device keeps working on what the server knows,
    // and the status page names the failure so a wrong key or a refused endpoint is not silent.
    repairError = describePushError(error);
  }
  return { state: "ok", device, meta, subscriptionPresent, repairError };
}

function isGone(error: unknown): boolean {
  return error instanceof PushApiError && (error.status === 404 || error.status === 401);
}

/** Stored after a successful pairing so the worker can repair the subscription on its own. */
export function pairedMeta(input: {
  deviceToken: string;
  deviceId: string;
  endpoint: string;
  kid: string;
  vapidPublicKey: string;
  platform: string;
  label: string;
}): Record<string, string> {
  return {
    [META_KEYS.deviceToken]: input.deviceToken,
    [META_KEYS.deviceId]: input.deviceId,
    [META_KEYS.endpoint]: input.endpoint,
    [META_KEYS.kid]: input.kid,
    [META_KEYS.vapidPublicKey]: input.vapidPublicKey,
    [META_KEYS.platform]: input.platform,
    [META_KEYS.label]: input.label,
    [META_KEYS.pairedAt]: new Date().toISOString(),
    [META_KEYS.apiBaseUrl]: API_BASE_URL,
  };
}
