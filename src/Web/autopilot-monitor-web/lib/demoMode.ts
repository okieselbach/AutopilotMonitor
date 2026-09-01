/**
 * Demo ("presentation") mode: pure logic, extracted from the hook so the whole matrix is testable
 * without a DOM (house pattern — see navVisibility / decideHostBounce).
 *
 * Demo mode makes the portal LOOK like a plain tenant-admin session so the product can be shown on
 * stage without leaking operator internals (build/commit hashes, the Global-Admin toggle, the
 * Global-Admin badge, platform-only configuration). It is PRESENTATION ONLY — every real gate stays
 * on the server (TenantConfigValidation, GlobalAdminOnly → 403). Never treat it as a security
 * boundary and never describe it as one.
 *
 * It is armed via a URL parameter that consumes itself: `?demo=1` writes the flag to localStorage
 * and is stripped from the address bar immediately, so nothing remains visible in a screenshot and
 * no devtools are needed on stage. `?demo=0` clears it the same way.
 */

/** localStorage key holding the demo-mode flag ("true" / "false"). */
export const DEMO_MODE_STORAGE_KEY = "demoMode";

/** Query-string parameter that arms or clears demo mode. */
export const DEMO_MODE_PARAM = "demo";

/**
 * Reads the demo-mode intent from a query string. Returns true (arm), false (clear), or null when
 * the parameter is absent or unrecognised — null means "leave the stored value alone", so a typo
 * (`?demo=yes`) never silently drops the operator out of demo mode mid-presentation.
 *
 * @param search a location.search value, with or without the leading "?".
 */
export function readDemoParam(search: string | null | undefined): boolean | null {
  if (!search) return null;

  let params: URLSearchParams;
  try {
    params = new URLSearchParams(search.startsWith("?") ? search.slice(1) : search);
  } catch {
    return null;
  }

  const raw = params.get(DEMO_MODE_PARAM);
  if (raw === null) return null;

  const value = raw.trim().toLowerCase();
  // A bare "?demo" (no value) reads as an empty string — treat it as "arm", which is what someone
  // typing the shorthand means.
  if (value === "" || value === "1" || value === "true" || value === "on") return true;
  if (value === "0" || value === "false" || value === "off") return false;
  return null;
}

/**
 * Removes the demo parameter from a path+query+hash string, leaving everything else untouched.
 * Drops the "?" entirely when it was the only parameter, so the address bar shows a clean URL.
 *
 * @param url a path with optional query and hash, e.g. "/settings/tenant?demo=1&tab=x#top".
 */
export function stripDemoParam(url: string): string {
  const hashAt = url.indexOf("#");
  const hash = hashAt >= 0 ? url.slice(hashAt) : "";
  const withoutHash = hashAt >= 0 ? url.slice(0, hashAt) : url;

  const queryAt = withoutHash.indexOf("?");
  if (queryAt < 0) return url;

  const path = withoutHash.slice(0, queryAt);
  const params = new URLSearchParams(withoutHash.slice(queryAt + 1));
  if (!params.has(DEMO_MODE_PARAM)) return url;

  params.delete(DEMO_MODE_PARAM);
  const rest = params.toString();
  return `${path}${rest ? `?${rest}` : ""}${hash}`;
}

/**
 * The Global-Admin view flag as the UI should see it. Demo mode forces it off WITHOUT touching the
 * stored value, so the operator's usual setting comes back the moment demo mode is cleared.
 */
export function effectiveGlobalAdminMode(stored: boolean, demoMode: boolean): boolean {
  return stored && !demoMode;
}
