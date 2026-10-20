/**
 * A failed sign-in return, recovered once per tab.
 *
 * Every redirect sign-in returns to "/" (lib/msalConfig.ts: redirectUri is the origin,
 * navigateToLoginRequestUrl false). When that return ends without an account — the auth code could
 * not be redeemed (state or PKCE mismatch, a redirect state that is gone, a token-endpoint or network
 * error) — the tab used to stay on the public landing with a dead `#code=` in the URL. Instead:
 *  - the first failure in a tab starts one fresh sign-in on the spot; the saved deep link
 *    (lib/postLoginReturn.ts) stays and is honored once that sign-in succeeds;
 *  - any further failure in the tab shows ProtectedRoute's "Sign-in required" card on the deep link,
 *    which waits for a click — never an automatic loop.
 * An Entra error return (`#error=`) keeps its own first-attempt handling (consent decline, cross-app
 * fallback) and the landing. A tab that holds an account is never "recovered": a stale `/#code=`
 * history entry or a reload during auth/me must take the normal path.
 *
 * Deliberately msal-free (pure decisions plus sessionStorage markers) so it is testable without a
 * browser. Marker keys must not start with `msal.`: the MSAL purges in lib/authApp.ts and
 * app/global-error.tsx delete those.
 */

export interface BootAuthResponse {
  /** The page load carried an authorization code. */
  code: boolean;
  /** The page load carried an Entra error response. */
  error: boolean;
}

/**
 * The auth response the page was loaded with. Read it at module load: MSAL clears the hash
 * asynchronously, and the app router may write its hydration-time URL (hash included) back later.
 */
export function readBootAuthResponse(hash: string): BootAuthResponse {
  return { code: /[#&]code=/.test(hash), error: /[#&]error=/.test(hash) };
}

export type RedirectOutcome = "proceed" | "retry" | "card" | "landing";

export interface RedirectOutcomeInput {
  boot: BootAuthResponse;
  /** An account is cached once the redirect handling settled. */
  hasAccount: boolean;
  /** The automatic retry already ran in this tab. */
  retried: boolean;
}

/** What a page load does once MSAL's redirect handling has settled. */
export function decideRedirectOutcome({ boot, hasAccount, retried }: RedirectOutcomeInput): RedirectOutcome {
  if (hasAccount) return "proceed";
  if (!boot.code && !boot.error) return "proceed"; // not a sign-in return
  if (retried) return "card";
  return boot.code && !boot.error ? "retry" : "landing";
}

const RETRY_KEY = "am_redirect_retry"; // the one automatic retry ran: "<via>:<reason>"
const FAILED_KEY = "am_login_failed"; // the next ProtectedRoute starts on its sign-in card: "<reason>"

export type RecoveryVia = "auto" | "card";

/** Starts the one automatic retry of this tab. False when it already ran or nothing can be recorded. */
export function tryBeginRedirectRetry(reason: string): boolean {
  try {
    if (window.sessionStorage.getItem(RETRY_KEY) !== null) return false;
    window.sessionStorage.setItem(RETRY_KEY, `auto:${reason}`);
    return true;
  } catch {
    return false;
  }
}

/** Whether the automatic retry already ran in this tab. */
export function redirectRetried(): boolean {
  try {
    return window.sessionStorage.getItem(RETRY_KEY) !== null;
  } catch {
    return false;
  }
}

/**
 * Hands the tab over to the sign-in card: the next ProtectedRoute starts in its failed state, and a
 * later successful sign-in reports that it came through the card.
 */
export function markLoginFailed(reason: string): void {
  try {
    window.sessionStorage.setItem(RETRY_KEY, `card:${reason}`);
    window.sessionStorage.setItem(FAILED_KEY, reason);
  } catch {
    // Without storage MSAL's own redirect state cannot survive either.
  }
}

/** Reads AND clears the sign-in card marker (one-shot, consumed by ProtectedRoute): its reason, or null. */
export function consumeLoginFailed(): string | null {
  try {
    const reason = window.sessionStorage.getItem(FAILED_KEY);
    if (reason !== null) window.sessionStorage.removeItem(FAILED_KEY);
    return reason;
  } catch {
    return null;
  }
}

/** Reads AND clears the recovery trail after a successful sign-in: how it got there, or null. */
export function consumeRedirectRecovery(): { via: RecoveryVia; reason: string } | null {
  try {
    const value = window.sessionStorage.getItem(RETRY_KEY);
    window.sessionStorage.removeItem(RETRY_KEY);
    window.sessionStorage.removeItem(FAILED_KEY);
    if (value === null) return null;
    const separator = value.indexOf(":");
    const via = value.slice(0, separator) === "card" ? "card" : "auto";
    return { via, reason: value.slice(separator + 1) };
  } catch {
    return null;
  }
}

/** The MSAL error code of a redirect failure — never its message, which can carry the user's UPN. */
export function authErrorCode(error: unknown): string | null {
  const code = (error as { errorCode?: unknown } | null)?.errorCode;
  return typeof code === "string" && /^[A-Za-z0-9_.-]{1,64}$/.test(code) ? code : null;
}

// msal-browser TemporaryCacheKeys: `msal.interaction.status` plus `msal.<clientId>.<key>`.
const MSAL_REDIRECT_STATE_KEY =
  /^msal\.(interaction\.status|[^.]+\.(request\.origin|urlHash|request\.params|code\.verifier|request\.native))$/;

/**
 * Removes this tab's in-flight MSAL redirect state, keeping accounts and tokens. A status left by the
 * other app registration or an interrupted flow otherwise makes the next loginRedirect throw
 * interaction_in_progress, which the login paths swallow — the page then stays blank.
 */
export function purgeMsalRedirectState(): void {
  try {
    const storage = window.sessionStorage;
    const doomed: string[] = [];
    for (let i = 0; i < storage.length; i++) {
      const key = storage.key(i);
      if (key && MSAL_REDIRECT_STATE_KEY.test(key)) doomed.push(key);
    }
    doomed.forEach((key) => storage.removeItem(key));
  } catch {
    /* ignore */
  }
}
