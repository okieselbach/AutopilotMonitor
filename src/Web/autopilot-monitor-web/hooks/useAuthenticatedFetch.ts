"use client";

import { useState, useCallback, useRef } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { useNotifications } from "@/contexts/NotificationContext";
import {
  authenticatedFetch,
  TokenExpiredError,
} from "@/lib/authenticatedFetch";

export interface UseAuthenticatedFetchOptions {
  /** Called on non-TokenExpiredError failures. If not set, only the error state is updated. */
  onError?: (error: Error) => void;
  /** Called on TokenExpiredError. Default: addNotification via NotificationContext. */
  onTokenExpired?: (error: TokenExpiredError) => void;
}

export interface ExecuteOptions<T> {
  /** Transform the raw JSON before storing in data state. */
  transform?: (json: unknown) => T;
  /** If true, skip setting loading state (useful for background refreshes). */
  silent?: boolean;
}

export interface UseAuthenticatedFetchReturn<T> {
  data: T | null;
  loading: boolean;
  error: string | null;
  /** Execute a fetch request. Returns the parsed data or null on failure. */
  execute: (
    url: string,
    init?: RequestInit,
    options?: ExecuteOptions<T>,
  ) => Promise<T | null>;
  clearError: () => void;
  setData: React.Dispatch<React.SetStateAction<T | null>>;
}

export function useAuthenticatedFetch<T = unknown>(
  options?: UseAuthenticatedFetchOptions,
): UseAuthenticatedFetchReturn<T> {
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();

  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Stabilize callback refs so `execute` identity doesn't change on every render
  const optionsRef = useRef(options);
  optionsRef.current = options;

  // Latest-wins guard: when execute() is called again while a previous request is still in
  // flight (e.g. the tenant-scope selector seeds a persisted foreign tenant right after mount,
  // firing an own-tenant fetch immediately followed by the override fetch), only the most
  // recently started request may write data/error/loading. Without this the LAST-RESOLVED
  // response wins and a stale tenant's data can overwrite the current selection's result.
  // The stale call still returns its parsed result to its direct awaiter.
  const requestSeqRef = useRef(0);
  // The loading spinner is owned by the last-started NON-silent request (a later silent
  // background refresh must not leave a superseded visible request's spinner stuck on).
  const loadingSeqRef = useRef(0);

  const clearError = useCallback(() => setError(null), []);

  const execute = useCallback(
    async (
      url: string,
      init?: RequestInit,
      executeOptions?: ExecuteOptions<T>,
    ): Promise<T | null> => {
      const seq = ++requestSeqRef.current;
      const isCurrent = () => requestSeqRef.current === seq;
      try {
        if (!executeOptions?.silent) {
          loadingSeqRef.current = seq;
          setLoading(true);
        }
        setError(null);

        const response = await authenticatedFetch(url, getAccessToken, init);

        if (!response.ok) {
          // Try to extract error message from response body (API returns { message: "..." })
          let message = `Failed: ${response.statusText}`;
          try {
            const errorData = await response.json();
            if (errorData.message) message = errorData.message;
          } catch {
            /* response body wasn't JSON */
          }
          throw new Error(message);
        }

        if (response.status === 204) {
          return null;
        }

        const json = await response.json();
        const result = executeOptions?.transform
          ? executeOptions.transform(json)
          : (json as T);
        if (isCurrent()) {
          setData(result);
        }
        return result;
      } catch (err) {
        // A superseded request's failure is irrelevant to the current view — the newer
        // request reports its own outcome. Swallow it entirely (no state, no callbacks).
        if (!isCurrent()) {
          return null;
        }
        const opts = optionsRef.current;
        if (err instanceof TokenExpiredError) {
          if (opts?.onTokenExpired) {
            opts.onTokenExpired(err);
          } else {
            addNotification(
              "error",
              "Session Expired",
              err.message,
              "session-expired",
            );
          }
        } else if (opts?.onError) {
          opts.onError(
            err instanceof Error ? err : new Error(String(err)),
          );
        }

        const message =
          err instanceof Error ? err.message : "An unknown error occurred";
        setError(message);
        return null;
      } finally {
        // Only the spinner's owner may clear it — a superseded request finishing early
        // must not hide the loading state of the one still in flight.
        if (!executeOptions?.silent && loadingSeqRef.current === seq) {
          setLoading(false);
        }
      }
    },
    [getAccessToken, addNotification],
  );

  return { data, loading, error, execute, clearError, setData };
}
