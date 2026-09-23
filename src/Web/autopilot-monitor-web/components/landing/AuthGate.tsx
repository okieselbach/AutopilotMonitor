"use client";

import { useAuth } from "../../contexts/AuthContext";
import { useRouter } from "next/navigation";
import { useEffect } from "react";
import type { Route } from "next";
import { trustedRoute } from "../../lib/routes";
import { consumePostLoginReturnUrl, peekPostLoginReturnUrl } from "../../lib/postLoginReturn";
import { hasOwnTenantOrPlatformRole, hasTenantReadScope } from "../../lib/tenantScope";
import { portalHandoverUrl, shouldCrossOriginToPortal } from "../../lib/hostRouting";
import { consumePendingRehome, getSelectedAuthApp, legacyConfigured, switchAuthApp, tryBeginRehome } from "../../lib/authApp";
import { activeAuthApp } from "../../lib/msalConfig";
import { trackEvent } from "../../lib/appInsights";
import { AUTH_PENDING_CLASS, hasAuthHint } from "../../lib/authHint";

/**
 * Invisible client component that handles auth redirect logic.
 * Renders nothing visible — just redirects authenticated users.
 * While auth is still loading for a browser that carries an auth hint, the page sits under
 * the CSS `auth-pending` overlay (see below); anonymous visitors see the page immediately.
 */
export function AuthGate() {
  const { isAuthenticated, isLoading, user, isActivationPending } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (isAuthenticated && !isLoading && user) {
      // Dual app-reg window: the sign-in that just completed ran on the other app than the
      // tenant is homed on (fresh browser, wrong default). Same-origin: re-home NOW — switch
      // the browser to the homed app and land on the target, where ProtectedRoute completes
      // the sign-in silently via the Entra session and returns here with the deep link intact.
      // One hop per tab; the pending request is consumed either way.
      const rehome = consumePendingRehome();
      const crossOrigin = shouldCrossOriginToPortal();
      const rehomeNow = rehome !== null && !crossOrigin && tryBeginRehome();
      // Normally consume (read + clear) so a stale deep link can't misroute a later
      // sign-in; only honor it when the user's tenant is activated. On the re-home hop only
      // PEEK: the link has to survive the extra sign-in round trip for the AuthGate pass that
      // follows, and leaving it in place beats re-saving it.
      // While activation is pending the link is only PEEKED as well: an invited customer admin whose
      // tenant is still being activated must land on the invitation once activation completes, not on
      // /dashboard — the link survives the /activation detour for the AuthGate pass that follows.
      const returnUrl = rehomeNow || isActivationPending ? peekPostLoginReturnUrl() : consumePostLoginReturnUrl();
      let target: Route;
      if (isActivationPending) {
        target = "/activation";
      } else if (returnUrl) {
        // Restore the deep link the user originally opened before re-auth.
        target = trustedRoute(returnUrl);
      } else if (user.isDelegated && !hasOwnTenantOrPlatformRole(user)) {
        // A delegated ("MSP") admin with no own-tenant/platform role manages a fleet → land on /fleet.
        target = "/fleet";
      } else if (hasTenantReadScope(user)) {
        target = "/dashboard";
      } else {
        target = "/progress";
      }
      // On the public host, hand over to the portal origin in ONE full-page
      // navigation instead of router.push + HostRoutingGuard bounce. Auth state
      // is per-origin — the portal side runs its own (silent) MSAL sign-in, on
      // the app this browser just learned (passed along as ?authapp=).
      if (crossOrigin) {
        window.location.href = portalHandoverUrl(target, legacyConfigured() ? getSelectedAuthApp() : null);
      } else if (rehomeNow) {
        trackEvent("auth_app_rehomed", { from: activeAuthApp, to: rehome });
        switchAuthApp(rehome, target);
      } else {
        router.replace(target);
      }
    }
  }, [isAuthenticated, isLoading, user, isActivationPending, router]);

  // Overlay contract (lib/authHint.ts): the inline <head> script marks the document
  // `auth-pending` before first paint when an auth hint exists, and globals.css draws the
  // loading overlay from that class — the prerendered HTML carries no overlay, so an
  // anonymous visitor sees the page at first paint. This effect keeps the class only while
  // auth is still settling for a hinted browser and drops it as soon as the page either
  // stays (anonymous) or navigates away (unmount cleanup).
  useEffect(() => {
    document.documentElement.classList.toggle(AUTH_PENDING_CLASS, isLoading && hasAuthHint());
    return () => {
      document.documentElement.classList.remove(AUTH_PENDING_CLASS);
    };
  }, [isLoading]);

  // Renders nothing — the static page shows through (or sits under the CSS overlay).
  return null;
}
