// One pass over a session's events for the phase timeline: per phase the first and last event
// time (phase durations) and the live-activity text, plus the WhiteGlove signals. The timeline
// renders eight phase nodes, and each one used to re-scan the whole list several times per render.

import type { EnrollmentEvent } from "@/types";

// Event types that are not useful as activity status display
const ACTIVITY_IGNORED_EVENT_TYPES: ReadonlySet<string> = new Set([
  "performance_snapshot",
  "system_info",
  "network_info",
]);

export interface PhaseEventSummary {
  /** Earliest event timestamp of the phase, ms since epoch. */
  firstMs: number;
  /** Latest event timestamp of the phase, ms since epoch. */
  lastMs: number;
  count: number;
  /** What the phase is doing, read from its latest events; null when nothing describes it. */
  currentActivity: string | null;
}

export interface EventPhaseSummary {
  byPhase: ReadonlyMap<number, PhaseEventSummary>;
  hasWhiteGloveComplete: boolean;
  hasWhiteGloveResumed: boolean;
}

// "Latest" is the highest sequence; on a tie the first one in list order wins — the same event
// the former sort-by-sequence-desc (stable) + find() picked.
interface PhaseAccumulator {
  firstMs: number;
  lastMs: number;
  count: number;
  /** Latest event that is not activity noise — the fallback message. */
  latest: EnrollmentEvent | null;
  trackingSummary: EnrollmentEvent | null;
  espState: EnrollmentEvent | null;
  /** app_download_started or app_install_started, whichever is later. */
  appInstall: EnrollmentEvent | null;
  download: EnrollmentEvent | null;
}

function later(current: EnrollmentEvent | null, candidate: EnrollmentEvent): EnrollmentEvent {
  return current === null || candidate.sequence > current.sequence ? candidate : current;
}

// Precedence is by kind, not by sequence: tracking summary, ESP UI state, app download/install
// start, download progress, then the latest short message.
function deriveActivity(p: PhaseAccumulator): string | null {
  if (p.latest === null) return null;

  // Check for app_tracking_summary events (new strategic events)
  if (p.trackingSummary?.data) {
    // Counters are serialized as strings on the wire.
    const d = p.trackingSummary.data as Record<string, string | undefined>;
    const completed = parseInt(d.completedApps ?? d.appsCompleted ?? "0", 10);
    const total = parseInt(d.totalApps ?? "0", 10);
    if (total > 0) {
      return `Installing apps (${completed}/${total})`;
    }
  }

  // Check for esp_ui_state events (legacy) to show app install progress
  if (p.espState?.data) {
    const d = p.espState.data as Record<string, string | undefined>;
    const completed = parseInt(d.blocking_apps_completed ?? d.blockingAppsCompleted ?? "0", 10);
    const total = parseInt(d.blocking_apps_total ?? d.blockingAppsTotal ?? "0", 10);
    const currentItem = d.current_item ?? d.currentItem ?? d.status_text ?? d.statusText;
    if (total > 0 && currentItem) {
      return `${currentItem} (${completed}/${total})`;
    }
    if (total > 0) {
      return `Installing apps (${completed}/${total})`;
    }
  }

  // Check for app install events (new strategic events)
  if (p.appInstall?.data) {
    const appName = p.appInstall.data.appName ?? p.appInstall.data.appId ?? "app";
    if (p.appInstall.eventType === "app_download_started") return `Downloading ${appName}`;
    return `Installing ${appName}`;
  }

  // Check for download_progress to show active download (legacy)
  if (p.download?.data) {
    const d = p.download.data as Record<string, string | undefined>;
    const appName = d.app_name ?? d.appName ?? "content";
    const pct = d.bytes_total && d.bytes_downloaded
      ? Math.round((parseInt(d.bytes_downloaded) / parseInt(d.bytes_total)) * 100)
      : null;
    if (pct !== null) return `Downloading ${appName} - ${pct}%`;
    return `Downloading ${appName}`;
  }

  // Fall back to latest event message
  if (p.latest.message && p.latest.message.length < 80) {
    return p.latest.message;
  }

  return null;
}

export function summarizeEventsByPhase(events: readonly EnrollmentEvent[]): EventPhaseSummary {
  const accumulators = new Map<number, PhaseAccumulator>();
  let hasWhiteGloveComplete = false;
  let hasWhiteGloveResumed = false;

  for (const e of events) {
    if (e.eventType === "whiteglove_complete") hasWhiteGloveComplete = true;
    else if (e.eventType === "whiteglove_resumed") hasWhiteGloveResumed = true;

    const t = new Date(e.timestamp).getTime();
    let p = accumulators.get(e.phase);
    if (p === undefined) {
      p = { firstMs: t, lastMs: t, count: 0, latest: null, trackingSummary: null, espState: null, appInstall: null, download: null };
      accumulators.set(e.phase, p);
    } else {
      if (t < p.firstMs) p.firstMs = t;
      if (t > p.lastMs) p.lastMs = t;
    }
    p.count++;

    if (ACTIVITY_IGNORED_EVENT_TYPES.has(e.eventType)) continue;
    p.latest = later(p.latest, e);
    switch (e.eventType) {
      case "app_tracking_summary": p.trackingSummary = later(p.trackingSummary, e); break;
      case "esp_ui_state": p.espState = later(p.espState, e); break;
      case "app_download_started":
      case "app_install_started": p.appInstall = later(p.appInstall, e); break;
      case "download_progress": p.download = later(p.download, e); break;
    }
  }

  const byPhase = new Map<number, PhaseEventSummary>();
  for (const [phase, p] of accumulators) {
    byPhase.set(phase, { firstMs: p.firstMs, lastMs: p.lastMs, count: p.count, currentActivity: deriveActivity(p) });
  }
  return { byPhase, hasWhiteGloveComplete, hasWhiteGloveResumed };
}
