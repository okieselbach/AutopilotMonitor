/**
 * Shared host routing constants for the public/portal split.
 *
 * - Public surface (marketing, docs, terms) lives on www.
 * - Authenticated app surface lives on portal.
 * - Apex redirects to www at the registrar (Strato HTTP forwarder).
 *
 * Since the static export there is no middleware: HostRoutingGuard enforces
 * these boundaries client-side, and the helpers below let components nudge
 * users to the right host before MSAL fires, so we avoid bouncing through
 * two MSAL flows during sign-in.
 *
 * Hostnames are derived from the URL registry in utils/config.ts — this file
 * adds the routing semantics, not a second copy of the hosts.
 */
import { PORTAL_URL, SITE_URL } from "@/utils/config";

/**
 * Paths that belong on the PUBLIC (www) host. Everything else is portal
 * surface. Formerly enforced server-side by middleware.ts (deleted with the
 * static export); now consumed by HostRoutingGuard.
 */
export const PUBLIC_PATH_PREFIXES = [
  "/about",
  "/changelog",
  "/docs",
  "/get-started",
  "/privacy",
  "/terms",
  "/roadmap",
  // NOTE: /sla is deliberately NOT here. It is a fully authenticated portal
  // page (ProtectedRoute + token fetches) that only LOOKED public because SLA
  // notifications historically linked www…/sla. As a public path it dead-ends:
  // ProtectedRoute stands down on the public host, so the page waits forever
  // for a sign-in (prod incident 2026-07-29). Off the list, old www…/sla links
  // bounce to portal/sla where auth works; new notifications link portal
  // directly (SlaBreachEvaluationService.DashboardUrl).
  "/robots.txt",
  "/sitemap.xml",
  "/IndexNow.txt",
  "/opengraph-image",
  "/twitter-image",
  "/apple-icon",
  "/icon",
];

export function isPublicPath(pathname: string): boolean {
  if (pathname === "/") return true;
  for (const p of PUBLIC_PATH_PREFIXES) {
    if (pathname === p) return true;
    if (pathname.startsWith(p + "/")) return true;
    // Match generated assets like /opengraph-image.png, /icon-192.png.
    if (pathname.startsWith(p + ".") || pathname.startsWith(p + "-")) return true;
  }
  return false;
}

export const PUBLIC_HOST = new URL(SITE_URL).hostname;
export const PORTAL_HOST = new URL(PORTAL_URL).hostname;
export const APEX_HOST = PUBLIC_HOST.replace(/^www\./, "");

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

export type HostBounce = "to-portal" | "to-www" | null;

/**
 * Pure decision core of HostRoutingGuard — where a page load belongs given
 * host, path, and auth state. Kept side-effect free so the safety rules are
 * unit-testable (see hostRouting.guard.test.ts).
 *
 * - hasAuthResponse: the URL still carries an MSAL #code/#state/#error
 *   fragment. Never move it — the sign-in would be destroyed.
 * - Portal → www requires SETTLED, UNAUTHENTICATED state (2026-07-30 prod
 *   incident): the guard's effect can run before MSAL restores a session or
 *   while it is still redeeming an auth code whose hash it already stripped.
 *   Bouncing then throws away a valid sign-in; an authenticated user must
 *   never be pushed off the portal origin.
 * - www → portal is auth-independent: www cannot serve portal paths for
 *   anyone, so it may fire while auth is still loading.
 */
export function decideHostBounce(input: {
  onPublicHost: boolean;
  onPortalHost: boolean;
  pathname: string;
  hasAuthResponse: boolean;
  isAuthLoading: boolean;
  isAuthenticated: boolean;
}): HostBounce {
  if (input.hasAuthResponse) return null;
  if (input.onPublicHost && !isPublicPath(input.pathname)) return "to-portal";
  if (input.onPortalHost && isPublicPath(input.pathname)) {
    if (input.isAuthLoading || input.isAuthenticated) return null;
    return "to-www";
  }
  return null;
}
