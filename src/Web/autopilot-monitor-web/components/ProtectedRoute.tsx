"use client";

import { useAuth } from "../contexts/AuthContext";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { savePostLoginReturnUrl } from "../lib/postLoginReturn";
import { isOnPublicHost } from "../lib/hostRouting";
import { consumeLoginDeclined, legacyConfigured, switchAuthApp } from "../lib/authApp";
import { activeAuthApp } from "../lib/msalConfig";
import { DOCS_PATHS } from "../lib/docsPaths";
import { DOCS_URL } from "../utils/config";
import { useAdminMode } from "../hooks/useAdminMode";

interface ProtectedRouteProps {
  children: React.ReactNode;
  requireGlobalAdmin?: boolean;
  /**
   * Require platform scope: Global Admin OR the read-only Global Reader. Use for cross-tenant areas
   * a reader may VIEW (e.g. the /admin data pages). Within such areas, mutating controls + the
   * platform-settings sub-pages stay gated on the real Global-Admin status (nav-hidden for readers,
   * backend-enforced 403/redaction).
   */
  requireGlobalScope?: boolean;
  /**
   * Require FLEET scope: full platform scope (Global Admin / Reader) OR a delegated ("MSP") admin who
   * manages a subset of tenants. Use for the fleet area, which a delegated admin may VIEW bounded to its
   * managed tenants. The backend enforces the per-tenant bound on every request; this only gates the
   * client route so a single-tenant user with no fleet doesn't land on an empty fleet page.
   */
  requireFleetScope?: boolean;
}

/**
 * Protects routes by requiring authentication. Optionally requires Global Admin, platform scope
 * (Global Admin or read-only Global Reader), or fleet scope (platform scope OR a delegated MSP admin).
 */
export function ProtectedRoute({ children, requireGlobalAdmin = false, requireGlobalScope = false, requireFleetScope = false }: ProtectedRouteProps) {
  const { isAuthenticated, user, hasGlobalScope, hasFleetScope, isLoading, login } = useAuth();
  const router = useRouter();

  // Once authenticated, remember it so transient auth-state flips (e.g. MSAL
  // handleRedirectPromise re-settling) don't unmount/flash the children. State
  // adjusted during render (guarded, converges after one extra render) — a ref
  // written during render would tear under concurrent rendering.
  const [wasAuthenticated, setWasAuthenticated] = useState(false);
  if (isAuthenticated && !wasAuthenticated) {
    setWasAuthenticated(true);
  }

  // Prevent infinite redirect loops: only attempt re-login once per mount.
  const reloginAttempted = useRef(false);
  // Start in the failed state when the user actively DECLINED a consent prompt on the last
  // redirect (dual app-reg safety net, AADSTS65004): re-firing the auto login would bounce
  // them straight back to the same consent screen — show the manual retry UI (with the
  // previous-app link) instead. One-shot marker, consumed here; a later manual retry runs
  // the normal flow again. Lazy initializer: consumed once per mount, never during SSR
  // (consumeLoginDeclined is try/catch-guarded against a missing window).
  const [loginFailed, setLoginFailed] = useState(() => consumeLoginDeclined());

  useEffect(() => {
    if (!isLoading && !isAuthenticated) {
      // On the PUBLIC host this page is about to be moved to portal by
      // HostRoutingGuard — starting an MSAL interaction here would sign the
      // user in on the WRONG origin (www) and force a second sign-in after
      // the handover. Stand down and let the guard navigate.
      if (isOnPublicHost()) {
        return;
      }
      // Failed state (consent declined / earlier login error): stand down so the manual
      // retry UI stays in control instead of auto-firing another redirect.
      if (loginFailed) {
        reloginAttempted.current = true;
        return;
      }
      if (!reloginAttempted.current) {
        // Trigger MSAL login redirect once. On portal this is also the
        // entry-point flow for users who arrived via a www → portal
        // cross-origin sign-in nav with no portal-side session yet. `auto`
        // drops the account picker so an existing Entra session completes
        // silently (no visible second sign-in).
        reloginAttempted.current = true;
        // Stash the deep link so AuthGate can restore it after re-auth. MSAL has
        // navigateToLoginRequestUrl=false, so without this the user lands on the
        // role-default route (e.g. /dashboard) instead of the page they opened.
        savePostLoginReturnUrl(window.location.pathname + window.location.search);
        login({ auto: true }).catch((err) => {
          // Do NOT navigate to a public path here: auth state is per-origin, so
          // "/" bounces (HostRoutingGuard) back to www, whose AuthGate pushes an
          // authenticated user straight back here — a hard redirect loop
          // (production incident 2026-07-29). Fail in place with a retry UI.
          console.warn('[ProtectedRoute] Login redirect failed:', err);
          setLoginFailed(true);
        });
      }
    }
  }, [isAuthenticated, isLoading, router, login, loginFailed]);

  // Demo ("presentation") mode: a platform route reached by a typed URL or a stale bookmark while
  // presenting live. Bounce silently to the tenant dashboard instead of rendering the Access Denied
  // card below — its text ("You need Global Admin permissions") would itself reveal that a platform
  // area exists. Presentation only: the route opens again the moment demo mode is cleared, and the
  // backend gates it either way. See lib/demoMode.ts.
  const { demoMode } = useAdminMode();
  const demoBlockedPlatformRoute = demoMode && (requireGlobalAdmin || requireGlobalScope);
  useEffect(() => {
    if (demoBlockedPlatformRoute && isAuthenticated) {
      router.replace("/dashboard");
    }
  }, [demoBlockedPlatformRoute, isAuthenticated, router]);

  // Sign-in failed (e.g. an interrupted earlier redirect left MSAL in
  // interaction_in_progress) — stay on this origin and offer a manual retry.
  if (!isAuthenticated && !isLoading && loginFailed) {
    return (
      <div className="min-h-screen bg-[var(--lp-bg)] flex items-center justify-center p-4">
        <div className="bg-white rounded-lg shadow-xl p-8 max-w-md w-full text-center">
          <h2 className="text-xl font-semibold text-gray-900 mb-2">Sign-in required</h2>
          <p className="text-gray-600 mb-6">We couldn&apos;t sign you in automatically.</p>
          <button
            onClick={() => {
              setLoginFailed(false);
              login().catch(() => setLoginFailed(true));
            }}
            className="px-4 py-2 bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors"
          >
            Sign in
          </button>
          {legacyConfigured() && activeAuthApp !== "legacy" && (
            <p className="mt-4 text-sm text-gray-500">
              Existing customer?{" "}
              <button
                onClick={() => switchAuthApp("legacy")}
                className="text-green-700 underline hover:text-green-800"
              >
                Sign in with the previous Autopilot Monitor app
              </button>
            </p>
          )}
          {legacyConfigured() && (
            <p className="mt-2 text-xs text-gray-400">
              <a
                href={`${DOCS_URL}${DOCS_PATHS.appRegistrationMigration}`}
                target="_blank"
                rel="noopener noreferrer"
                className="underline hover:text-gray-600"
              >
                Why are there two Autopilot Monitor apps?
              </a>
            </p>
          )}
        </div>
      </div>
    );
  }

  // Show loading spinner while MSAL settles or while re-login redirect is pending.
  if (isLoading || (!isAuthenticated && wasAuthenticated)) {
    return (
      <div className="min-h-screen bg-[var(--lp-bg)] flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-green-600 mx-auto"></div>
          <p className="mt-4 text-gray-600">Loading...</p>
        </div>
      </div>
    );
  }

  // Show nothing if never authenticated (will redirect)
  if (!isAuthenticated && !wasAuthenticated) {
    return null;
  }

  // Render nothing while the demo-mode bounce above navigates away.
  if (demoBlockedPlatformRoute) {
    return null;
  }

  // Show nothing if the route's platform requirement isn't met.
  // requireGlobalAdmin → real Global Admin only; requireGlobalScope → Global Admin OR Global Reader.
  const platformDenied =
    (requireGlobalAdmin && user && !user.isGlobalAdmin) ||
    (requireGlobalScope && user && !hasGlobalScope) ||
    (requireFleetScope && user && !hasFleetScope);
  if (platformDenied) {
    return (
      <div className="min-h-screen bg-[var(--lp-bg)] flex items-center justify-center p-4">
        <div className="bg-white rounded-lg shadow-xl p-8 max-w-md w-full text-center">
          <svg className="h-12 w-12 text-red-500 mx-auto mb-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
          </svg>
          <h2 className="text-xl font-semibold text-gray-900 mb-2">Access Denied</h2>
          <p className="text-gray-600 mb-6">You need Global Admin permissions to access this page.</p>
          <button
            onClick={() => router.push("/dashboard")}
            className="px-4 py-2 bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors"
          >
            Back to Home
          </button>
        </div>
      </div>
    );
  }

  // Render children if authenticated (and global admin if required)
  return <>{children}</>;
}
