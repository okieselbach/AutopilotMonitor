"use client";

import { useEffect, useRef, type RefObject } from "react";

/**
 * Returns a ref that always holds the latest `value`, synced in a passive effect
 * (never during render — render-phase ref writes tear under concurrent rendering,
 * flagged by react-hooks/refs).
 *
 * Use for long-lived closures (SignalR handlers, fetch callbacks, interval timers)
 * that must see current props/state without being re-created per render.
 *
 * Timing contract: `ref.current` updates AFTER commit. That is safe for async
 * callbacks and event handlers, and for sibling effects declared after the hook
 * call (effects flush in registration order). Do NOT read it during render, and
 * do not rely on it from layout effects or child-component effects — those run
 * before this hook's sync effect and would observe the previous render's value.
 */
export function useLatest<T>(value: T): RefObject<T> {
  const ref = useRef(value);
  useEffect(() => {
    ref.current = value;
  });
  return ref;
}
