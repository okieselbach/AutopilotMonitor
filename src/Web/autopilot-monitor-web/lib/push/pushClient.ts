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
  META_KEYS,
  PAIR_COOKIE_NAME,
  readMeta,
  writeMeta,
  type PlatformInput,
} from "./pushCore";
import {
  deleteDevice,
  describePushError,
  getDevice,
  PushApiError,
  updateDeviceSubscription,
  type PushDeviceResponse,
} from "./pushApi";
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
  | { state: "ok"; device: PushDeviceResponse; meta: Record<string, string>; subscriptionPresent: boolean }
  | { state: "error"; message: string; meta: Record<string, string> };

/**
 * K14: on every open, compare the browser's subscription with what the server knows.
 * `pushsubscriptionchange` never fires on iOS, so this is the only repair path there.
 * The decision itself is pure (decideReconcileAction); this function carries it out: a Stale
 * row means the held subscription is dead, so it is replaced and the new keys are PUT whatever
 * the endpoint comparison says — the server re-arms a Stale row only on PUT.
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
  try {
    const registration = (await getPushRegistration()) ?? (isPushSupported() ? await registerPushWorker() : null);
    if (registration) {
      let subscription = await registration.pushManager.getSubscription();
      const vapidPublicKey = meta[META_KEYS.vapidPublicKey];
      const action = decideReconcileAction({
        serverStatus: device.status,
        heldEndpoint: subscription?.endpoint ?? null,
        metaEndpoint: meta[META_KEYS.endpoint] ?? null,
        permission: notificationPermission(),
        canSubscribe: Boolean(vapidPublicKey),
      });
      if (action === "resubscribe") {
        // The held one is dead (Stale) or absent; a failed subscribe() below leaves none.
        const held = subscription;
        subscription = null;
        if (held) await held.unsubscribe().catch(() => false);
        subscription = await subscribeToPush(registration, vapidPublicKey);
      }
      if (subscription) {
        subscriptionPresent = true;
        if (action === "put" || action === "resubscribe") {
          const keys = subscriptionKeys(subscription);
          device = await updateDeviceSubscription(token, { ...keys, kid: meta[META_KEYS.kid] ?? device.kid });
          await writeMeta({ [META_KEYS.endpoint]: keys.endpoint });
        }
      }
    }
  } catch (error) {
    if (isGone(error)) {
      await wipeLocalDevice().catch(() => {});
      return { state: "gone" };
    }
    // Subscription repair is best effort; the status page shows "subscription: no".
  }
  return { state: "ok", device, meta, subscriptionPresent };
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
