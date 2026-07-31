/**
 * App-registration selection for the dual app-reg parallel window (legacy gktatooine app ∥
 * new C4A8 app). Model: "default legacy, funnel primary, learn in the background" —
 *
 *  - A browser that has signed in before carries `am_auth_app` in localStorage and always
 *    boots MSAL with that app. auth/me returns the tenant's `homedApp` after every login and
 *    AuthContext re-writes the flag, so an operator flipping a tenant to the new app makes
 *    every user's NEXT login use it automatically (admin consent exists by then → seamless).
 *  - No flag → NEXT_PUBLIC_ENTRA_DEFAULT_APP decides (start: "legacy" = today's behaviour for
 *    every existing customer; flipped to "primary" once the re-consent campaign is done).
 *  - The signup funnel (get-started CTA) hands over to portal with ?authapp=primary so brand-new
 *    tenants consent the NEW app as part of the expected signup flow.
 *  - ?authapp=legacy|primary doubles as a silent support lever (persists the flag).
 *
 * Everything here is deliberately msal-free (no import cycles) and SSR-safe.
 */

import { trackEvent } from "./appInsights";

export type AuthApp = "primary" | "legacy";

const SELECTED_KEY = "am_auth_app";           // localStorage: app of the last successful login
const ATTEMPT_KEY = "am_login_attempt";       // sessionStorage: app that started the in-flight redirect
const FALLBACK_KEY = "am_login_fallback";     // sessionStorage: the one-shot 90094 auto-fallback ran
const DECLINED_KEY = "am_login_declined";     // sessionStorage: user actively declined consent (65004)

export function legacyClientId(): string | undefined {
  return process.env.NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID || undefined;
}

/** The parallel window is active only when the legacy client id is configured. */
export function legacyConfigured(): boolean {
  return !!legacyClientId();
}

export function otherApp(app: AuthApp): AuthApp {
  return app === "legacy" ? "primary" : "legacy";
}

export function primaryClientId(): string | undefined {
  return process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID || undefined;
}

/**
 * Classifies a tenant's `homedAppClientId` against the two configured app registrations.
 * `null`/empty is the backend's legacy invariant. Only meaningful while the parallel window
 * is active (`legacyConfigured()`); "unknown" means a GUID matching neither app — the backend
 * routes such tenants to primary with a warning.
 */
export function classifyClientId(clientId: string | null | undefined): AuthApp | "unknown" {
  if (!clientId || !clientId.trim()) return "legacy";
  const normalized = clientId.trim().toLowerCase();
  if (normalized === primaryClientId()?.toLowerCase()) return "primary";
  if (normalized === legacyClientId()?.toLowerCase()) return "legacy";
  return "unknown";
}

function defaultApp(): AuthApp {
  return process.env.NEXT_PUBLIC_ENTRA_DEFAULT_APP === "legacy" ? "legacy" : "primary";
}

function readUrlOverride(): AuthApp | null {
  try {
    const param = new URLSearchParams(window.location.search).get("authapp");
    return param === "legacy" || param === "primary" ? param : null;
  } catch {
    return null;
  }
}

/**
 * The app a fresh MSAL boot should use when NO redirect is in flight:
 * URL override (persisted) → stored flag → env default. Always "primary" during SSR/build
 * and whenever the legacy app isn't configured (pre-migration deploys stay byte-identical).
 */
export function getSelectedAuthApp(): AuthApp {
  if (typeof window === "undefined" || !legacyConfigured()) return "primary";
  const override = readUrlOverride();
  if (override) {
    setSelectedAuthApp(override);
    return override;
  }
  try {
    const stored = window.localStorage.getItem(SELECTED_KEY);
    if (stored === "legacy" || stored === "primary") return stored;
  } catch {
    // Storage unavailable (privacy mode) — fall through to the default.
  }
  return defaultApp();
}

export function setSelectedAuthApp(app: AuthApp): void {
  try {
    window.localStorage.setItem(SELECTED_KEY, app);
  } catch {
    // Best-effort: without storage the browser just re-derives the default next time.
  }
}

/**
 * The app THIS page load must construct its PublicClientApplication for. A redirect return
 * must be processed by the app that started it (`am_login_attempt`), regardless of what the
 * flag says — otherwise handleRedirectPromise can't redeem the auth code.
 */
export function getBootAuthApp(): AuthApp {
  if (typeof window === "undefined" || !legacyConfigured()) return "primary";
  return getLoginAttemptApp() ?? getSelectedAuthApp();
}

export function getLoginAttemptApp(): AuthApp | null {
  try {
    const value = window.sessionStorage.getItem(ATTEMPT_KEY);
    return value === "legacy" || value === "primary" ? value : null;
  } catch {
    return null;
  }
}

export function setLoginAttemptApp(app: AuthApp): void {
  try {
    window.sessionStorage.setItem(ATTEMPT_KEY, app);
  } catch {
    // Without the marker a cross-app fallback redirect can't be redeemed, but the regular
    // single-app flow still works (boot app equals the flag/default).
  }
}

export function clearLoginAttemptApp(): void {
  try {
    window.sessionStorage.removeItem(ATTEMPT_KEY);
  } catch {
    /* ignore */
  }
}

/**
 * One-shot guard for the AADSTS90094 auto-fallback ("this app needs admin approval in your
 * tenant" → silently retry with the other app). Returns true exactly once per browser tab
 * until a successful login clears it — prevents a ping-pong loop when NEITHER app is consented.
 */
export function tryBeginLoginFallback(): boolean {
  try {
    if (window.sessionStorage.getItem(FALLBACK_KEY)) return false;
    window.sessionStorage.setItem(FALLBACK_KEY, "1");
    return true;
  } catch {
    return false;
  }
}

export function clearLoginFallback(): void {
  try {
    window.sessionStorage.removeItem(FALLBACK_KEY);
  } catch {
    /* ignore */
  }
}

/** Whether the one-shot cross-app fallback ran in this tab (marker not yet cleared by a successful login). */
export function loginFallbackActive(): boolean {
  try {
    return !!window.sessionStorage.getItem(FALLBACK_KEY);
  } catch {
    return false;
  }
}

/** User actively declined the consent prompt (AADSTS65004) — never auto-fallback on that. */
export function markLoginDeclined(): void {
  try {
    window.sessionStorage.setItem(DECLINED_KEY, "1");
  } catch {
    /* ignore */
  }
}

/** Reads AND clears the declined marker (one-shot, consumed by ProtectedRoute). */
export function consumeLoginDeclined(): boolean {
  try {
    if (!window.sessionStorage.getItem(DECLINED_KEY)) return false;
    window.sessionStorage.removeItem(DECLINED_KEY);
    return true;
  } catch {
    return false;
  }
}

/** Classification of Entra redirect errors driving the safety net. */
export function classifyEntraAuthError(errorText: string): "admin-approval-required" | "declined" | "other" {
  if (/AADSTS90094|AADSTS65001/i.test(errorText)) return "admin-approval-required";
  if (/AADSTS65004/i.test(errorText)) return "declined";
  return "other";
}

/**
 * Switch this browser to the given app and reboot the bundle so the module-level MSAL
 * instance is reconstructed for it. Purges MSAL state to avoid interaction_in_progress
 * leftovers from an interrupted flow on the other app.
 */
export function switchAuthApp(app: AuthApp): void {
  // Strategic trace point of the dual app-reg migration: an explicit app switch (support
  // lever, post-flip "sign in with the new app" button). Best-effort — the reload below may
  // outrun the batch, but AI's pagehide beacon usually carries it out.
  trackEvent("auth_app_switched", { to: app });
  setSelectedAuthApp(app);
  clearLoginAttemptApp();
  clearLoginFallback();
  try {
    const doomed: string[] = [];
    for (let i = 0; i < window.sessionStorage.length; i++) {
      const key = window.sessionStorage.key(i);
      if (key && (key.startsWith("msal.") || key.includes(".login.request"))) doomed.push(key);
    }
    doomed.forEach((key) => window.sessionStorage.removeItem(key));
  } catch {
    /* ignore */
  }
  window.location.reload();
}
