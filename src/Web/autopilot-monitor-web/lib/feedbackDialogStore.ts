"use client";

import { useSyncExternalStore } from "react";
import { trackEvent } from "@/lib/appInsights";

/**
 * Open state of the "Send feedback" dialog. A module store, like whatsNewStore, because
 * unrelated triggers open the one dialog the root layout mounts: the help menu (desktop and
 * mobile), the dashboard banner and the Submit Logs page for roles that cannot submit files.
 */
export interface FeedbackDialogState {
  open: boolean;
  /** Which trigger opened it; travels with the telemetry. */
  source: string;
}

const INITIAL: FeedbackDialogState = { open: false, source: "" };

let state: FeedbackDialogState = INITIAL;
const listeners = new Set<() => void>();

function setState(next: FeedbackDialogState): void {
  state = next;
  for (const listener of [...listeners]) listener();
}

function subscribe(onStoreChange: () => void): () => void {
  listeners.add(onStoreChange);
  return () => listeners.delete(onStoreChange);
}

export function useFeedbackDialogState(): FeedbackDialogState {
  return useSyncExternalStore(subscribe, () => state, () => INITIAL);
}

export function openFeedbackDialog(source: string): void {
  trackEvent("feedback_dialog_opened", { source });
  setState({ open: true, source });
}

export function closeFeedbackDialog(): void {
  setState({ ...state, open: false });
}
