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

/**
 * Invisible client component that handles auth redirect logic.
 * Renders nothing visible — just redirects authenticated users.
 * When auth is still loading, shows a loading overlay on top of the static page.
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
      const returnUrl = rehomeNow ? peekPostLoginReturnUrl() : consumePostLoginReturnUrl();
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

  // While auth is loading and we might need to redirect, show overlay.
  // This prevents a flash of the landing page for authenticated users.
  if (isLoading) {
    return (
      <div className="fixed inset-0 z-50 bg-[var(--lp-bg)] flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-green-600 mx-auto"></div>
          <p className="mt-4 text-gray-600">Loading...</p>
        </div>
      </div>
    );
  }

  // Once loaded, render nothing — the static page shows through.
  return null;
}
