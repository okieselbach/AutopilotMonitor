"use client";

import { useEffect, useState } from "react";
import { api } from "./api";
import { cachedAuthFetchJson, LATEST_VERSIONS_TTL_MS } from "./cachedAuthFetch";

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
 * Fetches latest published agent/bootstrap versions from the backend, served
 * from the per-tab lookup cache (LATEST_VERSIONS_TTL_MS, matching the backend's
 * own 5-min manifest cache) so the What's new panel and the version badges do
 * not re-fetch on every mount; a fresh release still shows up within minutes.
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
        const json = await cachedAuthFetchJson<LatestVersionsResponse>(api.config.latestVersions(), getAccessToken, { ttlMs: LATEST_VERSIONS_TTL_MS });
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
