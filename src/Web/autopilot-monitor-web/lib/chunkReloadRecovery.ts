"use client";

import { trackEvent } from "@/lib/appInsights";

/**
 * Recovery for the post-deploy stale-bundle failure mode: a tab still running the
 * previous deployment's client bundle asks for a lazily-loaded chunk (next/dynamic
 * charts, route chunks) whose content-hashed filename no longer exists after the
 * SWA swap. The import rejects (or the script tag 404s) and the user is stuck on
 * a "Loading chart…" placeholder until they reload by hand.
 *
 * The standard remedy is a single automatic reload — the fresh document references
 * the new bundle, so the retry succeeds. The reload MUST be bounded: if chunks are
 * genuinely unreachable (CDN outage), reloading again would loop. Hence the
 * sessionStorage timestamp guard: at most one auto-reload per tab per window; a
 * chunk error inside the guard window falls through to the normal error surface.
 */

const STORAGE_KEY = "am-chunk-reload-at";
/** Minimum gap between automatic reloads for one tab. */
export const RELOAD_WINDOW_MS = 5 * 60 * 1000;

/** True when the value looks like a failed chunk / dynamic-import load (webpack + browser variants). */
export function isChunkLoadError(reason: unknown): boolean {
  if (!reason) return false;
  const message =
    typeof reason === "string"
      ? reason
      : reason instanceof Error
        ? `${reason.name}: ${reason.message}`
        : "";
  return (
    /ChunkLoadError/.test(message) ||
    /Loading chunk [\w-]+ failed/i.test(message) ||
    /Loading CSS chunk/i.test(message) ||
    /Failed to fetch dynamically imported module/i.test(message) ||
    /error loading dynamically imported module/i.test(message) ||
    /Importing a module script failed/i.test(message)
  );
}

/** Pure guard decision — exported for tests. Allows a reload only outside the window. */
export function shouldAutoReload(lastReloadAt: number | null, now: number): boolean {
  return lastReloadAt === null || now - lastReloadAt >= RELOAD_WINDOW_MS;
}

function readLastReloadAt(): number | null {
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY);
    if (raw === null) return null;
    const parsed = Number(raw);
    return Number.isFinite(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

/**
 * Reload once if allowed. Returns true when a reload was initiated (callers should
 * treat the error as handled). Without working sessionStorage there is no loop
 * guard, so no auto-reload happens at all — the normal error surface takes over.
 */
export function tryRecoverFromChunkError(source: string): boolean {
  if (typeof window === "undefined") return false;

  const now = Date.now();
  if (!shouldAutoReload(readLastReloadAt(), now)) return false;
  try {
    window.sessionStorage.setItem(STORAGE_KEY, String(now));
  } catch {
    return false;
  }

  trackEvent("ChunkReloadRecovery", { source, path: window.location.pathname });
  window.location.reload();
  return true;
}
