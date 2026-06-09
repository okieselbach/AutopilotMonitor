/**
 * Post-login return-URL helper.
 *
 * MSAL is configured with `navigateToLoginRequestUrl: false` (see lib/msalConfig.ts),
 * so after an interactive login redirect the browser lands on the bare origin (`/`)
 * and AuthGate routes the user to a role-based default (e.g. /dashboard) — losing the
 * deep link they originally tried to open (e.g. a /sessions/[id] link in a new tab).
 *
 * To preserve the intended destination we stash it just before triggering the login
 * redirect and restore it once auth settles back on the landing page. sessionStorage
 * survives the same-tab redirect round-trip and auto-clears when the tab closes.
 */

const RETURN_URL_KEY = "apm:postLoginReturnUrl";

/**
 * Only same-origin absolute in-app paths (e.g. "/sessions/abc") are eligible.
 * Rejects protocol-relative ("//evil.com"), backslash tricks ("/\\evil.com"),
 * absolute URLs, and the bare root (nothing meaningful to restore) to avoid
 * open-redirect abuse.
 */
function isSafeReturnPath(path: string | null | undefined): path is string {
  if (!path || !path.startsWith("/")) return false;
  if (path.startsWith("//") || path.startsWith("/\\")) return false;
  if (path === "/") return false;
  return true;
}

/** Persist the in-app location to return to after the login redirect completes. */
export function savePostLoginReturnUrl(path: string): void {
  try {
    if (!isSafeReturnPath(path)) return;
    window.sessionStorage.setItem(RETURN_URL_KEY, path);
  } catch {
    // sessionStorage unavailable (private mode / ITP) — fall back to default routing.
  }
}

/** Read and clear the saved return URL. Returns null if absent or unsafe. */
export function consumePostLoginReturnUrl(): string | null {
  try {
    const value = window.sessionStorage.getItem(RETURN_URL_KEY);
    if (value !== null) window.sessionStorage.removeItem(RETURN_URL_KEY);
    return isSafeReturnPath(value) ? value : null;
  } catch {
    return null;
  }
}
