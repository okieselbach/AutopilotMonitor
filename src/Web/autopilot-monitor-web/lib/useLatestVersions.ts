"use client";

import { useEffect, useState } from "react";
import { api } from "./api";
import { authenticatedFetch } from "./authenticatedFetch";

type GetAccessToken = (forceRefresh?: boolean) => Promise<string | null>;

export interface LatestVersionsResponse {
  latestAgentVersion?: string | null;
  latestBootstrapScriptVersion?: string | null;
  latestAgentSha256?: string | null;
  fetchedAtUtc?: string | null;
  source?: "cache" | "blob" | null;
}

export interface UseLatestVersionsResult {
  latestAgentVersion: string | null;
  latestBootstrapVersion: string | null;
  loading: boolean;
}

/**
 * Fetches latest published agent/bootstrap versions from the backend
 * once per mount. Backend cache and browser Cache-Control are both minutes,
 * not hours, so a fresh release shows up almost immediately — the What's new
 * panel announces this number, and the panel mounts on every open.
 *
 * Silently swallows all errors — on failure, returns nulls so callers
 * can gracefully hide "outdated" badges.
 */
export function useLatestVersions(getAccessToken: GetAccessToken): UseLatestVersionsResult {
  const [data, setData] = useState<LatestVersionsResponse | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;

    (async () => {
      try {
        const res = await authenticatedFetch(api.config.latestVersions(), getAccessToken, { method: "GET" });
        if (!res.ok) return;
        const json = (await res.json()) as LatestVersionsResponse;
        if (!cancelled) setData(json);
      } catch {
        // swallow — badges just won't render
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => { cancelled = true; };
    // getAccessToken identity typically stable from MSAL context; intentionally omit from deps
    // to avoid refetching on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return {
    latestAgentVersion: data?.latestAgentVersion ?? null,
    latestBootstrapVersion: data?.latestBootstrapScriptVersion ?? null,
    loading,
  };
}
