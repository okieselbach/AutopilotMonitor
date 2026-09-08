"use client";

import { useCallback, useEffect, useMemo } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { fetchOk, jsonBody } from "@/lib/apiClient";
import { trackEvent } from "@/lib/appInsights";
import {
  countUnseen,
  laterMark,
  nextSeenMark,
  WHATS_NEW_CHANNELS,
  type WhatsNewChannel,
  type WhatsNewEntry,
  type WhatsNewSeen,
} from "@/lib/whatsNew";
import {
  closeWhatsNew,
  ensureWhatsNewLoaded,
  openWhatsNew,
  recordSessionSeen,
  setWhatsNewChannel,
  useWhatsNewState,
  type WhatsNewLoadStatus,
} from "@/lib/whatsNewStore";
import type { WhatsNewSeenRequest } from "@/utils/wire-types.generated";

export interface UseWhatsNew {
  status: WhatsNewLoadStatus;
  entries: (channel: WhatsNewChannel) => WhatsNewEntry[];
  docsUrl: (channel: WhatsNewChannel) => string | null;
  /**
   * Whether unseen entries are counted at all — only for signed-in users, whose mark lives
   * on the server. Anonymous visitors get no counter and no "New" labels.
   */
  tracksUnseen: boolean;
  /** Effective seen mark per channel: server mark plus this session's writes; NEVER_SEEN when not tracking. */
  seen: WhatsNewSeen;
  unseen: Record<WhatsNewChannel, number>;
  totalUnseen: number;
  panelOpen: boolean;
  panelChannel: WhatsNewChannel;
  open: (source: string, channel?: WhatsNewChannel) => void;
  close: () => void;
  setChannel: (channel: WhatsNewChannel) => void;
  /** Marks a channel as viewed up to the newest loaded entry (no-op when not tracking). */
  markSeen: (channel: WhatsNewChannel) => Promise<void>;
}

/**
 * The one hook behind every What's new trigger and the panel. Mounting it loads the payload
 * (once, shared); signed-in users read their mark from auth/me and write it through the
 * backend, so the counter resets across browsers.
 */
export function useWhatsNew(): UseWhatsNew {
  const { isAuthenticated, user, getAccessToken } = useAuth();
  const s = useWhatsNewState();

  useEffect(() => {
    void ensureWhatsNewLoaded();
  }, []);

  const tracksUnseen = isAuthenticated && user !== null;
  const serverPlatform = tracksUnseen ? user.whatsNewSeenPlatformUtc ?? null : null;
  const serverAgent = tracksUnseen ? user.whatsNewSeenAgentUtc ?? null : null;
  const { sessionSeen } = s;
  const seen = useMemo<WhatsNewSeen>(
    () => ({
      platform: laterMark(serverPlatform, sessionSeen.platform),
      agent: laterMark(serverAgent, sessionSeen.agent),
    }),
    [serverPlatform, serverAgent, sessionSeen],
  );

  const entries = useCallback(
    (channel: WhatsNewChannel): WhatsNewEntry[] => s.payload?.channels[channel].entries ?? [],
    [s.payload],
  );

  const unseen = {
    platform: tracksUnseen ? countUnseen(entries("platform"), seen.platform) : 0,
    agent: tracksUnseen ? countUnseen(entries("agent"), seen.agent) : 0,
  };

  const markSeen = useCallback(
    async (channel: WhatsNewChannel) => {
      if (!tracksUnseen) return;
      const mark = nextSeenMark(entries(channel));
      if (mark === null) return;
      if (laterMark(seen[channel], mark) === seen[channel]) return;
      recordSessionSeen(channel, mark);
      try {
        await fetchOk(api.auth.whatsNewSeen(), getAccessToken, {
          method: "PUT",
          body: jsonBody<WhatsNewSeenRequest>({ channel, seenUtc: mark }),
        });
      } catch {
        // Best effort: the session layer already hides the badge; the next visit retries.
      }
    },
    [entries, seen, tracksUnseen, getAccessToken],
  );

  const open = useCallback(
    (source: string, channel?: WhatsNewChannel) => {
      trackEvent("whats_new_opened", {
        source,
        authenticated: isAuthenticated,
        unseenPlatform: unseen.platform,
        unseenAgent: unseen.agent,
      });
      openWhatsNew(source, channel);
    },
    [isAuthenticated, unseen.platform, unseen.agent],
  );

  return {
    status: s.status,
    entries,
    docsUrl: channel => s.payload?.channels[channel].docsUrl ?? null,
    tracksUnseen,
    seen,
    unseen,
    totalUnseen: WHATS_NEW_CHANNELS.reduce((sum, c) => sum + unseen[c], 0),
    panelOpen: s.panelOpen,
    panelChannel: s.panelChannel,
    open,
    close: closeWhatsNew,
    setChannel: setWhatsNewChannel,
    markSeen,
  };
}
