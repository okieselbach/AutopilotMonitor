"use client";

import { useState } from "react";
import type { SortDir } from "./mcpUsageLogic";

/** A sortable column set: a click on the active column flips the direction, another column starts at its default. */
export function useSortState<K extends string>(initial: K, defaultDir: Record<K, SortDir>) {
  const [sortKey, setSortKey] = useState<K>(initial);
  const [sortDir, setSortDir] = useState<SortDir>(defaultDir[initial]);
  const onSort = (key: K) => {
    if (key === sortKey) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortKey(key);
      setSortDir(defaultDir[key]);
    }
  };
  return { sortKey, sortDir, onSort };
}

/** Which rows of a list are open. */
export function useOpenSet() {
  const [open, setOpen] = useState<ReadonlySet<string>>(() => new Set());
  const toggle = (id: string) =>
    setOpen((current) => {
      const next = new Set(current);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  return { open, toggle };
}
