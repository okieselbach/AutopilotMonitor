/**
 * After this browser wrote a tenant configuration, its requests ask the backend for the stored row
 * for a few minutes (`X-Tenant-Config: stored`).
 *
 * The backend caches each tenant's configuration per instance, and a write clears only the cache of
 * the instance that served it. The next read is often answered by another instance: without the
 * header it shows the configuration from before the write (a settings page reloaded after the
 * admin-consent redirect, "Send test" right after saving a channel, auth/me after the terms tick).
 *
 * The mark lives in localStorage, because it has to survive that redirect and reach other tabs.
 */

const STORAGE_KEY = "am_config_written";

/**
 * Longer than the backend's configuration cache (TenantConfigurationService.CacheDuration, five
 * minutes): once it has passed, no instance holds a copy from before the write. Raising the
 * backend's duration without this one brings the stale reads back.
 */
export const STORED_CONFIG_HINT_MS = 6 * 60_000;

const HEADERS = { "X-Tenant-Config": "stored" } as const;
const NO_HEADERS = {} as const;

/** The last write this tab made, for a browser without usable storage. */
let lastWriteAt = 0;

function readMark(): number {
  try {
    const stored = Number(window.localStorage.getItem(STORAGE_KEY));
    return Number.isFinite(stored) ? Math.max(stored, lastWriteAt) : lastWriteAt;
  } catch {
    return lastWriteAt;
  }
}

/**
 * A request that writes a tenant configuration row: every write under `/api/config/`, and the terms
 * confirmation. The backend pins this list from its side (TenantConfigWriteRouteGuardTests): a route
 * that writes the row anywhere else has to be added here.
 */
export function writesTenantConfig(method: string, url: string): boolean {
  const verb = method.toUpperCase();
  if (verb === "GET" || verb === "HEAD") return false;
  let path = url;
  try {
    path = new URL(url, "http://localhost").pathname;
  } catch {
    // Keep the raw value: a relative path still matches below.
  }
  return path.startsWith("/api/config/") || path === "/api/auth/dpa-acceptance";
}

/** Call once a configuration write has succeeded. */
export function noteTenantConfigWritten(now: number = Date.now()): void {
  lastWriteAt = now;
  try {
    window.localStorage.setItem(STORAGE_KEY, String(now));
  } catch {
    // Without storage the mark still covers this tab until it reloads.
  }
}

/** The header every API call carries while a configuration write of this browser is recent; none otherwise. */
export function storedConfigHeaders(now: number = Date.now()): Readonly<Record<string, string>> {
  if (typeof window === "undefined") return NO_HEADERS;
  const at = readMark();
  // A mark from the future (the device clock was set back) counts no longer than a fresh one.
  return at > 0 && Math.abs(now - at) < STORED_CONFIG_HINT_MS ? HEADERS : NO_HEADERS;
}
