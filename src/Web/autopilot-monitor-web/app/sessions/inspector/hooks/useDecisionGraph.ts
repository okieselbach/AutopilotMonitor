"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import type { DecisionGraphResponse, DecisionGraphProjection } from "../types";

interface UseDecisionGraphParams {
  sessionId: string;
  tenantId?: string;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
}

export interface UseDecisionGraphReturn {
  graph: DecisionGraphProjection | null;
  truncated: boolean;
  loading: boolean;
  error: string | null;
  reload: () => void;
}

/**
 * Loads the pre-projected DecisionGraph for the session. The Inspector treats
 * an empty Nodes list as the "this session has no V2 decision data" lineage
 * signal — V1-agent sessions return a 200 with `nodes: []`.
 */
export function useDecisionGraph({
  sessionId,
  tenantId,
  getAccessToken,
}: UseDecisionGraphParams): UseDecisionGraphReturn {
  const [graph, setGraph] = useState<DecisionGraphProjection | null>(null);
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
        const url = api.inspector.decisionGraph(sessionId, tenantId);
        const response = await authenticatedFetch(url, getAccessToken);
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}: ${response.statusText}`);
        }
        const data = (await response.json()) as DecisionGraphResponse;
        if (aborted) return;
        setGraph(data.graph);
        setTruncated(data.truncated);
      } catch (err) {
        if (aborted) return;
        if (err instanceof TokenExpiredError) {
          setError("Session expired — please reload to sign in again.");
        } else {
          setError(err instanceof Error ? err.message : "Failed to load decision graph");
        }
      } finally {
        if (!aborted) setLoading(false);
      }
    })();

    return () => {
      aborted = true;
    };
  }, [sessionId, tenantId, getAccessToken, reloadCounter]);

  return {
    graph,
    truncated,
    loading,
    error,
    reload: () => setReloadCounter((n) => n + 1),
  };
}
