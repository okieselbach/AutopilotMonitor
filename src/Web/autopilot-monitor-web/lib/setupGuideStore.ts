"use client";

import { useSyncExternalStore } from "react";
import { trackEvent } from "@/lib/appInsights";
import { parseSetupTour, type SetupGuideLocalMarks, type SetupStepId, type SetupTour } from "@/lib/setupGuide";

/**
 * Shared state of the setup guide. A module store like feedbackDialogStore: the dashboard card,
 * the sidebar entry, the help menu and the settings tour all open the one dialog the root layout
 * mounts. The tour and the local marks live in sessionStorage, because the device-validation
 * consent leaves the portal and reloads it.
 */

export interface SetupGuideDialogState {
  open: boolean;
  /** The step to show; null opens at the first open step. */
  step: SetupStepId | null;
}

const CLOSED: SetupGuideDialogState = { open: false, step: null };
const TOUR_KEY = "am_setup_tour";
const MARKS_KEY = "am_setup_marks";
/** Longer than the backend's per-instance configuration cache, so the flags have caught up by then. */
const MARKS_TTL_MS = 15 * 60_000;

let dialog: SetupGuideDialogState = CLOSED;
/** Bumps on every local mark; hooks re-read the flags and the marks. */
let revision = 0;
/** undefined = not read from sessionStorage yet. */
let tour: SetupTour | null | undefined;
const listeners = new Set<() => void>();

function emit(): void {
  for (const listener of [...listeners]) listener();
}

function subscribe(onStoreChange: () => void): () => void {
  listeners.add(onStoreChange);
  return () => listeners.delete(onStoreChange);
}

// ── Dialog ──

export function useSetupGuideDialog(): SetupGuideDialogState {
  return useSyncExternalStore(subscribe, () => dialog, () => CLOSED);
}

export function openSetupGuide(source: string, step: SetupStepId | null = null): void {
  trackEvent("setup_guide_opened", { source, step: step ?? "next" });
  dialog = { open: true, step };
  emit();
}

export function closeSetupGuide(): void {
  if (!dialog.open) return;
  dialog = CLOSED;
  emit();
}

// ── Tour (survives the consent redirect) ──

function readTour(): SetupTour | null {
  if (tour !== undefined) return tour;
  if (typeof window === "undefined") return null;
  try {
    tour = parseSetupTour(window.sessionStorage.getItem(TOUR_KEY));
  } catch {
    tour = null;
  }
  return tour;
}

export function useSetupTour(): SetupTour | null {
  return useSyncExternalStore(subscribe, readTour, () => null);
}

export function startSetupTour(next: SetupTour): void {
  tour = next;
  try {
    window.sessionStorage.setItem(TOUR_KEY, next);
  } catch {
    // Without storage the tour still runs until the next reload.
  }
  emit();
}

export function endSetupTour(): void {
  tour = null;
  try {
    window.sessionStorage.removeItem(TOUR_KEY);
  } catch {
    // Nothing stored.
  }
  emit();
}

// ── Local marks ──

interface StoredMarks {
  tenantId: string;
  at: number;
  marks: SetupGuideLocalMarks;
}

export function readSetupMarks(tenantId: string): SetupGuideLocalMarks {
  try {
    const stored = JSON.parse(window.sessionStorage.getItem(MARKS_KEY) ?? "null") as StoredMarks | null;
    if (!stored || stored.tenantId !== tenantId || Date.now() - stored.at > MARKS_TTL_MS) return {};
    return stored.marks ?? {};
  } catch {
    return {};
  }
}

export function markSetup(tenantId: string, marks: SetupGuideLocalMarks): void {
  const next: StoredMarks = { tenantId, at: Date.now(), marks: { ...readSetupMarks(tenantId), ...marks } };
  try {
    window.sessionStorage.setItem(MARKS_KEY, JSON.stringify(next));
  } catch {
    // The flags catch up within minutes anyway.
  }
  revision += 1;
  emit();
}

export function useSetupRevision(): number {
  return useSyncExternalStore(subscribe, () => revision, () => 0);
}
