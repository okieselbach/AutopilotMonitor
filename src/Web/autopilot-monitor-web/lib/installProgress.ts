// Aggregation behind the Install Progress panel. Extracted from the component so the
// event-folding rules (state precedence, out-of-order delivery, source separation) are
// unit-testable without rendering.

export interface InstallEvent {
  timestamp: string;
  eventType?: string;
  data?: Record<string, unknown>;
}

// Minimal structural view of the event-data payload: only the fields this aggregation
// reads are declared (the agent serializes them as strings); everything else stays
// `unknown` via the index signature.
interface InstallEventData {
  [key: string]: unknown;
  appName?: string;
  app_name?: string;
  appId?: string;
  app_id?: string;
  displayName?: string;
  display_name?: string;
  packageId?: string;
  package_id?: string;
  exitCode?: string;
  exit_code?: string;
  lastExitCode?: string;
  last_exit_code?: string;
  hresultFromWin32?: string;
  hresult_from_win32?: string;
  failureType?: string;
  failure_type?: string;
  confidence?: string;
  errorDetail?: string;
  error_detail?: string;
  errorPatternId?: string;
  error_pattern_id?: string;
  errorCode?: string;
  error_code?: string;
}

// Where an install row was observed. `ime` is the default and stays unlabelled in the UI —
// only the non-default sources carry a pill, because they answer a question the plain app
// name cannot: this row is NOT Intune's view of the package.
//   * office-c2r  — Office Click-to-Run lifecycle from the agent's OfficeInstallDetector.
//                   Coexists with an IME row whenever the admin also deploys Microsoft 365
//                   Apps as their own Win32 app; the two measure different things.
//   * realmjoin   — RealmJoin package lifecycle (realmjoin_package_*), not an Intune app.
export type InstallSource = "ime" | "office-c2r" | "realmjoin";

export interface InstallItem {
  // Identity of the row: source + app name. Two sources may legitimately report the same
  // name (a Win32 app literally called "Microsoft 365 Apps" alongside the C2R lifecycle),
  // so the name alone must never key the row.
  key: string;
  source: InstallSource;
  appName: string;
  appId: string;
  state: "Installing" | "Installed" | "Failed" | "Postponed" | "Skipped" | "Preinstalled";
  startedAt?: string;
  completedAt?: string;
  durationMs?: number;
  isCompleted: boolean;
  isError: boolean;
  errorDetail?: string;
  errorPatternId?: string;
  exitCode?: string;
  hresultFromWin32?: string;
  // ESP-level HRESULT extracted from the failed subcategory's statusText (e.g.
  // 0x87D1041C). Only set on `app_install_failed` events produced by the V2
  // termination-handler promotion, where it carries the cross-app failure cause.
  // Distinct from hresultFromWin32 (per-app installer HRESULT).
  errorCode?: string;
  // c117946b debrief (2026-05-12) — when the agent promotes an app from Installing
  // to Error on terminal ESP-Apps-failure, it tags the event with `failureType`.
  // Session 080edee9 follow-up (2026-05-28) — three flavours:
  //   * esp_apps_timeout            → orange "Likely stuck" (no HRESULT observed)
  //   * esp_apps_detection_failure  → red "Detection failed" (HRESULT 0x87D1041C —
  //     install ran but Intune could not detect the app afterwards)
  //   * esp_apps_install_failure    → red "Install failed" (any other HRESULT —
  //     installer itself returned an error)
  failureType?: string;
  confidence?: string;
  isLikelyStuck: boolean;
  isDetectionFailure: boolean;
  isInstallFailure: boolean;
  firstSeenIndex: number;
  eventData?: Record<string, unknown>;
}

// Canonical V2 agent `failureType` identifiers — mirrors
// AutopilotMonitor.Shared.Constants.AppFailureTypes (Constants.cs). Stable strings —
// UI badges, summary buckets, and analyze rules all match on these.
const ESP_APPS_TIMEOUT = "esp_apps_timeout";
const ESP_APPS_DETECTION_FAILURE = "esp_apps_detection_failure";
const ESP_APPS_INSTALL_FAILURE = "esp_apps_install_failure";

const REALMJOIN_TYPES: ReadonlySet<string> = new Set([
  "realmjoin_package_started",
  "realmjoin_package_completed",
]);

const OFFICE_TYPES: ReadonlySet<string> = new Set([
  "office_install_started",
  "office_install_completed",
  "office_install_failed",
  "office_preinstalled_detected",
]);

function sourceOf(eventType: string | undefined): InstallSource {
  if (eventType && REALMJOIN_TYPES.has(eventType)) return "realmjoin";
  if (eventType && OFFICE_TYPES.has(eventType)) return "office-c2r";
  return "ime";
}

/**
 * Folds app/office/RealmJoin install events into one row per (source, app name),
 * ordered by first appearance. Expects the caller to have stripped historic-replay
 * events already (see lib/historicReplay).
 */
export function buildInstallItems(events: InstallEvent[]): InstallItem[] {
  if (events.length === 0) return [];

  const sortedEvents = [...events].sort(
    (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
  );

  const installMap = new Map<string, InstallItem>();
  let insertionIndex = 0;

  for (const evt of sortedEvents) {
    const d = evt.data as InstallEventData | undefined;
    if (!d) continue;

    const type = evt.eventType;
    const source = sourceOf(type);

    // RealmJoin packages carry packageId/displayName instead of appId/appName.
    const isRealmJoin = source === "realmjoin";
    const appName = isRealmJoin
      ? (d.displayName ?? d.display_name ?? d.packageId ?? d.package_id)
      : (d.appName ?? d.app_name ?? d.appId ?? d.app_id);
    if (!appName) continue;
    const appId = (isRealmJoin ? (d.packageId ?? d.package_id) : (d.appId ?? d.app_id)) ?? appName;

    // Source-scoped key: an IME app named exactly like the Office C2R lifecycle
    // ("Microsoft 365 Apps") must stay its own row instead of overwriting it.
    const key = `${source}|${appName}`;
    const existing = installMap.get(key);
    const eventTs = evt.timestamp;

    // The Office C2R lifecycle (office_install_*) is not an IME app but maps onto the same
    // started → completed/failed install flow, so it renders as a first-class install row
    // with the live timer + duration. Office has no postponed/skipped variants.
    // RealmJoin has no separate failed event — realmjoin_package_completed carries
    // success ("true"/"false") + lastExitCode, so success=false routes to the failed branch.
    const rjFailed = type === "realmjoin_package_completed" && String(d.success).toLowerCase() === "false";
    const isStarted = type === "app_install_started" || type === "office_install_started" || type === "realmjoin_package_started";
    const isCompleted = type === "app_install_completed" || type === "office_install_completed" || (type === "realmjoin_package_completed" && !rjFailed);
    const isFailed = type === "app_install_failed" || type === "office_install_failed" || rjFailed;

    const base = { key, source, appName, appId };

    if (isStarted) {
      // Don't reset an app that already completed — later batch re-scans
      // would overwrite the real duration with near-zero timestamps.
      // Allow restart after failure (retry).
      if (existing?.state === "Installed") {
        // Out-of-order delivery: completed arrived before started (e.g. Office already on disk when
        // C2R ran — CSP / Win32-wrapper install). Backfill the missing start time so the duration is
        // computed, without downgrading the completed state.
        if (!existing.startedAt) {
          existing.startedAt = eventTs;
          if (existing.completedAt) {
            existing.durationMs = Math.max(0, new Date(existing.completedAt).getTime() - new Date(eventTs).getTime());
          }
        }
        continue;
      }
      installMap.set(key, {
        ...base,
        state: "Installing",
        startedAt: eventTs,
        isCompleted: false,
        isError: false,
        isLikelyStuck: false,
        isDetectionFailure: false,
        isInstallFailure: false,
        firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
        eventData: d,
      });
    } else if (isCompleted) {
      // Keep the first valid completion — don't let batch re-scans overwrite.
      if (existing?.state === "Installed" && existing.durationMs != null) continue;
      const startTime = existing?.startedAt ? new Date(existing.startedAt).getTime() : null;
      const endTime = new Date(eventTs).getTime();
      const duration = startTime ? endTime - startTime : undefined;

      installMap.set(key, {
        ...base,
        state: "Installed",
        startedAt: existing?.startedAt,
        completedAt: eventTs,
        durationMs: duration,
        isCompleted: true,
        isError: false,
        isLikelyStuck: false,
        isDetectionFailure: false,
        isInstallFailure: false,
        exitCode: d.exitCode ?? d.exit_code ?? d.lastExitCode ?? d.last_exit_code,
        hresultFromWin32: d.hresultFromWin32 ?? d.hresult_from_win32,
        firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
        eventData: d,
      });
    } else if (isFailed) {
      // Don't downgrade from Installed to Failed.
      if (existing?.state === "Installed") continue;
      const startTime = existing?.startedAt ? new Date(existing.startedAt).getTime() : null;
      const endTime = new Date(eventTs).getTime();
      const duration = startTime ? endTime - startTime : undefined;

      const failureType = (d.failureType ?? d.failure_type) as string | undefined;
      const confidence = (d.confidence) as string | undefined;

      installMap.set(key, {
        ...base,
        state: "Failed",
        startedAt: existing?.startedAt,
        completedAt: eventTs,
        durationMs: duration,
        isCompleted: true,
        isError: true,
        isLikelyStuck: failureType === ESP_APPS_TIMEOUT,
        isDetectionFailure: failureType === ESP_APPS_DETECTION_FAILURE,
        isInstallFailure: failureType === ESP_APPS_INSTALL_FAILURE,
        failureType,
        confidence,
        errorDetail: d.errorDetail ?? d.error_detail,
        errorPatternId: d.errorPatternId ?? d.error_pattern_id,
        exitCode: d.exitCode ?? d.exit_code ?? d.lastExitCode ?? d.last_exit_code,
        hresultFromWin32: d.hresultFromWin32 ?? d.hresult_from_win32,
        // Session 080edee9 follow-up — ESP-level HRESULT carried on promoted
        // app_install_failed events from the V2 termination handler.
        errorCode: d.errorCode ?? d.error_code,
        firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
        eventData: d,
      });
    } else if (type === "office_preinstalled_detected") {
      // Office was already fully resident on disk at the first signal (OEM/consumer inbox Office
      // running a background CLIENTUPDATE) — informational, not an enrollment install or failure.
      // Don't overwrite a real terminal state if one somehow co-exists.
      if (existing?.state === "Installed" || existing?.state === "Failed") continue;
      installMap.set(key, {
        ...base,
        state: "Preinstalled",
        completedAt: eventTs,
        isCompleted: true,
        isError: false,
        isLikelyStuck: false,
        isDetectionFailure: false,
        isInstallFailure: false,
        firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
        eventData: d,
      });
    } else if (type === "app_install_postponed") {
      // Don't downgrade from Installed to Postponed.
      if (existing?.state === "Installed") continue;
      const startTime = existing?.startedAt ? new Date(existing.startedAt).getTime() : null;
      const endTime = new Date(eventTs).getTime();
      const duration = startTime ? endTime - startTime : undefined;

      installMap.set(key, {
        ...base,
        state: "Postponed",
        startedAt: existing?.startedAt,
        completedAt: eventTs,
        durationMs: duration,
        isCompleted: true,
        isError: false,
        isLikelyStuck: false,
        isDetectionFailure: false,
        isInstallFailure: false,
        firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
        eventData: d,
      });
    } else if (type === "app_install_skipped") {
      // Don't overwrite terminal states.
      if (existing?.state === "Installed" || existing?.state === "Failed" || existing?.state === "Postponed") continue;
      installMap.set(key, {
        ...base,
        state: "Skipped",
        isCompleted: true,
        isError: false,
        isLikelyStuck: false,
        isDetectionFailure: false,
        isInstallFailure: false,
        firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
        eventData: d,
      });
    }
  }

  return Array.from(installMap.values()).sort((a, b) => a.firstSeenIndex - b.firstSeenIndex);
}
