/**
 * Shared host routing constants for the public/portal split.
 *
 * - Public surface (marketing, docs, terms, /go bootstrap) lives on www.
 * - Authenticated app surface lives on portal.
 * - Apex redirects to www at the registrar (Strato HTTP forwarder).
 *
 * Middleware enforces these boundaries server-side; helpers here let
 * client components nudge users to the right host before MSAL fires,
 * so we avoid bouncing through two MSAL flows during sign-in.
 */

export const PUBLIC_HOST = "www.autopilotmonitor.com";
export const PORTAL_HOST = "portal.autopilotmonitor.com";
export const APEX_HOST = "autopilotmonitor.com";

export const DEFAULT_PORTAL_LANDING = "/dashboard";

export function getCurrentHost(): string | null {
  if (typeof window === "undefined") return null;
  return window.location.host.toLowerCase();
}

export function isOnPortalHost(): boolean {
  return getCurrentHost() === PORTAL_HOST;
}

export function isOnPublicHost(): boolean {
  const host = getCurrentHost();
  return host === PUBLIC_HOST || host === APEX_HOST;
}

/**
 * Returns true when the current browser is on a known production host
 * with a public/portal split. False in dev (localhost), preview deploys,
 * or anything we don't recognize — those should keep single-origin MSAL.
 */
export function shouldCrossOriginToPortal(): boolean {
  const host = getCurrentHost();
  if (host === null) return false;
  return host === PUBLIC_HOST || host === APEX_HOST;
}

export function getPortalLoginUrl(path: string = DEFAULT_PORTAL_LANDING): string {
  const normalized = path.startsWith("/") ? path : `/${path}`;
  return `https://${PORTAL_HOST}${normalized}`;
}
