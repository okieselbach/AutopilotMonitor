/**
 * Service worker of the push receiver (registered with {type: "module", scope: "/push/"}).
 * It never intercepts fetches; its whole job is: show every push as a notification, keep the
 * local history, open the history on click, and repair the subscription when the browser
 * rotates it. The pure parts live in sw-core.js (unit-tested), this file is the glue.
 *
 * Contract (K25): display never waits for IndexedDB — `waitUntil(Promise.all([show, persist]))`
 * with persist failures swallowed and persist bounded in time; a payload that cannot be read
 * still shows the generic title, because a silent push costs the subscription on iOS after
 * three occurrences. On iOS the worker's showNotification throws inside a declarative push
 * event and the platform shows the proposed notification itself (worker trace, 2026-10-08), so
 * show() never rejects: a rejected waitUntil ended the event with the history write still in
 * flight, and entries were lost. lib/__tests__/pushSw.test.ts pins the order and the settling.
 */
import {
  base64UrlToUint8Array,
  bufferToBase64Url,
  clearPushDb,
  entryFragment,
  entryFromNotification,
  GENERIC_BODY,
  GENERIC_TITLE,
  isWipeCommand,
  META_KEYS,
  normalizePayload,
  planSubscriptionKeys,
  pushApiRequest,
  readMeta,
  toNotificationOptions,
  writeHistoryEntry,
  writeMeta,
  writeTrace,
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
  event.waitUntil(Promise.all([self.clients.claim(), settleWithin(writeTrace({ event: "activate" }))]));
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
 * @returns {Promise<string>} "shown", "fallback" or "failed: <error>" for the trace — never rejects
 */
async function show(entry) {
  try {
    await self.registration.showNotification(entry.title, toNotificationOptions(entry));
    return "shown";
  } catch (error) {
    try {
      await self.registration.showNotification(GENERIC_TITLE, { body: GENERIC_BODY });
      return "fallback";
    } catch (second) {
      const first = describeError(error);
      const again = describeError(second);
      return "failed: " + (again === first ? first : first + " / " + again);
    }
  }
}

/**
 * Runs the work for at most PERSIST_TIMEOUT_MS; resolves either way, never rejects.
 * @param {Promise<unknown>} work
 * @returns {Promise<void>}
 */
function settleWithin(work) {
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  let timer;
  /** @type {Promise<void>} */
  const timeout = new Promise((resolve) => {
    timer = setTimeout(resolve, PERSIST_TIMEOUT_MS);
  });
  return Promise.race([work, timeout])
    .then(
      () => undefined,
      () => undefined,
    )
    .finally(() => clearTimeout(timer));
}

/**
 * @param {unknown} error
 * @returns {string}
 */
function describeError(error) {
  if (error instanceof Error) return error.name + (error.message ? ": " + error.message : "");
  return String(error);
}

/**
 * Best-effort history write plus its trace record, bounded in time as one; never rejects. The
 * record says what became of the entry ("added", "duplicate", "error: …"); when the database
 * never answers, the missing record is the evidence the status page shows.
 * @param {import("./sw-core.js").HistoryEntry} entry
 * @param {Record<string, unknown>} fields the record's fields besides id, type, result and shown
 * @param {Promise<string>} [shown] the display outcome — already in flight and never rejecting, awaited only for the record
 * @returns {Promise<void>}
 */
function persist(entry, fields, shown) {
  return settleWithin(
    (async () => {
      /** @type {string} */
      let result;
      try {
        result = (await writeHistoryEntry(entry)) ? "added" : "duplicate";
      } catch (error) {
        result = "error: " + describeError(error);
      }
      if (result === "added") await notifyWindows("history").catch(() => {});
      const display = shown ? await shown.catch(() => "failed") : undefined;
      await writeTrace({ ...fields, id: entry.id, type: entry.type, result, shown: display });
    })(),
  );
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
 * @param {string} source which event member carried the payload (for the trace)
 */
function handlePush(event, raw, source) {
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
  const shown = show(entry);
  event.waitUntil(Promise.all([shown, persist(entry, { event: "push", source }, shown)]));
}

// Declarative Web Push with "mutable": true (Push API draft; Safari / iOS ≥ 18.4, verified on a
// device 2026-10-08): the regular push event carries the proposed Notification in
// event.notification and event.data is null. The worker shows the same content itself, so the
// proposed notification is dropped and exactly one appears, and the history gets its entry at
// arrival instead of only after a tap. Every other browser hands the raw JSON as event.data.
self.addEventListener("push", (event) => {
  const declared = event.notification ?? null;
  handlePush(event, declared ?? safeParse(event.data), declared ? "notification" : event.data ? "data" : "none");
});

// The earlier WebKit shape of the same mechanism: a separate pushnotification event with the
// proposed notification (kept until no supported Safari fires it).
self.addEventListener("pushnotification", (event) => {
  const proposed = event.notification ?? event.proposedNotification ?? null;
  handlePush(event, proposed ?? safeParse(event.data ?? null), proposed ? "pushnotification" : event.data ? "data" : "none");
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const entry = entryFromNotification(event.notification, Date.now());
  const id = entry ? entry.id : null;
  const target = new URL("/push/" + (id ? entryFragment(id) : ""), self.location.origin).toString();
  event.waitUntil(
    (async () => {
      // The push handler may have lost the race against IndexedDB, or never ran: the click writes
      // the entry if missing — bounded like every history write, so a stuck database cannot keep
      // the tap from opening the page.
      await (entry ? persist(entry, { event: "click" }) : settleWithin(writeTrace({ event: "click", result: "no entry" })));
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

/** Network calls from the worker are bounded; a hanging push API must not pin the event. */
const FETCH_TIMEOUT_MS = 10000;

/**
 * @param {RequestInit} init
 * @returns {RequestInit}
 */
function bounded(init) {
  return typeof AbortSignal !== "undefined" && typeof AbortSignal.timeout === "function"
    ? { ...init, signal: AbortSignal.timeout(FETCH_TIMEOUT_MS) }
    : init;
}

/**
 * The device's status as the server sees it, or null when the server did not answer — the
 * repair then falls back to the stored key.
 * @param {string} apiBaseUrl
 * @param {string} token
 * @returns {Promise<{ activeKid?: unknown, activeVapidPublicKey?: unknown } | null>}
 */
async function fetchDeviceStatus(apiBaseUrl, token) {
  try {
    const { url, init } = pushApiRequest(apiBaseUrl, "/api/push/device", { method: "GET", token });
    const response = await fetch(url, bounded(init));
    return response.ok ? await response.json() : null;
  } catch {
    return null;
  }
}

// Chrome ≥ 138 and Firefox rotate subscriptions here; iOS never fires it (the pages reconcile on
// open). The server's active key decides what to subscribe with — a key rotation may have
// happened while no page was open — and the stored key is the fallback when the server is silent.
self.addEventListener("pushsubscriptionchange", (event) => {
  event.waitUntil(
    (async () => {
      /** @type {string} */
      let result;
      try {
        result = await repairSubscription(event);
      } catch (error) {
        result = "error: " + describeError(error);
      }
      await settleWithin(writeTrace({ event: "subscriptionchange", result }));
    })(),
  );
});

/**
 * @param {PushSubscriptionChangeEvent} event
 * @returns {Promise<string>} what happened, for the trace
 */
async function repairSubscription(event) {
  const meta = await readMeta();
  const token = meta[META_KEYS.deviceToken];
  const apiBaseUrl = meta[META_KEYS.apiBaseUrl];
  if (!token || !apiBaseUrl) return "not paired";
  const plan = planSubscriptionKeys(meta, await fetchDeviceStatus(apiBaseUrl, token));
  if (!plan) return "no key";
  // The browser's replacement subscription was made with the old key; after a rotation it is dropped.
  let subscription = plan.rekey ? null : (event.newSubscription ?? null);
  if (plan.rekey && event.newSubscription) await event.newSubscription.unsubscribe().catch(() => false);
  if (!subscription) {
    subscription = await self.registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: base64UrlToUint8Array(plan.publicKey),
    });
  }
  const body = {
    endpoint: subscription.endpoint,
    p256dh: bufferToBase64Url(subscription.getKey("p256dh")),
    auth: bufferToBase64Url(subscription.getKey("auth")),
    kid: plan.kid,
  };
  const { url, init } = pushApiRequest(apiBaseUrl, "/api/push/device", { method: "PUT", token, body });
  const response = await fetch(url, bounded(init));
  if (!response.ok) return "server " + response.status;
  await writeMeta({
    [META_KEYS.endpoint]: subscription.endpoint,
    [META_KEYS.kid]: plan.kid,
    [META_KEYS.vapidPublicKey]: plan.publicKey,
  });
  return plan.rekey ? "rekeyed" : "updated";
}
