"use client";

import { useCallback, useSyncExternalStore } from "react";

/**
 * Subscribes to a CSS media query via useSyncExternalStore.
 *
 * The server snapshot is configurable (default false) so statically exported pages
 * hydrate against the markup they were prerendered with and re-render once the real
 * match is known — the same two-phase behavior as the classic mount-effect pattern,
 * without any effect-phase setState.
 */
export function useMediaQuery(query: string, serverSnapshot = false): boolean {
  const subscribe = useCallback(
    (onStoreChange: () => void) => {
      const mql = window.matchMedia(query);
      mql.addEventListener("change", onStoreChange);
      return () => mql.removeEventListener("change", onStoreChange);
    },
    [query],
  );
  const getSnapshot = useCallback(() => window.matchMedia(query).matches, [query]);
  return useSyncExternalStore(subscribe, getSnapshot, () => serverSnapshot);
}
