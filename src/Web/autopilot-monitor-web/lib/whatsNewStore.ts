"use client";

import { useSyncExternalStore } from "react";
import { parseJsonBody } from "@/lib/apiClient";
import {
  laterMark,
  NEVER_SEEN,
  parseWhatsNewPayload,
  WHATS_NEW_URL,
  type WhatsNewChannel,
  type WhatsNewPayload,
  type WhatsNewSeen,
} from "@/lib/whatsNew";

/**
 * Module-level store for What's new: the fetched payload, the panel's open state and the
 * marks written in this browser session. One store instead of a context because three
 * unrelated triggers open the same panel — the portal navbar, the landing navbar and the
 * Help page tile — and the panel itself is mounted once in the root layout.
 *
 * Snapshot is a single immutable object; the server snapshot is the initial state so the
 * badge only appears after hydration (no markup mismatch for the prerendered pages).
 */

export type WhatsNewLoadStatus = "idle" | "loading" | "ready" | "error";

export interface WhatsNewState {
  status: WhatsNewLoadStatus;
  payload: WhatsNewPayload | null;
  fetchedAt: number;
  /** Marks written in this session, layered over the server marks (optimistic). */
  sessionSeen: WhatsNewSeen;
  panelOpen: boolean;
  panelChannel: WhatsNewChannel;
  panelSource: string;
}

const INITIAL: WhatsNewState = {
  status: "idle",
  payload: null,
  fetchedAt: 0,
  sessionSeen: NEVER_SEEN,
  panelOpen: false,
  panelChannel: "platform",
  panelSource: "",
};

/** Re-fetch when the tab comes back after this long — a deploy may have shipped new entries. */
const STALE_AFTER_MS = 30 * 60 * 1000;

let state: WhatsNewState = INITIAL;
const listeners = new Set<() => void>();
let inflight: Promise<void> | null = null;
let visibilityHooked = false;

function setState(patch: Partial<WhatsNewState>): void {
  state = { ...state, ...patch };
  for (const listener of [...listeners]) listener();
}

function subscribe(onStoreChange: () => void): () => void {
  listeners.add(onStoreChange);
  return () => listeners.delete(onStoreChange);
}

export function useWhatsNewState(): WhatsNewState {
  return useSyncExternalStore(subscribe, () => state, () => INITIAL);
}

/** Loads the payload once (and again when stale); safe to call from every trigger's mount. */
export function ensureWhatsNewLoaded(): Promise<void> {
  if (typeof window === "undefined") return Promise.resolve();
  if (inflight) return inflight;

  if (!visibilityHooked) {
    visibilityHooked = true;
    document.addEventListener("visibilitychange", () => {
      if (document.visibilityState === "visible") void ensureWhatsNewLoaded();
    });
  }

  const fresh = state.status === "ready" && Date.now() - state.fetchedAt < STALE_AFTER_MS;
  if (fresh) return Promise.resolve();

  setState({ status: state.payload ? state.status : "loading" });
  inflight = (async () => {
    try {
      const response = await fetch(WHATS_NEW_URL, { cache: "no-store" });
      if (!response.ok) throw new Error(`whats-new.json ${response.status}`);
      const payload = parseWhatsNewPayload(await parseJsonBody<unknown>(response));
      if (!payload) throw new Error("whats-new.json: unexpected shape");
      setState({ status: "ready", payload, fetchedAt: Date.now() });
    } catch {
      // Keep a previously loaded payload; only a first load surfaces the error state.
      setState({ status: state.payload ? "ready" : "error", fetchedAt: Date.now() });
    } finally {
      inflight = null;
    }
  })();
  return inflight;
}

export function openWhatsNew(source: string, channel?: WhatsNewChannel): void {
  setState({ panelOpen: true, panelSource: source, ...(channel ? { panelChannel: channel } : {}) });
  void ensureWhatsNewLoaded();
}

export function closeWhatsNew(): void {
  setState({ panelOpen: false });
}

export function setWhatsNewChannel(channel: WhatsNewChannel): void {
  setState({ panelChannel: channel });
}

/**
 * Records a mark for this session so the badge updates immediately; the hook persists it
 * server-side. Monotonic: an older mark never wins.
 */
export function recordSessionSeen(channel: WhatsNewChannel, seenUtc: string): void {
  const next = laterMark(state.sessionSeen[channel], seenUtc);
  if (next === state.sessionSeen[channel]) return;
  setState({ sessionSeen: { ...state.sessionSeen, [channel]: next } });
}

/** Test seam. */
export function resetWhatsNewStoreForTests(): void {
  state = INITIAL;
  inflight = null;
}
