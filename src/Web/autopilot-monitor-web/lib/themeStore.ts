"use client";

export type Theme = "light" | "dark";

// Module-level theme store for useSyncExternalStore. The snapshot logic owns the
// precedence rule (manual localStorage preference beats the OS preference); an OS-level
// change while a manual preference exists notifies subscribers but yields an unchanged
// snapshot, so React bails out without re-rendering.
const listeners = new Set<() => void>();

export function getThemeSnapshot(): Theme {
  const stored = localStorage.getItem("theme");
  if (stored === "dark" || stored === "light") return stored;
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

// Statically exported pages prerender light; the client re-renders with the real theme.
export const getServerThemeSnapshot = (): Theme => "light";

export function subscribeTheme(onStoreChange: () => void): () => void {
  const mql = window.matchMedia("(prefers-color-scheme: dark)");
  mql.addEventListener("change", onStoreChange);
  listeners.add(onStoreChange);
  return () => {
    mql.removeEventListener("change", onStoreChange);
    listeners.delete(onStoreChange);
  };
}

// Persist a manual preference and notify subscribers (a same-tab localStorage write
// fires no "storage" event, so the store keeps its own listener list).
export function setStoredTheme(next: Theme): void {
  localStorage.setItem("theme", next);
  for (const listener of [...listeners]) listener();
}
