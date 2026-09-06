"use client";

import { useCallback, useMemo, useState } from "react";

/**
 * Per-viewer collapse state for grouped lists, persisted in localStorage.
 *
 * One storage key per page; inside it one string[] of collapsed group tokens per `scope`
 * (the rule pages use their tab as scope, so "Rules" and "Templates" remember independently).
 * The stored value is the render baseline (read once per key via useMemo); every change goes
 * into React state AND back to storage, so there is no state-sync effect. Storage access is
 * best-effort — a blocked or unavailable localStorage degrades to "everything expanded".
 */

type ScopedSets = Record<string, ReadonlySet<string>>;

function readStore(storageKey: string): ScopedSets {
  if (typeof window === "undefined") return {};
  try {
    const raw = window.localStorage.getItem(storageKey);
    if (!raw) return {};
    const parsed: unknown = JSON.parse(raw);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return {};
    const out: ScopedSets = {};
    for (const [scope, value] of Object.entries(parsed as Record<string, unknown>)) {
      if (Array.isArray(value)) {
        out[scope] = new Set(value.filter((v): v is string => typeof v === "string"));
      }
    }
    return out;
  } catch {
    return {};
  }
}

function writeStore(storageKey: string, store: ScopedSets): void {
  if (typeof window === "undefined") return;
  try {
    const serializable: Record<string, string[]> = {};
    for (const [scope, set] of Object.entries(store)) {
      if (set.size > 0) serializable[scope] = Array.from(set);
    }
    window.localStorage.setItem(storageKey, JSON.stringify(serializable));
  } catch {
    // Persistence is a convenience; the in-memory state still applies for this page view.
  }
}

const EMPTY: ReadonlySet<string> = new Set();

export interface CollapsedGroups {
  /** Collapsed tokens for a scope (empty set = everything expanded). */
  collapsed: (scope: string) => ReadonlySet<string>;
  toggle: (scope: string, token: string) => void;
  /** Make sure one group is visible — used when the page programmatically opens an item inside it. */
  expand: (scope: string, token: string) => void;
  expandAll: (scope: string) => void;
  collapseAll: (scope: string, tokens: readonly string[]) => void;
}

export function useCollapsedGroups(storageKey: string): CollapsedGroups {
  const baseline = useMemo(() => readStore(storageKey), [storageKey]);
  const [overrides, setOverrides] = useState<Record<string, ScopedSets>>({});

  const current = useCallback((): ScopedSets => overrides[storageKey] ?? baseline, [overrides, storageKey, baseline]);

  const commit = useCallback((next: ScopedSets) => {
    writeStore(storageKey, next);
    setOverrides((prev) => ({ ...prev, [storageKey]: next }));
  }, [storageKey]);

  const collapsed = useCallback((scope: string) => current()[scope] ?? EMPTY, [current]);

  const toggle = useCallback((scope: string, token: string) => {
    const store = current();
    const set = new Set(store[scope] ?? EMPTY);
    if (set.has(token)) set.delete(token); else set.add(token);
    commit({ ...store, [scope]: set });
  }, [current, commit]);

  const expand = useCallback((scope: string, token: string) => {
    const store = current();
    const set = store[scope];
    if (!set || !set.has(token)) return;
    const next = new Set(set);
    next.delete(token);
    commit({ ...store, [scope]: next });
  }, [current, commit]);

  const expandAll = useCallback((scope: string) => {
    commit({ ...current(), [scope]: EMPTY });
  }, [current, commit]);

  const collapseAll = useCallback((scope: string, tokens: readonly string[]) => {
    commit({ ...current(), [scope]: new Set(tokens) });
  }, [current, commit]);

  return { collapsed, toggle, expand, expandAll, collapseAll };
}
