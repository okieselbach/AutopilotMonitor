/**
 * Get-started signup marker. The get-started CTA stays inert until its Terms + DPA tick is set,
 * so a sign-in started there tells the first auth/me (`?signupConsent=1`) that a brand-new
 * tenant already accepted them — the onboarding records it, and the portal does not ask again.
 * Any other first sign-in leaves the acceptance pending and the portal asks once.
 *
 * The CTA hands the marker to portal as `?signupConsent=1` (cross-origin) or sets it directly
 * (same origin). It lives in sessionStorage — it survives the MSAL redirect in the same tab and
 * dies with it — and the parameter is removed from the address bar right away, so a bookmark
 * or a shared link never carries it. Only the tenant's very first sign-in reads it server-side.
 */

const KEY = "am_signup_consent";
export const SIGNUP_CONSENT_PARAM = "signupConsent";
/** Long enough for a sign-in with consent prompts and MFA, short enough that a stale tab cannot reuse it. */
const TTL_MS = 30 * 60 * 1000;

export function markSignupConsent(now: number = Date.now()): void {
  try {
    window.sessionStorage.setItem(KEY, String(now));
  } catch {
    // Storage unavailable: the portal then asks once in its dialog instead.
  }
}

export function hasSignupConsent(now: number = Date.now()): boolean {
  try {
    const stamp = Number(window.sessionStorage.getItem(KEY));
    return Number.isFinite(stamp) && stamp > 0 && now - stamp >= 0 && now - stamp < TTL_MS;
  } catch {
    return false;
  }
}

/** Reads the marker from the address bar into sessionStorage. Call before the first auth/me can fire. */
export function captureSignupConsentFromUrl(): void {
  if (typeof window === "undefined") return;
  try {
    if (new URLSearchParams(window.location.search).get(SIGNUP_CONSENT_PARAM) === "1") markSignupConsent();
  } catch {
    // Unparseable location: nothing to capture.
  }
}

/** The address bar without the marker, or null when it carries none. */
export function stripSignupConsentParam(pathWithQuery: string): string | null {
  const url = new URL(pathWithQuery, "https://portal.invalid");
  if (!url.searchParams.has(SIGNUP_CONSENT_PARAM)) return null;
  url.searchParams.delete(SIGNUP_CONSENT_PARAM);
  return url.pathname + url.search + url.hash;
}
