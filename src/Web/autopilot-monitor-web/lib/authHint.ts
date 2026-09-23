import { PORTAL_URL } from "@/utils/config";

/**
 * "Auth hint": does this page load belong to a signed-in (or signing-in) browser?
 *
 * The landing page is a static export shared by www and portal. It used to render a
 * full-screen "Loading…" overlay in the prerendered HTML for EVERY visitor until MSAL had
 * settled (measured 2026-09-23: 0.9 s → 2.8 s on a desktop with a cold cache), although only
 * a browser with a session ever leaves the page. The overlay is now shown only when one of
 * these hints exists, and it is applied before first paint by an inline <head> script (the
 * static HTML cannot know the host or the storage state):
 *  - the URL hash carries an MSAL auth response (`code=`, `state=`, `error=`) — the sign-in
 *    completes here and the page navigates on;
 *  - the page runs on the portal host — its root never stays visible (dashboard or bounce
 *    to www once auth settles, see HostRoutingGuard);
 *  - MSAL has an account in sessionStorage (`msal.<n>.account.keys` is a non-empty list) —
 *    AuthGate will redirect once the silent sign-in settles.
 * Only the site root carries the hint: AuthGate (which drops the class again) is rendered by
 * app/page.tsx alone, and a signed-in browser opening /about/ or /plans/ must see the page,
 * not a spinner nobody removes. globals.css additionally lifts the overlay after a timeout.
 *
 * `evaluateAuthHint` and `AUTH_HINT_INLINE_SCRIPT` implement the same rule; the parity test
 * in lib/__tests__/authHint.test.ts pins them to each other. Errors resolve to "no hint",
 * i.e. the landing renders — the anonymous case must never be blocked by a storage failure.
 */

export const AUTH_PENDING_CLASS = "auth-pending";

export const PORTAL_HOSTNAME = new URL(PORTAL_URL).hostname;

const AUTH_RESPONSE_HASH = /[#&](code|state|error)=/;
const MSAL_ACCOUNT_KEYS = /^msal\.\d+\.account\.keys$/;

export interface AuthHintInput {
  /** location.pathname — the hint applies to the site root only. */
  pathname: string;
  hash: string;
  hostname: string;
  /** All sessionStorage keys of the origin. */
  storageKeys: string[];
  getItem: (key: string) => string | null;
}

const ROOT_PATHNAMES = new Set(["/", "/index.html"]);

export function evaluateAuthHint(input: AuthHintInput): boolean {
  if (!ROOT_PATHNAMES.has(input.pathname)) return false;
  if (AUTH_RESPONSE_HASH.test(input.hash)) return true;
  if (input.hostname.toLowerCase() === PORTAL_HOSTNAME) return true;
  for (const key of input.storageKeys) {
    if (!MSAL_ACCOUNT_KEYS.test(key)) continue;
    const value = input.getItem(key);
    return !!value && value !== "[]";
  }
  return false;
}

/** Browser-side evaluation (client components only). */
export function hasAuthHint(): boolean {
  if (typeof window === "undefined") return false;
  try {
    const storage = window.sessionStorage;
    const keys: string[] = [];
    for (let i = 0; i < storage.length; i++) {
      const key = storage.key(i);
      if (key) keys.push(key);
    }
    return evaluateAuthHint({
      pathname: window.location.pathname,
      hash: window.location.hash,
      hostname: window.location.hostname,
      storageKeys: keys,
      getItem: (key) => storage.getItem(key),
    });
  } catch {
    return false;
  }
}

/**
 * Same rule as plain ES5 for the inline <head> script: `AUTH_HINT_INLINE_SCRIPT_BODY` is the
 * function body (parameters: location, sessionStorage, document) so the parity test can run
 * it with fakes; `AUTH_HINT_INLINE_SCRIPT` is what the root layout inlines.
 */
export const AUTH_HINT_INLINE_SCRIPT_BODY =
  "try{" +
  "var p=location.pathname;if(p!=='/'&&p!=='/index.html'){return;}" +
  "var cls=" + JSON.stringify(AUTH_PENDING_CLASS) + ";" +
  "if(/[#&](code|state|error)=/.test(location.hash)||location.hostname.toLowerCase()===" +
  JSON.stringify(PORTAL_HOSTNAME) +
  "){document.documentElement.classList.add(cls);return;}" +
  "for(var i=0;i<sessionStorage.length;i++){var k=sessionStorage.key(i);" +
  "if(k&&/^msal\\.\\d+\\.account\\.keys$/.test(k)){var v=sessionStorage.getItem(k);" +
  "if(v&&v!=='[]'){document.documentElement.classList.add(cls);}return;}}" +
  "}catch(e){}";

export const AUTH_HINT_INLINE_SCRIPT =
  "(function(location,sessionStorage,document){" +
  AUTH_HINT_INLINE_SCRIPT_BODY +
  "})(window.location,window.sessionStorage,window.document);";
