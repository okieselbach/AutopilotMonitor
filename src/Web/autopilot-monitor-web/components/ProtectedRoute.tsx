"use client";

import { useAuth } from "../contexts/AuthContext";
import { useRouter } from "next/navigation";
import { useEffect, useRef } from "react";

interface ProtectedRouteProps {
  children: React.ReactNode;
  requireGlobalAdmin?: boolean;
}

/**
 * Protects routes by requiring authentication
 * Optionally requires Global Admin role
 */
export function ProtectedRoute({ children, requireGlobalAdmin = false }: ProtectedRouteProps) {
  const { isAuthenticated, user, isLoading, login } = useAuth();
  const router = useRouter();

  // Once authenticated, remember it so transient auth-state flips (e.g. MSAL
  // handleRedirectPromise re-settling) don't unmount/flash the children.
  const wasAuthenticated = useRef(false);
  if (isAuthenticated) {
    wasAuthenticated.current = true;
  }

  // Prevent infinite redirect loops: only attempt re-login once per mount.
  const reloginAttempted = useRef(false);

  useEffect(() => {
    if (!isLoading && !isAuthenticated) {
      if (!reloginAttempted.current) {
        // Trigger MSAL login redirect once. On portal this is also the
        // entry-point flow for users who arrived via a www → portal
        // cross-origin sign-in nav with no portal-side session yet.
        reloginAttempted.current = true;
        login().catch((err) => {
          console.warn('[ProtectedRoute] Login redirect failed, navigating to landing:', err);
          router.push("/");
        });
      } else {
        // Login redirect already attempted — fall back to the public landing.
        // The 100ms timeout lets any in-flight MSAL redirect settle first.
        const id = setTimeout(() => router.push("/"), 100);
        return () => clearTimeout(id);
      }
    }
  }, [isAuthenticated, isLoading, router, login]);

  // Show loading spinner while MSAL settles or while re-login redirect is pending.
  if (isLoading || (!isAuthenticated && wasAuthenticated.current)) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <p className="mt-4 text-gray-600">Loading...</p>
        </div>
      </div>
    );
  }

  // Show nothing if never authenticated (will redirect)
  if (!isAuthenticated && !wasAuthenticated.current) {
    return null;
  }

  // Show nothing if requires global admin but user is not (will redirect)
  if (requireGlobalAdmin && user && !user.isGlobalAdmin) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100 flex items-center justify-center p-4">
        <div className="bg-white rounded-lg shadow-xl p-8 max-w-md w-full text-center">
          <svg className="h-12 w-12 text-red-500 mx-auto mb-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
          </svg>
          <h2 className="text-xl font-semibold text-gray-900 mb-2">Access Denied</h2>
          <p className="text-gray-600 mb-6">You need Global Admin permissions to access this page.</p>
          <button
            onClick={() => router.push("/dashboard")}
            className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 transition-colors"
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
