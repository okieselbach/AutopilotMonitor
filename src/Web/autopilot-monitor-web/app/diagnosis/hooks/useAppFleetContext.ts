"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { fetchJson } from "@/lib/apiClient";
import { useLatest } from "@/hooks/useLatest";
import { isGuid } from "@/lib/inputValidation";
import type { DiagnosisCulprit, GetAppFleetContextResponse } from "@/lib/generated/wire-types.generated";

/**
 * How the verdict's app fared in the tenant's other enrollments of the last week, read once per app
 * and session (the headline's live refetches do not repeat it). Null while loading, without an app
 * culprit and on any error: the fleet line is context, never required.
 */
export function useAppFleetContext(
  culprit: DiagnosisCulprit | null | undefined,
  sessionId: string,
  tenantId: string | null | undefined,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>
): GetAppFleetContextResponse | null {
  const getToken = useLatest(getAccessToken);
  const appName = culprit?.kind === "app" ? culprit.name : undefined;
  const appId = culprit?.kind === "app" ? culprit.key : undefined;
  const key = appName && sessionId && tenantId && isGuid(tenantId) ? [tenantId, sessionId, appName, appId ?? ""].join("|") : null;
  const [loaded, setLoaded] = useState<{ key: string; context: GetAppFleetContextResponse | null } | null>(null);

  useEffect(() => {
    if (!key || !appName || !tenantId) return;
    let cancelled = false;
    const run = async () => {
      try {
        const context = await fetchJson<GetAppFleetContextResponse>(
          api.apps.fleetContext(appName, { appId: appId || undefined, excludeSessionId: sessionId, tenantId }),
          (force?: boolean) => getToken.current(force)
        );
        if (!cancelled) setLoaded({ key, context });
      } catch {
        // fail-soft: no fleet line
        if (!cancelled) setLoaded({ key, context: null });
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [key, appName, appId, sessionId, tenantId, getToken]);

  return loaded?.key === key ? loaded.context : null;
}
