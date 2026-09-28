"use client";

import { useCallback, useRef, useState } from "react";
import { useLatest } from "@/hooks/useLatest";
import {
  afterSuccess,
  cursorFor,
  initialCursorPagerState,
  pageNumberOf,
  type CursorPage,
  type CursorPagerState,
  type PageMove,
} from "@/lib/cursorPager";

export interface CursorPager<T> {
  /** Rows of the page on screen. */
  items: T[];
  /** Local edit of the rows on screen (e.g. a row updated in a detail dialog). */
  updateItems: (update: (items: T[]) => T[]) => void;
  pageNumber: number;
  hasNext: boolean;
  hasPrev: boolean;
  /** The move in flight, if any. */
  pending: PageMove | null;
  /** The first load has finished, successfully or not. */
  settled: boolean;
  /** Load the first page (initial load and every filter change). */
  reset: () => Promise<void>;
  next: () => Promise<void>;
  prev: () => Promise<void>;
  refresh: () => Promise<void>;
}

/**
 * Pattern B1 paging state (see lib/cursorPager). `fetchPage` loads the page at `cursor` and returns
 * it, or null when the request failed — it reports the error itself; the page on screen then stays
 * as it was. Only the latest move commits, so a slow response can never overwrite a newer page.
 * `fetchPage` may change identity every render: the latest one is always called.
 */
export function useCursorPager<T>(
  fetchPage: (cursor: string | null) => Promise<CursorPage<T> | null>,
): CursorPager<T> {
  const fetchRef = useLatest(fetchPage);
  // Moves read and write the ref (callbacks only, never during render); the state mirrors it for rendering.
  const stateRef = useRef<CursorPagerState<T>>(initialCursorPagerState<T>());
  const [state, setState] = useState<CursorPagerState<T>>(initialCursorPagerState<T>);
  const [pending, setPending] = useState<PageMove | null>(null);
  const [settled, setSettled] = useState(false);
  const seqRef = useRef(0);

  const commit = useCallback((next: CursorPagerState<T>) => {
    stateRef.current = next;
    setState(next);
  }, []);

  const run = useCallback(
    async (move: PageMove) => {
      const cursor = cursorFor(stateRef.current, move);
      if (cursor === undefined) return;
      const seq = ++seqRef.current;
      setPending(move);
      try {
        const page = await fetchRef.current(cursor);
        if (seq !== seqRef.current || page === null) return;
        commit(afterSuccess(stateRef.current, move, cursor, page));
      } finally {
        if (seq === seqRef.current) {
          setPending(null);
          setSettled(true);
        }
      }
    },
    [commit, fetchRef],
  );

  const reset = useCallback(() => run("reset"), [run]);
  const next = useCallback(() => run("next"), [run]);
  const prev = useCallback(() => run("prev"), [run]);
  const refresh = useCallback(() => run("refresh"), [run]);
  const updateItems = useCallback(
    (update: (items: T[]) => T[]) => commit({ ...stateRef.current, items: update(stateRef.current.items) }),
    [commit],
  );

  return {
    items: state.items,
    updateItems,
    pageNumber: pageNumberOf(state),
    hasNext: cursorFor(state, "next") !== undefined,
    hasPrev: cursorFor(state, "prev") !== undefined,
    pending,
    settled,
    reset,
    next,
    prev,
    refresh,
  };
}
