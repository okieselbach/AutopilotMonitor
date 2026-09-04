"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { isChunkLoadError, tryRecoverFromChunkError } from "@/utils/chunkReloadRecovery";
import { trackEvent } from "@/lib/appInsights";

/**
 * Route-level error boundary — catches unhandled exceptions within page
 * components while the root layout (and thus AuthProvider / Navbar) stays
 * intact. Typical trigger: MSAL interaction failure during re-auth after a
 * long idle period.
 */
export default function Error({
  error,
  reset,
}: {
  error: Error & { digest?: string };
  reset: () => void;
}) {
  const router = useRouter();

  // Stale-bundle chunk failure after a deploy: reload once instead of showing the
  // error card (utils/chunkReloadRecovery.ts — the guard prevents reload loops; a
  // repeat inside the guard window falls through to the card below).
  useEffect(() => {
    if (isChunkLoadError(error)) {
      tryRecoverFromChunkError("error-boundary");
      return;
    }
    // The digest is Next's stable hash of the error (also in the server log) — the only
    // reference a user can quote for a client-side crash; the event makes it searchable.
    trackEvent("client_error_boundary", { digest: error.digest ?? "", name: error.name });
  }, [error]);

  return (
    <div className="min-h-screen bg-[var(--lp-bg)] flex items-center justify-center p-4">
      <div className="bg-white rounded-lg shadow-xl p-8 max-w-md w-full text-center">
        <div className="text-4xl mb-3">!</div>
        <h2 className="text-xl font-semibold text-gray-900 mb-2">Something went wrong</h2>
        <p className="text-gray-600 mb-6 leading-relaxed">
          Your session may have expired. Try reloading the page or signing in again.
        </p>
        {error.digest && (
          <p className="text-[11px] text-gray-400 mb-4 font-mono">Ref {error.digest}</p>
        )}
        <div className="flex gap-3 justify-center flex-wrap">
          <button
            onClick={() => router.push("/")}
            className="px-5 py-2.5 bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors font-medium text-sm"
          >
            Back to Home
          </button>
          <button
            onClick={() => reset()}
            className="px-5 py-2.5 bg-gray-100 text-gray-700 border border-gray-300 rounded-lg hover:bg-gray-200 transition-colors font-medium text-sm"
          >
            Try again
          </button>
        </div>
      </div>
    </div>
  );
}
