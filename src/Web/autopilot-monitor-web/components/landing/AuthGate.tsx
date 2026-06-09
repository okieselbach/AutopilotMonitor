"use client";

import { useAuth } from "../../contexts/AuthContext";
import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { consumePostLoginReturnUrl } from "../../lib/postLoginReturn";

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
      if (isPreviewBlocked) {
        router.push("/preview");
      } else if (returnUrl) {
        // Restore the deep link the user originally opened before re-auth.
        router.replace(returnUrl);
      } else if (user.isTenantAdmin || user.isGlobalAdmin || user.role === 'Operator') {
        router.push("/dashboard");
      } else {
        router.push("/progress");
      }
    }
  }, [isAuthenticated, isLoading, user, isPreviewBlocked, router]);

  // While auth is loading and we might need to redirect, show overlay.
  // This prevents a flash of the landing page for authenticated users.
  if (isLoading) {
    return (
      <div className="fixed inset-0 z-50 bg-gradient-to-br from-blue-50 via-indigo-50 to-purple-50 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <p className="mt-4 text-gray-600">Loading...</p>
        </div>
      </div>
    );
  }

  // Once loaded, render nothing — the static page shows through.
  return null;
}
