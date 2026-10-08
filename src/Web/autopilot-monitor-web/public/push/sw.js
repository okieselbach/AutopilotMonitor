/**
 * Service worker of the push receiver (registered with {type: "module", scope: "/push/"}).
 * It never intercepts fetches; its whole job is: show every push as a notification, keep the
 * local history, open the history on click, and repair the subscription when the browser
 * rotates it. The pure parts live in sw-core.js (unit-tested), this file is the glue.
 *
 * Contract (K25): display never waits for IndexedDB — `waitUntil(Promise.all([show, persist]))`
 * with persist failures swallowed and persist bounded in time; a payload that cannot be read
 * still shows the generic title, because a silent push costs the subscription on iOS after
 * three occurrences. lib/__tests__/pushSw.test.ts pins the order and the settling.
 */
import {
  base64UrlToUint8Array,
  bufferToBase64Url,
  clearPushDb,
  entryFragment,
  GENERIC_BODY,
  GENERIC_TITLE,
  isWipeCommand,
  META_KEYS,
  normalizePayload,
  pushApiRequest,
  readMeta,
  toNotificationOptions,
  writeHistoryEntry,
  writeMeta,
} from "./sw-core.js";

const CHANNEL = "am-push";

/**
 * An IndexedDB open that never answers (a stuck versionchange, a blocked request whose
 * `onblocked` never fires) would otherwise keep the push event's waitUntil pending until the
 * browser kills the worker. The notification is shown by then; the history write is retried by
 * the click handler, so giving up is cheap.
 */
const PERSIST_TIMEOUT_MS = 5000;

self.addEventListener("install", () => {
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  event.waitUntil(self.clients.claim());
});

/**
 * @param {PushMessageData | null} data
 * @returns {unknown}
 */
function safeParse(data) {
  if (!data) return null;
  try {
    return data.json();
  } catch {
    try {
      return data.text();
    } catch {
      return null;
    }
  }
}

/**
 * @param {string} type
 * @param {Record<string, unknown>} [detail]
 */
async function notifyWindows(type, detail = {}) {
  const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
  for (const client of windows) client.postMessage({ channel: CHANNEL, type, ...detail });
}

/**
 * @param {import("./sw-core.js").HistoryEntry} entry
 */
async function show(entry) {
  try {
    await self.registration.showNotification(entry.title, toNotificationOptions(entry));
  } catch {
    await self.registration.showNotification(GENERIC_TITLE, { body: GENERIC_BODY });
  }
}

/**
 * Best-effort history write, bounded in time; never rejects (see PERSIST_TIMEOUT_MS).
 * @param {import("./sw-core.js").HistoryEntry} entry
 * @returns {Promise<void>}
 */
function persist(entry) {
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  let timer;
  const write = (async () => {
    const added = await writeHistoryEntry(entry);
    if (added) await notifyWindows("history");
  })();
  /** @type {Promise<void>} */
  const timeout = new Promise((resolve) => {
    timer = setTimeout(resolve, PERSIST_TIMEOUT_MS);
  });
  return Promise.race([write, timeout])
    .catch(() => {})
    .finally(() => clearTimeout(timer));
}

/** The wipe command: forget the device locally and drop the subscription. */
async function wipe() {
  try {
    const subscription = await self.registration.pushManager.getSubscription();
    if (subscription) await subscription.unsubscribe();
  } catch {
    // Nothing to unsubscribe, or the browser already did.
  }
  await clearPushDb();
  await notifyWindows("wiped");
}

/**
 * @param {ExtendableEvent} event
 * @param {unknown} raw
 */
function handlePush(event, raw) {
  const entry = normalizePayload(raw, Date.now());
  if (isWipeCommand(entry)) {
    const shown = {
      ...entry,
      title: "This device was unpaired",
      body: entry.body === GENERIC_BODY ? "Alerts to this device were switched off." : entry.body,
    };
    event.waitUntil(Promise.all([show(shown), wipe().catch(() => {})]));
    return;
  }
  // show() is called first and synchronously reaches showNotification; persist() cannot delay it.
  event.waitUntil(Promise.all([show(entry), persist(entry)]));
}

self.addEventListener("push", (event) => {
  handlePush(event, safeParse(event.data));
});

// Safari / iOS ≥ 18.4: Declarative Web Push with "mutable": true wakes the worker with the
// proposed notification instead of a payload (event name verified at interop, K25).
self.addEventListener("pushnotification", (event) => {
  const proposed = event.notification ?? event.proposedNotification ?? null;
  handlePush(event, proposed ?? safeParse(event.data ?? null));
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const data = event.notification.data ?? {};
  const id = typeof data.id === "string" ? data.id : null;
  const entry = data.entry && typeof data.entry === "object" ? data.entry : null;
  const target = new URL("/push/" + (id ? entryFragment(id) : ""), self.location.origin).toString();
  event.waitUntil(
    (async () => {
      // The push handler may have lost the race against IndexedDB; the click writes the entry if missing.
      if (entry) {
        try {
          await writeHistoryEntry(entry);
        } catch {
          // Display already happened; the history is best effort.
        }
      }
      const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
      for (const client of windows) {
        if (!new URL(client.url).pathname.startsWith("/push")) continue;
        await client.focus();
        client.postMessage({ channel: CHANNEL, type: "open", id });
        return;
      }
      await self.clients.openWindow(target);
    })(),
  );
});

// Chrome ≥ 138 and Firefox rotate subscriptions here; iOS never fires it (the pages reconcile on open).
self.addEventListener("pushsubscriptionchange", (event) => {
  event.waitUntil(
    (async () => {
      const meta = await readMeta();
      const token = meta[META_KEYS.deviceToken];
      const apiBaseUrl = meta[META_KEYS.apiBaseUrl];
      const vapidPublicKey = meta[META_KEYS.vapidPublicKey];
      if (!token || !apiBaseUrl) return;
      let subscription = event.newSubscription ?? null;
      if (!subscription && vapidPublicKey) {
        subscription = await self.registration.pushManager.subscribe({
          userVisibleOnly: true,
          applicationServerKey: base64UrlToUint8Array(vapidPublicKey),
        });
      }
      if (!subscription) return;
      const body = {
        endpoint: subscription.endpoint,
        p256dh: bufferToBase64Url(subscription.getKey("p256dh")),
        auth: bufferToBase64Url(subscription.getKey("auth")),
        kid: meta[META_KEYS.kid] ?? "",
      };
      const { url, init } = pushApiRequest(apiBaseUrl, "/api/push/device", { method: "PUT", token, body });
      const response = await fetch(url, init);
      if (response.ok) await writeMeta({ [META_KEYS.endpoint]: subscription.endpoint });
    })(),
  );
});
