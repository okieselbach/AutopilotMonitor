"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { trackEvent } from "@/lib/appInsights";
import { Session } from "@/types";
import { type NotificationType, notifyApiError } from "@/contexts/NotificationContext";
import type { ProgressLookupSessionResponse } from "@/utils/wire-types.generated";
import { ApiError, fetchJson } from "@/lib/apiClient";
import { readSerialParam, withSerialParam } from "./serialDeepLink";

type AddNotification = (
  type: NotificationType,
  title: string,
  message: string,
  key?: string,
  href?: string,
) => void;

interface UseProgressSearchParams {
  tenantId: string;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  addNotification: AddNotification;
  onBeforeSearch?: () => void;
}

export interface UseProgressSearchReturn {
  serialInput: string;
  setSerialInput: React.Dispatch<React.SetStateAction<string>>;
  session: Session | null;
  setSession: React.Dispatch<React.SetStateAction<Session | null>>;
  searching: boolean;
  searched: boolean;
  notFound: boolean;
  headerCollapsed: boolean;
  setHeaderCollapsed: React.Dispatch<React.SetStateAction<boolean>>;
  searchBySerial: () => Promise<void>;
}

/**
 * Owns the progress page's serial-number search lifecycle:
 *  - resolves the session via the server-side lookup (exact serial/device-name match for
 *    roleless end users — the serial IS the authorization proof; members keep fuzzy search
 *    server-side; the tenant-wide list never reaches the browser)
 *  - exposes `setSession` so real-time refetch can replace the selected session
 *  - auto-collapses header on match, raises notFound on miss or error
 *  - runs one lookup for a `?serial=` deep link once the tenant is known, and mirrors every
 *    hit back into the URL as `?serial=<serial>` so reload, bookmark and a copied link work
 */
export function useProgressSearch({
  tenantId,
  getAccessToken,
  addNotification,
  onBeforeSearch,
}: UseProgressSearchParams): UseProgressSearchReturn {
  const [serialInput, setSerialInput] = useState("");
  const [session, setSession] = useState<Session | null>(null);
  const [searching, setSearching] = useState(false);
  const [searched, setSearched] = useState(false);
  const [notFound, setNotFound] = useState(false);
  const [headerCollapsed, setHeaderCollapsed] = useState(false);

  const runSearch = useCallback(async (rawTerm: string, source: "form" | "link") => {
    const term = rawTerm.trim();
    if (!term) return;

    trackEvent("progress_serial_submitted", { source });
    setSerialInput(term);
    setSearching(true);
    setSearched(true);
    setNotFound(false);
    setSession(null);
    onBeforeSearch?.();

    try {
      let data: ProgressLookupSessionResponse;
      try {
        data = await fetchJson<ProgressLookupSessionResponse>(api.progress.lookup(tenantId, term), getAccessToken);
      } catch (err) {
        if (!(err instanceof ApiError)) throw err;
        notifyApiError(addNotification, "Backend Error", err, "progress-search-error", "Search failed.");
        setNotFound(true);
        return;
      }
      const found: Session | null = data.found ? data.session ?? null : null;

      if (found) {
        setSession(found);
        setHeaderCollapsed(true);
        // The serial, not the typed term: a member's partial term or a device name would
        // not resolve for a roleless user the link is forwarded to.
        const search = withSerialParam(window.location.search, found.serialNumber?.trim() || term);
        if (search !== window.location.search) {
          window.history.replaceState(null, "", window.location.pathname + search + window.location.hash);
        }
      } else {
        setNotFound(true);
      }
    } catch (error) {
      console.error("Search failed:", error);
      addNotification(
        "error",
        "Backend Not Reachable",
        "Unable to search for device. Please check your connection.",
        "progress-search-error",
      );
      setNotFound(true);
    } finally {
      setSearching(false);
    }
  }, [tenantId, getAccessToken, addNotification, onBeforeSearch]);

  const searchBySerial = useCallback(() => runSearch(serialInput, "form"), [runSearch, serialInput]);

  // Deep link: one lookup per page load, once sign-in has resolved the tenant.
  const deepLinkHandled = useRef(false);
  useEffect(() => {
    if (!tenantId || deepLinkHandled.current) return;
    deepLinkHandled.current = true;
    const linked = readSerialParam(window.location.search);
    if (!linked) return;
    const run = async () => {
      await runSearch(linked, "link");
    };
    void run();
  }, [tenantId, runSearch]);

  return {
    serialInput,
    setSerialInput,
    session,
    setSession,
    searching,
    searched,
    notFound,
    headerCollapsed,
    setHeaderCollapsed,
    searchBySerial,
  };
}
