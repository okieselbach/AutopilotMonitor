/**
 * Pure logic of the push receiver, shared by the service worker (/push/sw.js imports it as a
 * module) and the receiver pages (lib/push/pushCore.ts re-exports it). Nothing here touches a
 * DOM, window or worker global at import time: the IndexedDB helpers reach for `indexedDB` only
 * when called, so lib/__tests__/pushCore.test.ts can import the module under node.
 *
 * Typed with JSDoc so `tsc --strict` sees real types through allowJs; keep the annotations when
 * changing a signature.
 */

export const DB_NAME = "am-push";
export const DB_VERSION = 1;
export const HISTORY_STORE = "history";
export const HISTORY_TS_INDEX = "ts";
export const META_STORE = "meta";

export const HISTORY_MAX_ENTRIES = 200;
/** Retention in days, chosen by the user on the status page; 0 keeps entries until the cap. */
export const HISTORY_RETENTION_DEFAULT_DAYS = 30;
export const HISTORY_RETENTION_MAX_DAYS = 365;
export const HISTORY_MAX_AGE_MS = HISTORY_RETENTION_DEFAULT_DAYS * 24 * 60 * 60 * 1000;

/** The backend caps the label at 40 characters (K13). */
export const LABEL_MAX_CHARS = 40;
/** Sent as appVersion when pairing (≤ 32 characters, K13); bump when the worker contract changes. */
export const APP_VERSION = "web-1";

export const PAIRING_CODE_LENGTH = 11;
/** Crockford base32 without I, L, O, U (K8): typeable, and never spells code=/state=/error. */
export const PAIRING_CODE_ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
const PAIRING_CODE_PATTERN = /^[0-9A-HJKMNP-TV-Z]{11}$/;

export const PAIR_COOKIE_NAME = "am_push_pair";

/** Closed platform list the backend accepts (K13). */
export const PLATFORMS = /** @type {const} */ ([
  "ios-homescreen",
  "android-chrome",
  "windows-chromium",
  "macos-safari",
  "firefox",
  "other",
]);

export const SEVERITIES = /** @type {const} */ (["error", "warning", "success", "info"]);

/** Shown when a push carries no readable payload — a silent push would cost the subscription on iOS. */
export const GENERIC_TITLE = "Autopilot Monitor";
export const GENERIC_BODY = "New alert";

/** Payload types with a local side effect in the worker. */
export const WIPE_TYPE = "push_revoked";

export const META_KEYS = /** @type {const} */ ({
  deviceToken: "deviceToken",
  deviceId: "deviceId",
  endpoint: "endpoint",
  kid: "kid",
  vapidPublicKey: "vapidPublicKey",
  platform: "platform",
  label: "label",
  pairedAt: "pairedAt",
  /** The pages know the API origin from their build; the worker learns it from here. */
  apiBaseUrl: "apiBaseUrl",
  /** Days the history is kept (string of an integer 0–365; 0 = until the entry cap; absent = default). */
  historyRetentionDays: "historyRetentionDays",
  /** The history page's chosen filter key ("all" or "<group>:<kind>"); absent = all. */
  historyFilter: "historyFilter",
});

/** @typedef {(typeof PLATFORMS)[number]} Platform */
/** @typedef {(typeof SEVERITIES)[number]} Severity */

/**
 * @typedef {object} Fact
 * @property {string} name
 * @property {string} value
 */

/**
 * One row of the local history (IndexedDB store "history", keyPath "id").
 * @typedef {object} HistoryEntry
 * @property {string} id
 * @property {string} ts ISO timestamp the entry sorts by
 * @property {string} title
 * @property {string} body
 * @property {string} type
 * @property {Severity} severity
 * @property {Fact[]} facts
 * @property {string | null} portalUrl
 * @property {string | null} navigate
 * @property {string | null} tag
 * @property {string} scope
 * @property {boolean} generic true when the payload could not be read and the generic text was used
 */

/** @typedef {{ kind: "pair", code: string } | { kind: "entry", id: string }} FragmentTarget */

/**
 * @typedef {object} PushApiRequest
 * @property {string} url
 * @property {RequestInit} init
 */

// ---------------------------------------------------------------------------
// Pairing code
// ---------------------------------------------------------------------------

/**
 * Normalises what a person typed or scanned: uppercase, spaces and hyphens removed, the
 * look-alikes O→0 and I/L→1 corrected. Returns null unless the result is a valid code.
 * @param {string | null | undefined} raw
 * @returns {string | null}
 */
export function normalizePairingCode(raw) {
  if (typeof raw !== "string") return null;
  const cleaned = raw
    .toUpperCase()
    .replace(/[\s-]+/g, "")
    .replace(/O/g, "0")
    .replace(/[IL]/g, "1");
  return PAIRING_CODE_PATTERN.test(cleaned) ? cleaned : null;
}

/**
 * @param {string} code
 * @returns {boolean}
 */
export function isValidPairingCode(code) {
  return PAIRING_CODE_PATTERN.test(code);
}

/**
 * Reads the receiver's URL fragment grammar: `#p=<code>` (pairing hand-off) and `#e/<id>`
 * (deep link into one history entry). Anything else is nobody's business and yields null.
 * @param {string} hash location.hash, with or without the leading "#"
 * @returns {FragmentTarget | null}
 */
export function parseFragment(hash) {
  const value = (hash || "").replace(/^#/, "");
  if (value.startsWith("p=")) {
    const code = normalizePairingCode(decodeURIComponent(value.slice(2)));
    return code ? { kind: "pair", code } : null;
  }
  if (value.startsWith("e/")) {
    const id = value.slice(2);
    return /^[A-Za-z0-9-]{1,64}$/.test(id) ? { kind: "entry", id } : null;
  }
  return null;
}

/**
 * @param {string} id
 * @returns {string} the fragment that opens this entry on the history page
 */
export function entryFragment(id) {
  return `#e/${id}`;
}

// ---------------------------------------------------------------------------
// Payload normalisation
// ---------------------------------------------------------------------------

/**
 * Control characters become whitespace, whitespace runs collapse, the result is trimmed and
 * capped: push text is rendered as text nodes only, but it must not carry terminal escapes
 * or zero-width padding into the history either.
 * @param {unknown} value
 * @param {number} max
 * @returns {string}
 */
function cleanText(value, max) {
  if (typeof value !== "string") return "";
  let visible = "";
  for (const ch of value) {
    const code = ch.charCodeAt(0);
    visible += code < 32 || code === 127 ? " " : ch;
  }
  const cleaned = visible.replace(/\s+/g, " ").trim();
  return cleaned.length > max ? cleaned.slice(0, max) : cleaned;
}

/**
 * @param {unknown} value
 * @returns {string | null} an absolute http(s) URL or null
 */
function cleanUrl(value) {
  if (typeof value !== "string" || value.length > 2048) return null;
  try {
    const url = new URL(value);
    return url.protocol === "https:" || url.protocol === "http:" ? url.toString() : null;
  } catch {
    return null;
  }
}

/**
 * @param {unknown} value
 * @returns {value is Record<string, unknown>}
 */
function isRecord(value) {
  return typeof value === "object" && value !== null;
}

/**
 * @param {unknown} value
 * @returns {Severity}
 */
export function normalizeSeverity(value) {
  return typeof value === "string" && /** @type {readonly string[]} */ (SEVERITIES).includes(value)
    ? /** @type {Severity} */ (value)
    : "info";
}

/**
 * Turns whatever the push event delivered into one history entry. Accepts the full payload
 * (`{web_push, mutable, notification: {...}}`), the bare notification object (Safari hands the
 * worker `event.notification` / `event.proposedNotification`; its `data` is the same object),
 * a JSON string, or garbage — the last case yields the generic entry, never nothing.
 * @param {unknown} input
 * @param {number} nowMs timestamp used for the fallback id and the fallback ts
 * @returns {HistoryEntry}
 */
export function normalizePayload(input, nowMs) {
  let value = input;
  if (typeof value === "string") {
    try {
      value = JSON.parse(value);
    } catch {
      value = null;
    }
  }
  const notification = isRecord(value) && isRecord(value.notification) ? value.notification : value;
  const data = isRecord(notification) && isRecord(notification.data) ? notification.data : null;

  const title = cleanText(isRecord(notification) ? notification.title : null, 120);
  const body = cleanText(isRecord(notification) ? notification.body : null, 500);
  const generic = title === "" && body === "";

  const idFromData = data ? cleanText(data.id, 64) : "";
  const tsFromData = data ? cleanText(data.ts, 40) : "";
  const tsValid = tsFromData !== "" && !Number.isNaN(Date.parse(tsFromData));

  /** @type {Fact[]} */
  const facts = [];
  if (data && Array.isArray(data.facts)) {
    for (const fact of data.facts) {
      if (!isRecord(fact)) continue;
      const name = cleanText(fact.name, 64);
      const factValue = cleanText(fact.value, 64);
      if (name === "" || factValue === "") continue;
      facts.push({ name, value: factValue });
      if (facts.length >= 12) break;
    }
  }

  return {
    id: idFromData !== "" ? idFromData : `local-${nowMs}`,
    ts: tsValid ? new Date(tsFromData).toISOString() : new Date(nowMs).toISOString(),
    title: generic ? GENERIC_TITLE : title || GENERIC_TITLE,
    body: generic ? GENERIC_BODY : body,
    type: cleanText(data ? data.type : null, 64) || "unknown",
    severity: normalizeSeverity(data ? data.severity : null),
    facts,
    portalUrl: cleanUrl(data ? data.portalUrl : null),
    navigate: cleanUrl(isRecord(notification) ? notification.navigate : null),
    tag: cleanText(isRecord(notification) ? notification.tag : null, 64) || null,
    scope: cleanText(data ? data.scope : null, 16) || "tenant",
    generic,
  };
}

/**
 * @param {HistoryEntry} entry
 * @returns {boolean} true for the wipe command (push_revoked): the device must forget everything
 */
export function isWipeCommand(entry) {
  return entry.type === WIPE_TYPE;
}

/**
 * Options for `registration.showNotification(entry.title, …)`. The whole entry travels in
 * `data` so a click can re-persist it when the push handler could not.
 * @param {HistoryEntry} entry
 * @returns {NotificationOptions}
 */
export function toNotificationOptions(entry) {
  /** @type {NotificationOptions} */
  const options = {
    body: entry.body,
    icon: "/push/icon-192.png",
    data: { id: entry.id, entry },
  };
  if (entry.tag) options.tag = entry.tag;
  return options;
}

// ---------------------------------------------------------------------------
// History retention
// ---------------------------------------------------------------------------

/**
 * Retention rule, applied on every write and on every open: entries older than 30 days go,
 * then only the newest 200 stay. Returns a new array, newest first; entries whose ts does not
 * parse are dropped as unreadable.
 * @param {HistoryEntry[]} entries
 * @param {number} nowMs
 * @returns {HistoryEntry[]}
 */
export function pruneHistory(entries, nowMs, retentionDays = HISTORY_RETENTION_DEFAULT_DAYS) {
  const days = normalizeRetentionDays(retentionDays);
  const cutoff = days === 0 ? Number.NEGATIVE_INFINITY : nowMs - days * 24 * 60 * 60 * 1000;
  const kept = entries
    .map((entry) => ({ entry, time: Date.parse(entry.ts) }))
    .filter((item) => !Number.isNaN(item.time) && item.time >= cutoff)
    .sort((a, b) => b.time - a.time)
    .slice(0, HISTORY_MAX_ENTRIES);
  return kept.map((item) => item.entry);
}

/**
 * The user's retention choice as the pages store it and the worker reads it: an integer 0–365
 * (0 = keep until the entry cap). Anything else — absent, non-numeric, fractional, out of range —
 * is the default, never a silent "forever".
 * @param {unknown} value a number or the string stored in meta
 * @returns {number}
 */
export function normalizeRetentionDays(value) {
  const n = typeof value === "number" ? value : typeof value === "string" && value.trim() !== "" ? Number(value) : Number.NaN;
  if (!Number.isInteger(n) || n < 0 || n > HISTORY_RETENTION_MAX_DAYS) return HISTORY_RETENTION_DEFAULT_DAYS;
  return n;
}

// ---------------------------------------------------------------------------
// Platform
// ---------------------------------------------------------------------------

/**
 * @typedef {object} PlatformInput
 * @property {string} userAgent
 * @property {number} [maxTouchPoints] navigator.maxTouchPoints — iPadOS reports a Macintosh UA
 */

/**
 * @param {PlatformInput} input
 * @returns {boolean}
 */
export function isIosDevice(input) {
  const ua = input.userAgent;
  if (/iPhone|iPad|iPod/.test(ua)) return true;
  return /Macintosh/.test(ua) && (input.maxTouchPoints ?? 0) > 1;
}

/**
 * @param {PlatformInput} input
 * @returns {boolean}
 */
export function isAndroidDevice(input) {
  return /Android/.test(input.userAgent);
}

/**
 * Maps the browser onto the backend's closed platform list. iOS always means the home-screen
 * app: the pairing page refuses to run in a Safari tab there.
 * @param {PlatformInput} input
 * @returns {Platform}
 */
export function detectPlatform(input) {
  const ua = input.userAgent;
  if (isIosDevice(input)) return "ios-homescreen";
  if (/Firefox\//.test(ua)) return "firefox";
  if (isAndroidDevice(input)) return "android-chrome";
  if (/Windows/.test(ua) && /Chrome\//.test(ua)) return "windows-chromium";
  if (/Macintosh/.test(ua) && /Safari\//.test(ua) && !/Chrome\//.test(ua)) return "macos-safari";
  return "other";
}

/**
 * @param {Platform} platform
 * @returns {string} the label offered when pairing (editable)
 */
export function defaultLabel(platform) {
  switch (platform) {
    case "ios-homescreen":
      return "iPhone";
    case "android-chrome":
      return "Android phone";
    case "windows-chromium":
      return "Windows PC";
    case "macos-safari":
      return "Mac";
    case "firefox":
      return "Firefox";
    default:
      return "Device";
  }
}

/**
 * @param {string} label
 * @returns {string} the label as the backend will accept it
 */
export function cleanLabel(label) {
  return cleanText(label, LABEL_MAX_CHARS);
}

// ---------------------------------------------------------------------------
// Key encoding
// ---------------------------------------------------------------------------

/**
 * base64url without padding, as the backend expects p256dh and auth.
 * @param {ArrayBuffer | null} buffer
 * @returns {string}
 */
export function bufferToBase64Url(buffer) {
  if (!buffer) return "";
  const bytes = new Uint8Array(buffer);
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/**
 * The VAPID public key as `applicationServerKey` wants it.
 * @param {string} base64Url
 * @returns {Uint8Array<ArrayBuffer>}
 */
export function base64UrlToUint8Array(base64Url) {
  const padded = base64Url + "=".repeat((4 - (base64Url.length % 4)) % 4);
  const binary = atob(padded.replace(/-/g, "+").replace(/_/g, "/"));
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

// ---------------------------------------------------------------------------
// API requests (the pages fetch through lib/push/pushApi.ts, the worker directly)
// ---------------------------------------------------------------------------

export const DEVICE_TOKEN_HEADER = "X-Push-Device-Token";

/**
 * Builds a request against the push API. The device token travels only in its header (K11).
 * @param {string} apiBaseUrl
 * @param {string} path path under the API origin, e.g. "/api/push/device"
 * @param {{ method?: string, token?: string | null, body?: unknown }} [options]
 * @returns {PushApiRequest}
 */
/**
 * Which key the worker subscribes with when the browser rotates the subscription
 * (pushsubscriptionchange; the pages' reconcile has the same rule in decideReconcileAction):
 * the server's active key when it is known and differs from the stored kid, or when no stored
 * key exists; otherwise the stored key; null when there is nothing to subscribe with.
 * @param {Record<string, string>} meta the stored meta (kid, vapidPublicKey)
 * @param {{ activeKid?: unknown, activeVapidPublicKey?: unknown } | null} status GET push/device, or null when it failed
 * @returns {{ kid: string, publicKey: string, rekey: boolean } | null}
 */
export function planSubscriptionKeys(meta, status) {
  const storedKid = typeof meta[META_KEYS.kid] === "string" ? meta[META_KEYS.kid] : "";
  const storedKey = typeof meta[META_KEYS.vapidPublicKey] === "string" ? meta[META_KEYS.vapidPublicKey] : "";
  const activeKid = status && typeof status.activeKid === "string" ? status.activeKid : "";
  const activeKey = status && typeof status.activeVapidPublicKey === "string" ? status.activeVapidPublicKey : "";
  if (activeKid !== "" && activeKey !== "" && (activeKid !== storedKid || storedKey === "")) {
    return { kid: activeKid, publicKey: activeKey, rekey: activeKid !== storedKid };
  }
  if (storedKid !== "" && storedKey !== "") return { kid: storedKid, publicKey: storedKey, rekey: false };
  return null;
}

export function pushApiRequest(apiBaseUrl, path, options = {}) {
  /** @type {Record<string, string>} */
  const headers = { Accept: "application/json" };
  if (options.token) headers[DEVICE_TOKEN_HEADER] = options.token;
  /** @type {RequestInit} */
  const init = { method: options.method ?? "GET", headers };
  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(options.body);
  }
  return { url: `${apiBaseUrl.replace(/\/$/, "")}${path}`, init };
}

// ---------------------------------------------------------------------------
// IndexedDB (called from the pages and the worker, never at import time)
// ---------------------------------------------------------------------------

/**
 * @returns {Promise<IDBDatabase>}
 */
export function openPushDb() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains(HISTORY_STORE)) {
        const history = db.createObjectStore(HISTORY_STORE, { keyPath: "id" });
        history.createIndex(HISTORY_TS_INDEX, "ts");
      }
      if (!db.objectStoreNames.contains(META_STORE)) {
        db.createObjectStore(META_STORE);
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error("IndexedDB open failed"));
    request.onblocked = () => reject(new Error("IndexedDB open blocked"));
  });
}

/**
 * @template T
 * @param {IDBRequest<T>} request
 * @returns {Promise<T>}
 */
function requestToPromise(request) {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error("IndexedDB request failed"));
  });
}

/**
 * @param {IDBTransaction} tx
 * @returns {Promise<void>}
 */
function transactionDone(tx) {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error ?? new Error("IndexedDB transaction failed"));
    tx.onabort = () => reject(tx.error ?? new Error("IndexedDB transaction aborted"));
  });
}

/**
 * @template T
 * @param {"readonly" | "readwrite"} mode
 * @param {(stores: { history: IDBObjectStore, meta: IDBObjectStore }) => Promise<T> | T} work
 * @returns {Promise<T>}
 */
async function withStores(mode, work) {
  const db = await openPushDb();
  try {
    const tx = db.transaction([HISTORY_STORE, META_STORE], mode);
    const result = await work({
      history: tx.objectStore(HISTORY_STORE),
      meta: tx.objectStore(META_STORE),
    });
    await transactionDone(tx);
    return result;
  } finally {
    db.close();
  }
}

/**
 * @returns {Promise<Record<string, string>>} every meta key/value
 */
export function readMeta() {
  return withStores("readonly", async ({ meta }) => {
    const keys = await requestToPromise(meta.getAllKeys());
    const values = await requestToPromise(meta.getAll());
    /** @type {Record<string, string>} */
    const result = {};
    keys.forEach((key, i) => {
      const value = values[i];
      if (typeof key === "string" && typeof value === "string") result[key] = value;
    });
    return result;
  });
}

/**
 * Merges the given keys into the meta store.
 * @param {Record<string, string>} values
 * @returns {Promise<void>}
 */
export function writeMeta(values) {
  return withStores("readwrite", async ({ meta }) => {
    for (const [key, value] of Object.entries(values)) {
      await requestToPromise(meta.put(value, key));
    }
  });
}

/**
 * @returns {Promise<HistoryEntry[]>} all entries, newest first, after applying the retention rule
 */
export function readHistory() {
  return withStores("readonly", async ({ history, meta }) => {
    const all = /** @type {HistoryEntry[]} */ (await requestToPromise(history.getAll()));
    return pruneHistory(all, Date.now(), await retentionDaysFrom(meta));
  });
}

/**
 * Upserts one entry (dedupe by id) and prunes what the retention rule no longer keeps.
 * @param {HistoryEntry} entry
 * @returns {Promise<boolean>} false when an entry with this id already existed
 */
export function writeHistoryEntry(entry) {
  return withStores("readwrite", async ({ history, meta }) => {
    const existing = await requestToPromise(history.get(entry.id));
    if (existing) return false;
    await requestToPromise(history.put(entry));
    await pruneStore(history, meta);
    return true;
  });
}

/**
 * Removes one entry from the local history (the user's delete; nothing leaves the device).
 * @param {string} id
 * @returns {Promise<void>}
 */
export function deleteHistoryEntry(id) {
  return withStores("readwrite", async ({ history }) => {
    await requestToPromise(history.delete(id));
  });
}

/**
 * Applies the retention rule to the stored rows (called on open as well as on write).
 * @returns {Promise<void>}
 */
export function pruneStoredHistory() {
  return withStores("readwrite", ({ history, meta }) => pruneStore(history, meta));
}

/**
 * @returns {Promise<number>} the stored retention in days (default when unset or invalid)
 */
export function readRetentionDays() {
  return withStores("readonly", ({ meta }) => retentionDaysFrom(meta));
}

/**
 * Stores the user's retention choice and applies it at once.
 * @param {number} days 0–365, 0 = keep until the entry cap
 * @returns {Promise<number>} the value as stored
 */
export function writeRetentionDays(days) {
  const value = normalizeRetentionDays(days);
  return withStores("readwrite", async ({ history, meta }) => {
    await requestToPromise(meta.put(String(value), META_KEYS.historyRetentionDays));
    await pruneStore(history, meta);
    return value;
  });
}

/**
 * @param {IDBObjectStore} meta
 * @returns {Promise<number>}
 */
async function retentionDaysFrom(meta) {
  return normalizeRetentionDays(await requestToPromise(meta.get(META_KEYS.historyRetentionDays)));
}

/**
 * @param {IDBObjectStore} history
 * @param {IDBObjectStore} meta
 * @returns {Promise<void>}
 */
async function pruneStore(history, meta) {
  const all = /** @type {HistoryEntry[]} */ (await requestToPromise(history.getAll()));
  const keep = new Set(pruneHistory(all, Date.now(), await retentionDaysFrom(meta)).map((entry) => entry.id));
  for (const entry of all) {
    if (!keep.has(entry.id)) await requestToPromise(history.delete(entry.id));
  }
}

/**
 * Forgets everything: history and meta (unpair, wipe command, "device gone").
 * @returns {Promise<void>}
 */
export function clearPushDb() {
  return withStores("readwrite", async ({ history, meta }) => {
    await requestToPromise(history.clear());
    await requestToPromise(meta.clear());
  });
}
