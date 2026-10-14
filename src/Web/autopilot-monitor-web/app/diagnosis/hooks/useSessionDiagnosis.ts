"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { fetchJson } from "@/lib/apiClient";
import { isGuid } from "@/lib/inputValidation";
import { createBurstScheduler, type BurstScheduler } from "@/lib/burstScheduler";
import { useLatest } from "@/hooks/useLatest";
import type { DiagnosisHeadline, GetSessionDiagnosisResponse } from "@/lib/generated/wire-types.generated";

export type DiagnosisLoadState = "loading" | "ready" | "error";

/**
 * The session's verdict (GET sessions/{id}/diagnosis). A live session pushes with every agent batch;
 * `scheduleRefetch` coalesces those into one read after each burst. A failed refresh keeps the last
 * verdict — only a failed first read is an error.
 */
export function useSessionDiagnosis(
  sessionId: string,
  tenantId: string | null,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>
) {
  const [headline, setHeadline] = useState<DiagnosisHeadline | null>(null);
  const [state, setState] = useState<DiagnosisLoadState>("loading");
  const getToken = useLatest(getAccessToken);
  const scheduler = useRef<BurstScheduler | null>(null);

  const fetchHeadline = useCallback(async () => {
    if (!sessionId || !tenantId || !isGuid(tenantId)) return;
    try {
      const json = await fetchJson<GetSessionDiagnosisResponse>(api.sessions.diagnosis(sessionId, tenantId), (force?: boolean) =>
        getToken.current(force)
      );
      setHeadline(json.headline);
      setState("ready");
    } catch {
      setState((prev) => (prev === "ready" ? prev : "error"));
    }
  }, [sessionId, tenantId, getToken]);

  const fetchLatest = useLatest(fetchHeadline);

  useEffect(() => {
    const run = async () => {
      await fetchHeadline();
    };
    void run();
  }, [fetchHeadline]);

  useEffect(() => () => scheduler.current?.cancel(), []);

  const scheduleRefetch = useCallback(() => {
    scheduler.current ??= createBurstScheduler(() => void fetchLatest.current(), { trailingMs: 1500, maxWaitMs: 10_000 });
    scheduler.current.trigger();
  }, [fetchLatest]);

  return { headline, state, refetch: fetchHeadline, scheduleRefetch };
}
