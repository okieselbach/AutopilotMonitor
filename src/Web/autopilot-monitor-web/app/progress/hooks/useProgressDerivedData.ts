"use client";

import { useEffect, useMemo, useState } from "react";
import { EnrollmentEvent, Session } from "@/types";

// Minimal structural view of the event payloads this hook reads — only fields used in a
// typed position are declared (the agent serializes them as strings); everything else
// stays `unknown` via the index signature.
interface ProgressEventData {
  [key: string]: unknown;
  totalApps?: string;
  total_apps?: string;
  installed?: string;
  failed?: string;
  installing?: string;
  downloading?: string;
  blocking_apps_total?: string;
  blockingAppsTotal?: string;
  blocking_apps_completed?: string;
  blockingAppsCompleted?: string;
  app_name?: string;
  appName?: string;
  file_name?: string;
  fileName?: string;
  appId?: string;
  app_id?: string;
  status?: string;
}

export interface AppSummary {
  total: number;
  installed: number;
  failed: number;
  installing: number;
  downloading: number;
}

export interface CurrentDownload {
  appName: string | null;
  bytesDownloaded: number;
  bytesTotal: number;
  downloadRateBps: number;
  isComplete: boolean;
  completedCount: number;
  active: boolean;
}

export interface CurrentInstall {
  appName: string | null;
  startedAt: string | null;
  completedCount: number;
  failedCount: number;
  totalCount: number;
  active: boolean;
}

// Module-level pure helper: the loop with its Map mutations and early return keeps the
// React compiler from memoizing the body inline (preserve-manual-memoization).
function computeCurrentDownload(events: EnrollmentEvent[]): CurrentDownload | null {
  const downloadEvents = events.filter((e) => e.eventType === "download_progress");
  if (downloadEvents.length === 0) return null;

  const appLatest = new Map<
    string,
    { bytesDownloaded: number; bytesTotal: number; downloadRateBps: number; isComplete: boolean }
  >();
  for (const evt of downloadEvents) {
    const d = evt.data as ProgressEventData | undefined;
    if (!d) continue;
    const appName = d.app_name ?? d.appName ?? d.file_name ?? d.fileName ?? null;
    if (!appName) continue;
    const bytesDownloaded = Number(d.bytes_downloaded ?? d.bytesDownloaded ?? 0);
    const bytesTotal = Number(d.bytes_total ?? d.bytesTotal ?? 0);
    const downloadRateBps = Number(d.download_rate_bps ?? d.downloadRateBps ?? 0);
    const status = d.status ?? "";
    const isComplete =
      status === "completed" ||
      status === "failed" ||
      d.isCompleted === true ||
      d.is_completed === true;
    appLatest.set(appName, {
      bytesDownloaded: isNaN(bytesDownloaded) ? 0 : bytesDownloaded,
      bytesTotal: isNaN(bytesTotal) ? 0 : bytesTotal,
      downloadRateBps: isNaN(downloadRateBps) ? 0 : downloadRateBps,
      isComplete,
    });
  }

  const completedCount = Array.from(appLatest.values()).filter((v) => v.isComplete).length;

  for (let i = downloadEvents.length - 1; i >= 0; i--) {
    const d = downloadEvents[i].data as ProgressEventData | undefined;
    if (!d) continue;
    const appName = d.app_name ?? d.appName ?? d.file_name ?? d.fileName ?? null;
    if (!appName) continue;
    const latest = appLatest.get(appName);
    if (latest && !latest.isComplete) {
      return { appName, ...latest, completedCount, active: true };
    }
  }

  return {
    appName: null,
    bytesDownloaded: 0,
    bytesTotal: 0,
    downloadRateBps: 0,
    isComplete: true,
    completedCount,
    active: false,
  };
}

export interface UseProgressDerivedDataReturn {
  appSummary: AppSummary | null;
  currentDownload: CurrentDownload | null;
  currentInstall: CurrentInstall | null;
  installElapsedMs: number | null;
  overallProgress: number;
}

/**
 * Pure derivation of progress-page view state from events + session:
 *  - appSummary from app_tracking_summary (with esp_ui_state fallback)
 *  - currentDownload: latest-state-per-app + most recent active app
 *  - currentInstall: app-state map driven by app_install_* events
 *  - installElapsedMs: 1s live timer while an install is active
 *  - overallProgress: phase-based 0-100% gauge
 */
export function useProgressDerivedData(
  events: EnrollmentEvent[],
  session: Session | null,
): UseProgressDerivedDataReturn {
  const appSummary = useMemo<AppSummary | null>(() => {
    const summaryEvents = events.filter((e) => e.eventType === "app_tracking_summary");
    if (summaryEvents.length > 0) {
      const latest = summaryEvents[summaryEvents.length - 1];
      const d = latest.data as ProgressEventData | undefined;
      if (d) {
        const total = parseInt(d.totalApps ?? d.total_apps ?? "0", 10);
        if (total > 0) {
          // Flat V1 schema — see AppTrackingSummaryBuilder.cs for the contract.
          return {
            total,
            installed: parseInt(d.installed ?? "0", 10),
            failed: parseInt(d.failed ?? "0", 10),
            installing: parseInt(d.installing ?? "0", 10),
            downloading: parseInt(d.downloading ?? "0", 10),
          };
        }
      }
    }

    const espEvents = events.filter((e) => e.eventType === "esp_ui_state");
    if (espEvents.length > 0) {
      const latest = espEvents[espEvents.length - 1];
      const d = latest.data as ProgressEventData | undefined;
      if (d) {
        const total = parseInt(d.blocking_apps_total ?? d.blockingAppsTotal ?? "0", 10);
        const installed = parseInt(d.blocking_apps_completed ?? d.blockingAppsCompleted ?? "0", 10);
        if (total > 0) {
          return { total, installed, failed: 0, installing: 0, downloading: 0 };
        }
      }
    }

    return null;
  }, [events]);

  const currentDownload = useMemo<CurrentDownload | null>(
    () => computeCurrentDownload(events),
    [events]
  );

  const currentInstall = useMemo<CurrentInstall | null>(() => {
    const installTypes = new Set([
      "app_install_started",
      "app_install_completed",
      "app_install_failed",
      "app_install_postponed",
      "app_install_skipped",
    ]);
    const installEvents = events.filter((e) => installTypes.has(e.eventType));
    if (installEvents.length === 0) return null;

    const sorted = [...installEvents].sort(
      (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime(),
    );

    const appState = new Map<string, { state: string; startedAt?: string }>();
    for (const evt of sorted) {
      const d = evt.data as ProgressEventData | undefined;
      if (!d) continue;
      const appName = d.appName ?? d.app_name ?? d.appId ?? d.app_id ?? null;
      if (!appName) continue;
      const existing = appState.get(appName);

      if (evt.eventType === "app_install_started") {
        if (existing?.state === "Installed") continue;
        appState.set(appName, { state: "Installing", startedAt: evt.timestamp });
      } else if (evt.eventType === "app_install_completed") {
        if (existing?.state === "Installed") continue;
        appState.set(appName, { state: "Installed" });
      } else if (evt.eventType === "app_install_failed") {
        if (existing?.state === "Installed") continue;
        appState.set(appName, { state: "Failed" });
      } else if (evt.eventType === "app_install_postponed") {
        if (existing?.state === "Installed") continue;
        appState.set(appName, { state: "Postponed" });
      } else if (evt.eventType === "app_install_skipped") {
        if (
          existing?.state === "Installed" ||
          existing?.state === "Failed" ||
          existing?.state === "Postponed"
        )
          continue;
        appState.set(appName, { state: "Skipped" });
      }
    }

    const entries = Array.from(appState.entries());
    const completedCount = entries.filter(([, v]) => v.state === "Installed").length;
    const failedCount = entries.filter(([, v]) => v.state === "Failed").length;
    const totalCount = entries.filter(([, v]) => v.state !== "Skipped").length;

    let activeApp: string | null = null;
    let activeStartedAt: string | null = null;
    for (const [name, v] of entries) {
      if (v.state === "Installing") {
        activeApp = name;
        activeStartedAt = v.startedAt ?? null;
      }
    }

    return {
      appName: activeApp,
      startedAt: activeStartedAt,
      completedCount,
      failedCount,
      totalCount,
      active: activeApp !== null,
    };
  }, [events]);

  const [installElapsedMs, setInstallElapsedMs] = useState<number | null>(null);
  useEffect(() => {
    if (!currentInstall?.active || !currentInstall?.startedAt) {
      setInstallElapsedMs(null);
      return;
    }
    const startTime = new Date(currentInstall.startedAt).getTime();
    const tick = () => setInstallElapsedMs(Date.now() - startTime);
    tick();
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  }, [currentInstall?.active, currentInstall?.startedAt]);

  const overallProgress = session
    ? session.status === "Succeeded"
      ? 100
      : session.status === "Failed"
      ? Math.min(
          100,
          ((session.currentPhase === 99 ? 3 : session.currentPhase) / 6) * 100,
        )
      : Math.min(100, (session.currentPhase / 6) * 100)
    : 0;

  return {
    appSummary,
    currentDownload,
    currentInstall,
    installElapsedMs,
    overallProgress,
  };
}
