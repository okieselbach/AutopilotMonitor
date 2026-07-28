"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import type { SignalsResponse, SignalRecord } from "../types";

interface UseSessionSignalsParams {
  sessionId: string;
  tenantId?: string;
  maxResults?: number;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
}

export interface UseSessionSignalsReturn {
  signals: SignalRecord[];
  count: number;
  truncated: boolean;
  loading: boolean;
  error: string | null;
  reload: () => void;
}

/**
 * Loads the SignalLog for the session. Page size defaults to backend default
 * (1000); cap is 5000. The Inspector renders signals in `sessionTraceOrdinal`
 * order — backend already sorts by `RowKey` (= `sessionSignalOrdinal`), which
 * is monotonic per signal-log; we trust that ordering for v1.
 */
export function useSessionSignals({
  sessionId,
  tenantId,
  maxResults,
  getAccessToken,
}: UseSessionSignalsParams): UseSessionSignalsReturn {
  const [signals, setSignals] = useState<SignalRecord[]>([]);
  const [count, setCount] = useState(0);
  const [truncated, setTruncated] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadCounter, setReloadCounter] = useState(0);

  useEffect(() => {
    if (!sessionId) return;
    let aborted = false;

    setLoading(true);
    setError(null);

    (async () => {
      try {
        const url = api.inspector.signals(sessionId, { tenantId, maxResults });
        const response = await authenticatedFetch(url, getAccessToken);
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}: ${response.statusText}`);
        }
        const data = (await response.json()) as SignalsResponse;
        if (aborted) return;
        setSignals(data.signals);
        setCount(data.count);
        setTruncated(data.truncated);
      } catch (err) {
        if (aborted) return;
        if (err instanceof TokenExpiredError) {
          setError("Session expired — please reload to sign in again.");
        } else {
          setError(err instanceof Error ? err.message : "Failed to load signals");
        }
      } finally {
        if (!aborted) setLoading(false);
      }
    })();

    return () => {
      aborted = true;
    };
  }, [sessionId, tenantId, maxResults, getAccessToken, reloadCounter]);

  return {
    signals,
    count,
    truncated,
    loading,
    error,
    reload: () => setReloadCounter((n) => n + 1),
  };
}
