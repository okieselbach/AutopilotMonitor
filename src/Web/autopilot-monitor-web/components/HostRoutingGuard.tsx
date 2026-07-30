"use client";

import { useEffect } from "react";
import { usePathname } from "next/navigation";
import { useAuth } from "@/contexts/AuthContext";
import {
  decideHostBounce,
  isOnPortalHost,
  isOnPublicHost,
  PORTAL_HOST,
  PUBLIC_HOST,
} from "@/lib/hostRouting";

/**
 * Client-side replacement for the deleted middleware.ts host bounce (the
 * static export has no server): portal-only paths reaching www are sent to
 * portal, public paths reaching portal are sent back to www. The apex host is
 * a registrar-level 301 to www and never reaches this code.
 *
 * Three hard safety rules learned from production incidents:
 *
 * 1. NEVER bounce while the URL carries an MSAL auth response. MSAL returns to
 *    the ORIGIN ROOT (redirectUri has no path, navigateToLoginRequestUrl is
 *    false), so portal.../#code=… looks like "public path on portal host" —
 *    bouncing it destroys the sign-in before handleRedirectPromise can run.
 *
 * 2. The portal → www direction must WAIT for auth to settle and only bounce
 *    when the user is actually unauthenticated (2026-07-30 incident: sign-ins
 *    landed back on the www landing page). Rule 1 alone is a race: MSAL's
 *    module-level init strips the #code hash BEFORE React hydration runs this
 *    effect, so the fresh auth response was bounced mid-token-exchange. It also
 *    locked signed-in users out of portal root — the bounce won against
 *    AuthGate, which needs settled auth state to route "/" to /dashboard.
 *    An authenticated user never gets bounced off portal; an anonymous visitor
 *    still lands on www once MSAL settles with no session. The www → portal
 *    direction stays auth-free: www cannot serve portal paths for anyone.
 *
 * 3. Loop breaker: auth state is per-origin. If the two origins disagree about
 *    where the user belongs (e.g. signed in on www but a failing sign-in on
 *    portal navigating back to a public path), the guard would ping-pong
 *    forever. After MAX_BOUNCES within WINDOW_MS the guard stands down for the
 *    session and lets the page render where it is.
 *
 * `location.replace` mirrors the middleware's 302 semantics — nothing is
 * cached, and the wrong-host entry does not linger in history. Dev (localhost)
 * and preview hosts match neither branch and pass through untouched.
 */

const BOUNCE_KEY = "hostRoutingGuard_bounces";
const MAX_BOUNCES = 3;
const WINDOW_MS = 15_000;

function hasAuthResponse(): boolean {
  const h = window.location.hash;
  return h.includes("code=") || h.includes("state=") || h.includes("error");
}

/** True while under the limit; records the bounce. Stands down otherwise. */
function registerBounce(): boolean {
  try {
    const now = Date.now();
    const raw = sessionStorage.getItem(BOUNCE_KEY);
    const recent = (raw ? (JSON.parse(raw) as number[]) : []).filter(
      (t) => now - t < WINDOW_MS,
    );
    if (recent.length >= MAX_BOUNCES) {
      console.warn(
        "[HostRoutingGuard] bounce limit reached — standing down to avoid a redirect loop",
      );
      return false;
    }
    recent.push(now);
    sessionStorage.setItem(BOUNCE_KEY, JSON.stringify(recent));
    return true;
  } catch {
    // Storage unavailable — still bounce (single hop is always safe).
    return true;
  }
}

export function HostRoutingGuard() {
  const pathname = usePathname();
  const { isAuthenticated, isLoading } = useAuth();

  useEffect(() => {
    if (!pathname) return;

    const bounce = decideHostBounce({
      onPublicHost: isOnPublicHost(),
      onPortalHost: isOnPortalHost(),
      pathname,
      hasAuthResponse: hasAuthResponse(),
      isAuthLoading: isLoading,
      isAuthenticated,
    });
    if (!bounce) return;

    const { search, hash } = window.location;
    const suffix = `${pathname}${search}${hash}`;
    const targetHost = bounce === "to-portal" ? PORTAL_HOST : PUBLIC_HOST;
    if (registerBounce()) window.location.replace(`https://${targetHost}${suffix}`);
  }, [pathname, isAuthenticated, isLoading]);

  return null;
}
