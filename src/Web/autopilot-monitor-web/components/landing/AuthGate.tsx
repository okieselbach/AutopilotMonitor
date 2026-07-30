"use client";

import { useAuth } from "../../contexts/AuthContext";
import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { consumePostLoginReturnUrl } from "../../lib/postLoginReturn";
import { PORTAL_HOST, shouldCrossOriginToPortal } from "../../lib/hostRouting";

/**
 * Invisible client component that handles auth redirect logic.
 * Renders nothing visible — just redirects authenticated users.
 * When auth is still loading, shows a loading overlay on top of the static page.
 */
export function AuthGate() {
  const { isAuthenticated, isLoading, user, isPreviewBlocked } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (isAuthenticated && !isLoading && user) {
      // Always consume (read + clear) so a stale deep link can't misroute a later
      // sign-in; only honor it when the user isn't preview-gated.
      const returnUrl = consumePostLoginReturnUrl();
      let target: string;
      if (isPreviewBlocked) {
        target = "/preview";
      } else if (returnUrl) {
        // Restore the deep link the user originally opened before re-auth.
        target = returnUrl;
      } else if (user.isDelegated && !user.isTenantAdmin && !user.isGlobalAdmin && !user.isGlobalReader && user.role !== 'Operator') {
        // A delegated ("MSP") admin with no own-tenant/platform role manages a fleet → land on /fleet.
        target = "/fleet";
      } else if (user.isTenantAdmin || user.isGlobalAdmin || user.isGlobalReader || user.role === 'Operator') {
        target = "/dashboard";
      } else {
        target = "/progress";
      }
      // On the public host, hand over to the portal origin in ONE full-page
      // navigation instead of router.push + HostRoutingGuard bounce. Auth state
      // is per-origin — the portal side runs its own (silent) MSAL sign-in.
      if (shouldCrossOriginToPortal()) {
        window.location.href = `https://${PORTAL_HOST}${target}`;
      } else {
        router.replace(target);
      }
    }
  }, [isAuthenticated, isLoading, user, isPreviewBlocked, router]);

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
