"use client";

import { useCallback, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { trackEvent } from "@/lib/appInsights";
import { Session } from "@/types";
import type { NotificationType } from "@/contexts/NotificationContext";

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

  const searchBySerial = useCallback(async () => {
    if (!serialInput.trim()) return;

    trackEvent("progress_serial_submitted");
    setSearching(true);
    setSearched(true);
    setNotFound(false);
    setSession(null);
    onBeforeSearch?.();

    try {
      const response = await authenticatedFetch(
        api.progress.lookup(tenantId, serialInput.trim()),
        getAccessToken,
      );

      if (response.ok) {
        const data = await response.json();
        const found: Session | null = data.found ? data.session : null;

        if (found) {
          setSession(found);
          setHeaderCollapsed(true);
        } else {
          setNotFound(true);
        }
      } else {
        addNotification(
          "error",
          "Backend Error",
          `Search failed: ${response.statusText}`,
          "progress-search-error",
        );
        setNotFound(true);
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification(
          "error",
          "Session Expired",
          error.message,
          "session-expired-error",
        );
      } else {
        console.error("Search failed:", error);
        addNotification(
          "error",
          "Backend Not Reachable",
          "Unable to search for device. Please check your connection.",
          "progress-search-error",
        );
      }
      setNotFound(true);
    } finally {
      setSearching(false);
    }
  }, [serialInput, tenantId, getAccessToken, addNotification, onBeforeSearch]);

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
