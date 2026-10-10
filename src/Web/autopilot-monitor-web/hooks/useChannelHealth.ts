"use client";

import { useEffect, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { fetchJson } from "@/lib/apiClient";
import { describeChannelHealth, type ChannelHealthView } from "@/lib/channelHealth";
import type { NotificationChannelHealthResponse } from "@/utils/wire-types.generated";

/**
 * Delivery status per saved channel id, read from `url` (null = caller may not read it, nothing
 * is fetched). Re-read whenever `savedChannels` or `lastTest` change identity — a save or a
 * finished test — because both can change a channel's status. A failed read yields no status:
 * the dot is guidance, the channel list works without it.
 */
export function useChannelHealth(
  url: string | null,
  savedChannels: unknown,
  lastTest: unknown,
): Map<string, ChannelHealthView> | null {
  const { getAccessToken } = useAuth();
  const [state, setState] = useState<{ url: string; byId: Map<string, ChannelHealthView> } | null>(null);

  useEffect(() => {
    if (!url) return;
    let cancelled = false;
    const run = async () => {
      try {
        const data = await fetchJson<NotificationChannelHealthResponse>(url, getAccessToken);
        if (cancelled) return;
        const nowMs = Date.now();
        setState({ url, byId: new Map(data.channels.map((c) => [c.channelId, describeChannelHealth(c, nowMs)])) });
      } catch {
        if (!cancelled) setState(null);
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [url, getAccessToken, savedChannels, lastTest]);

  return state && state.url === url ? state.byId : null;
}
