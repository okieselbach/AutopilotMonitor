"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { fetchJson, type GetAccessToken } from "@/lib/apiClient";
import type { ProgressTenantStatusResponse } from "@/utils/wire-types.generated";

/**
 * One read of the caller's tenant status for the access hint, only for members without a role
 * (`enabled`). Any failure leaves null: the hint then stays the plain member note, so nothing here
 * is worth a notification.
 */
export function useProgressTenantStatus(
  enabled: boolean,
  getAccessToken: GetAccessToken,
): ProgressTenantStatusResponse | null {
  const [status, setStatus] = useState<ProgressTenantStatusResponse | null>(null);

  useEffect(() => {
    if (!enabled) return;
    let cancelled = false;
    const run = async () => {
      const data = await fetchJson<ProgressTenantStatusResponse>(api.progress.tenantStatus(), getAccessToken)
        .catch(() => null);
      if (!cancelled) setStatus(data);
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [enabled, getAccessToken]);

  return status;
}
