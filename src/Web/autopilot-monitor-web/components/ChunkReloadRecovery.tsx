"use client";

import { useEffect } from "react";
import { isChunkLoadError, tryRecoverFromChunkError } from "@/utils/chunkReloadRecovery";

/**
 * Global listener pair for stale-bundle chunk failures (see utils/chunkReloadRecovery.ts).
 * Two paths surface them outside any React error boundary:
 *  - a rejected dynamic import that nothing awaits → unhandledrejection
 *  - the chunk <script>/<link> tag itself failing to load → a capture-phase error
 *    event on window whose target is the tag (these don't bubble and carry no Error)
 * Renders nothing; mounted once in the root layout.
 */
export default function ChunkReloadRecovery() {
  useEffect(() => {
    const onRejection = (e: PromiseRejectionEvent) => {
      if (isChunkLoadError(e.reason) && tryRecoverFromChunkError("unhandledrejection")) {
        e.preventDefault();
      }
    };

    const onError = (e: ErrorEvent | Event) => {
      // Resource-load failure: target is the failed tag, no error object attached.
      const target = e.target as HTMLElement | null;
      if (target && (target.tagName === "SCRIPT" || target.tagName === "LINK")) {
        const url =
          (target as HTMLScriptElement).src || (target as HTMLLinkElement).href || "";
        // Only our own immutable build assets — a blocked third-party script must not reload the app.
        if (url.includes("/_next/") && tryRecoverFromChunkError("resource-error")) return;
      }
      const error = (e as ErrorEvent).error ?? (e as ErrorEvent).message;
      if (isChunkLoadError(error)) {
        tryRecoverFromChunkError("window-error");
      }
    };

    window.addEventListener("unhandledrejection", onRejection);
    window.addEventListener("error", onError, true);
    return () => {
      window.removeEventListener("unhandledrejection", onRejection);
      window.removeEventListener("error", onError, true);
    };
  }, []);

  return null;
}
