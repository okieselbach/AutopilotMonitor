'use client';

import { useCallback, useEffect, useRef, useState } from 'react';

/**
 * Elapsed/estimate tracking for long-running fetches so loading states can show honest
 * progress instead of an opaque spinner. The estimate is the last recorded fetch duration
 * (persisted per storageKey in localStorage); callers should record only "fresh" computes —
 * a server cache hit finishing in ~100ms would make the next cold load look stuck at the cap.
 *
 * Usage: call begin() when the request starts and finish(recordEstimate) in finally.
 */
export const DEFAULT_FETCH_ESTIMATE_MS = 45_000;

function readEstimateMs(storageKey: string, defaultEstimateMs: number): number {
  try {
    const v = Number(localStorage.getItem(storageKey));
    return Number.isFinite(v) && v > 1_000 ? v : defaultEstimateMs;
  } catch {
    return defaultEstimateMs;
  }
}

export function useFetchProgress(storageKey: string, defaultEstimateMs = DEFAULT_FETCH_ESTIMATE_MS) {
  const startedAtRef = useRef<number | null>(null);
  const [active, setActive] = useState(false);
  const [elapsedMs, setElapsedMs] = useState(0);
  const [estimateMs, setEstimateMs] = useState(defaultEstimateMs);

  useEffect(() => {
    if (!active) return;
    const id = setInterval(() => {
      if (startedAtRef.current !== null) setElapsedMs(Date.now() - startedAtRef.current);
    }, 500);
    return () => clearInterval(id);
  }, [active]);

  const begin = useCallback(() => {
    startedAtRef.current = Date.now();
    setElapsedMs(0);
    setEstimateMs(readEstimateMs(storageKey, defaultEstimateMs));
    setActive(true);
  }, [storageKey, defaultEstimateMs]);

  const finish = useCallback((recordEstimate = true) => {
    const started = startedAtRef.current;
    if (recordEstimate && started !== null) {
      try {
        localStorage.setItem(storageKey, String(Date.now() - started));
      } catch {
        // storage unavailable — keep the default estimate
      }
    }
    startedAtRef.current = null;
    setActive(false);
  }, [storageKey]);

  return { begin, finish, elapsedMs, estimateMs, active };
}
