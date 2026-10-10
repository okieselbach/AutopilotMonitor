import type { EnrollmentEvent } from "@/types";
import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";
import { isTerminalStatus } from "@/lib/sessionStatus";
import { isParkedAfterTechnicianPart } from "@/lib/preProvisioning";

/**
 * The OOBE quality update while the enrollment still runs — the live counterpart of the time
 * attribution's `os_update` / `awaiting_sign_in` segments, which exist only once a session ended.
 *
 * `computeOsUpdateInterval` is a port of the backend `TimeAttributionCalculator.BuildOsUpdates`
 * for one observation window (with `FindUpdateBegin`, `ResolveOutcome`, `FindRebootGaps` and
 * `BuildAnchors`): begin, end, restarts, outcome and KBs are what the time attribution would
 * report if the session ended now. `tests/fixtures/os-update-live/cases.json` pins both sides;
 * a change to the calculator goes into that file first.
 *
 * The interval cannot say whether the update still runs — its end is the latest evidence so far,
 * known only in hindsight. `deriveOsUpdateLive` therefore labels it from positive evidence that
 * the update finished: no update page open, no restart pending, no package mid-servicing.
 */

const V = SHARED_MANIFEST.oobeUpdate;
const PHASE = SHARED_MANIFEST.enrollmentPhases;

type EventTypeName = (typeof SHARED_MANIFEST.eventTypes)[number];

/** How an OOBE quality update ended (backend `OsUpdateOutcomes`). */
export type OsUpdateOutcome = (typeof SHARED_MANIFEST.osUpdateOutcomes)[number];

const EVENT = {
  updatePage: "oobe_update_page",
  servicing: "windows_update_servicing",
  rebootDetected: "system_reboot_detected",
  clockChanged: "system_clock_changed",
  espExiting: "esp_exiting",
  desktopArrived: "desktop_arrived",
  helloWizardStarted: "hello_wizard_started",
} as const satisfies Record<string, EventTypeName>;

const OUTCOME = {
  installed: "installed",
  failed: "failed",
  skipped: "skipped",
  unknown: "unknown",
} as const satisfies Record<string, OsUpdateOutcome>;

/** The event fields the derivation reads. */
export type LiveEvent = Pick<EnrollmentEvent, "eventType" | "timestamp" | "sequence" | "phase" | "data" | "source">;

/**
 * Sources that backfill rows with Windows event-log timestamps (backend
 * `Constants.EventSources.SessionAnchorIneligible`): never "the device reported now" — a sleep
 * episode read back from the event log can even carry a time hours ahead of the enrollment.
 */
const BACKFILLING_SOURCES: ReadonlySet<string> = new Set(SHARED_MANIFEST.sessionAnchorIneligibleSources);

export interface OsUpdateInterval {
  startMs: number;
  /** The latest update evidence before the bound: page record, servicing step or restart end. */
  endMs: number;
  /** Where the update's time stops: the user is back, attribution ended (Failed) or the window end; null while open. */
  boundMs: number | null;
  /** Packages that reached "Installed" in the interval. */
  kbs: string[];
  /** Packages serviced in the interval without reaching "Installed". */
  notInstalledKbs: string[];
  outcome: OsUpdateOutcome;
  rebootCount: number;
}

// ── parsing ──────────────────────────────────────────────────────────────────

/** UTC instant of an ISO timestamp; one without a zone is UTC, as the backend reads it (JS would read local time). */
export function parseUtcMs(raw: unknown): number | null {
  if (typeof raw !== "string" || raw.length === 0) return null;
  const zoned = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(raw) ? raw : `${raw}Z`;
  const ms = Date.parse(zoned);
  return Number.isNaN(ms) ? null : ms;
}

function dataString(data: Record<string, unknown> | null | undefined, key: string): string | null {
  if (!data || !Object.prototype.hasOwnProperty.call(data, key)) return null;
  const value = data[key];
  return value == null ? null : String(value);
}

function sameText(a: string | null, b: string): boolean {
  return a !== null && a.toLowerCase() === b.toLowerCase();
}

function hasMarker(name: string | null, markers: readonly string[]): boolean {
  if (name === null) return false;
  const lower = name.toLowerCase();
  return markers.some(marker => lower.includes(marker.toLowerCase()));
}

interface Parsed {
  at: number;
  eventType: string;
  phase: number;
  data: Record<string, unknown> | null | undefined;
}

interface PageRecord {
  at: number;
  cxhEvent: string;
  page: string | null;
  name: string | null;
  result: string | null;
}

interface ServicingStep {
  at: number;
  pkg: string | null;
  step: string | null;
  targetState: string | null;
}

const isStartOf = (p: PageRecord, page: string) => p.cxhEvent === V.cxhEvents.pageStarted && sameText(p.page, page);
const isStopOf = (p: PageRecord, page: string) => p.cxhEvent === V.cxhEvents.pageStopped && sameText(p.page, page);
const reachedInstalled = (s: ServicingStep) =>
  sameText(s.step, V.servicingSteps.stateReached) && sameText(s.targetState, V.installedState);
const servicingFailed = (s: ServicingStep) => sameText(s.step, V.servicingSteps.failed);

/** A page name only an update in progress writes (backend `IsUpdateActivityName`). */
export function isUpdateActivityName(name: string | null): boolean {
  return hasMarker(name, V.activityMarkers);
}

// "Package_for_KB5129195~31bf…": the KB follows an underscore, so no \b before it.
const KB_PATTERN = /(?<![A-Za-z0-9])KB(\d{6,8})(?![0-9])/i;

function extractKb(pkg: string | null): string | null {
  if (!pkg) return null;
  const match = KB_PATTERN.exec(pkg);
  return match ? `KB${match[1]}` : null;
}

// ── anchors and restarts (backend BuildAnchors / FindRebootGaps) ─────────────

type Bucket = "prep" | "apps" | "identity" | "user" | "desktop" | null;

function bucketOf(phase: number): Bucket {
  switch (phase) {
    case PHASE.Start:
    case PHASE.DevicePreparation:
    case PHASE.DeviceSetup:
      return "prep";
    case PHASE.AppsDevice:
      return "apps";
    case PHASE.AccountSetup:
    case PHASE.FinalizingSetup:
      return "identity";
    case PHASE.AppsUser:
      return "user";
    case PHASE.Complete:
      return "desktop";
    default:
      return null; // Failed (and anything unmapped) ends attribution
  }
}

function buildAnchors(events: readonly Parsed[]): { ts: number; bucket: Bucket }[] {
  const anchors: { ts: number; bucket: Bucket }[] = [];
  for (const e of events) {
    let bucket: Bucket;
    if (e.eventType === EVENT.desktopArrived) bucket = "desktop";
    else if (e.phase !== PHASE.Unknown) bucket = bucketOf(e.phase);
    else continue;

    const prev = anchors[anchors.length - 1];
    if (prev) {
      if (e.at < prev.ts) continue;         // went backward: dropped
      if (prev.bucket === bucket) continue; // same-phase re-declaration
    }
    anchors.push({ ts: e.at, bucket });
  }
  return anchors;
}

/** One gap per observed boot: the last event before `lastBootUtc` → the first at/after it. */
function findRebootGaps(events: readonly Parsed[]): { start: number; end: number }[] {
  const result: { start: number; end: number }[] = [];
  const seenBoots = new Set<number>();
  for (let i = 0; i < events.length; i++) {
    const evt = events[i];
    if (evt.eventType !== EVENT.rebootDetected) continue;

    const lastBoot = parseUtcMs(dataString(evt.data, "lastBootUtc"));
    let gapStart: number | null = null;
    let gapEnd: number | null = null;
    if (lastBoot !== null) {
      if (seenBoots.has(lastBoot)) continue; // duplicate detection of the same boot
      seenBoots.add(lastBoot);
      for (const e of events) {
        if (e.eventType === EVENT.clockChanged) continue;
        if (e.at < lastBoot) {
          if (gapStart === null || e.at > gapStart) gapStart = e.at;
        } else if (gapEnd === null || e.at < gapEnd) {
          gapEnd = e.at;
        }
      }
    } else if (i > 0) {
      gapStart = events[i - 1].at;
      gapEnd = evt.at;
    }

    if (gapStart === null || gapEnd === null || gapEnd <= gapStart) continue;
    result.push({ start: gapStart, end: gapEnd });
  }
  return result;
}

// ── the update interval (backend BuildOsUpdates for one window) ──────────────

interface Prepared {
  pages: PageRecord[];
  servicing: ServicingStep[];
  rebootGaps: { start: number; end: number }[];
  userBack: number[];
  attributionEnd: number | null;
  espExits: number[];
}

function prepare(events: readonly LiveEvent[]): Prepared {
  // Canonical order: sequence (the backend sorts by Sequence; Array.prototype.sort is stable).
  const ordered: Parsed[] = [...events]
    .sort((a, b) => a.sequence - b.sequence)
    .flatMap(e => {
      const at = parseUtcMs(e.timestamp);
      return at === null ? [] : [{ at, eventType: e.eventType, phase: typeof e.phase === "number" ? e.phase : PHASE.Unknown, data: e.data }];
    });

  const anchors = buildAnchors(ordered);
  const pages: PageRecord[] = [];
  const servicing: ServicingStep[] = [];
  for (const e of ordered) {
    if (e.eventType === EVENT.updatePage && e.data) {
      pages.push({
        at: e.at,
        cxhEvent: dataString(e.data, "cxhEvent") ?? "",
        page: dataString(e.data, "page"),
        name: dataString(e.data, "name"),
        result: dataString(e.data, "result"),
      });
    } else if (e.eventType === EVENT.servicing) {
      servicing.push({
        at: e.at,
        pkg: dataString(e.data, "package"),
        step: dataString(e.data, "step"),
        targetState: dataString(e.data, "targetState"),
      });
    }
  }
  pages.sort((a, b) => a.at - b.at);
  servicing.sort((a, b) => a.at - b.at);

  return {
    pages,
    servicing,
    rebootGaps: findRebootGaps(ordered),
    userBack: [
      ...anchors.filter(a => a.bucket === "user" || a.bucket === "desktop").map(a => a.ts),
      ...ordered.filter(e => e.eventType === EVENT.helloWizardStarted).map(e => e.at),
    ].sort((a, b) => a - b),
    attributionEnd: anchors.find(a => a.bucket === null)?.ts ?? null,
    espExits: ordered.filter(e => e.eventType === EVENT.espExiting).map(e => e.at).sort((a, b) => a - b),
  };
}

function findUpdateBegin(window: { startMs: number; endMs: number }, p: Prepared, evidence: readonly number[]): number | null {
  const { pages, servicing, espExits, userBack, attributionEnd } = p;
  // An update after the user is back runs beside the enrollment, not instead of it.
  const userBackBy = (t: number) => userBack.some(u => u > window.startMs && u <= t);
  const inWindow = (t: number) => t >= window.startMs && t < window.endMs;
  const espExitBefore = (before: number): number | null => {
    for (const exit of espExits) {
      if (exit < window.startMs) continue;
      if (exit >= window.endMs || exit >= before) break;
      return exit;
    }
    return null;
  };

  let firstPageStart: number | null = null;
  for (const page of pages) {
    if (!isStartOf(page, V.updatePage) || !inWindow(page.at)) continue;
    firstPageStart = page.at;
    break;
  }

  // Update names before the first observed page start: the agent missed the start of their visit.
  let firstOrphanName: number | null = null;
  for (const page of pages) {
    if (firstPageStart !== null && page.at >= firstPageStart) break;
    if (!inWindow(page.at) || !isUpdateActivityName(page.name) || userBackBy(page.at)) continue;
    firstOrphanName = page.at;
    break;
  }
  if (firstOrphanName !== null) {
    const firstName = firstOrphanName;
    let firstStep: number | null = null;
    const exitBefore = espExitBefore(firstName);
    if (exitBefore !== null) {
      for (const step of servicing) {
        if (step.at < exitBefore || step.at < window.startMs) continue;
        if (step.at < firstName) firstStep = step.at;
        break;
      }
    } else {
      // The earliest step of a package the update also serviced at or after its first name.
      let limit = window.endMs;
      if (attributionEnd !== null && attributionEnd < limit) limit = attributionEnd;
      for (const u of userBack) {
        if (u <= firstName) continue;
        if (u < limit) limit = u;
        break;
      }
      const members = new Set(
        servicing.filter(s => s.at >= firstName && s.at < limit && s.pkg).map(s => s.pkg!.toLowerCase()),
      );
      for (const step of servicing) {
        if (step.at < window.startMs) continue;
        if (step.at >= firstName) break;
        if (!step.pkg || !members.has(step.pkg.toLowerCase())) continue;
        firstStep = step.at;
        break;
      }
    }
    return firstStep !== null && firstStep < firstName ? firstStep : firstName;
  }

  let pageStartSeen = false;
  for (let i = 0; i < pages.length; i++) {
    const start = pages[i];
    if (!isStartOf(start, V.updatePage) || start.at < window.startMs || start.at >= window.endMs) continue;
    pageStartSeen = true;
    if (userBackBy(start.at)) continue;

    // The visit ends with its stop, the restart page or the next visit.
    let visitEnd = window.endMs;
    for (let j = i + 1; j < pages.length; j++) {
      const next = pages[j];
      if (isStopOf(next, V.updatePage) || isStartOf(next, V.updateRestartPage) || isStartOf(next, V.updatePage)) {
        visitEnd = next.at;
        break;
      }
    }
    for (const t of userBack) {
      if (t <= start.at) continue;
      if (t < visitEnd) visitEnd = t;
      break;
    }

    if (evidence.some(t => t >= start.at && t <= visitEnd)) return start.at;
  }

  // A page that was seen and did not update is the answer; a servicing step beside it is some other update.
  if (pageStartSeen) return null;

  // Neither a page start nor an update name: the first servicing step after this window's device-ESP exit.
  let deviceEspExit: number | null = null;
  for (const exit of espExits) {
    if (exit < window.startMs) continue;
    if (exit >= window.endMs || userBackBy(exit)) break;
    deviceEspExit = exit;
    break;
  }
  if (deviceEspExit === null) return null;
  for (const step of servicing) {
    if (step.at < deviceEspExit || step.at < window.startMs || step.at >= window.endMs) continue;
    return userBackBy(step.at) ? null : step.at;
  }
  return null;
}

/** The latest outcome evidence in [begin, bound) decides (backend `ResolveOutcome`). */
function resolveOutcome(pages: readonly PageRecord[], servicing: readonly ServicingStep[], begin: number, bound: number): OsUpdateOutcome {
  let latest: number | null = null;
  let outcome: OsUpdateOutcome = OUTCOME.unknown;
  const consider = (at: number, kind: OsUpdateOutcome) => {
    if (latest !== null && at < latest) return;
    latest = at;
    outcome = kind;
  };

  for (const page of pages) {
    if (page.at < begin || page.at >= bound) continue;
    if (hasMarker(page.name, V.succeededMarkers)) consider(page.at, OUTCOME.installed);
    else if (hasMarker(page.name, V.failedMarkers)) consider(page.at, OUTCOME.failed);
    else if (hasMarker(page.name, V.skippedMarkers)) consider(page.at, OUTCOME.skipped);
    else if (isStopOf(page, V.updatePage) && sameText(page.result, V.pageResultFail)) consider(page.at, OUTCOME.failed);
  }
  for (const step of servicing) {
    if (step.at < begin || step.at >= bound) continue;
    if (reachedInstalled(step)) consider(step.at, OUTCOME.installed);
    else if (servicingFailed(step)) consider(step.at, OUTCOME.failed);
  }
  return outcome;
}

function intervalFrom(p: Prepared, window: { startMs: number; endMs: number }): OsUpdateInterval | null {
  const { pages, servicing, rebootGaps, userBack, attributionEnd } = p;
  if (servicing.length === 0 && !pages.some(page => isUpdateActivityName(page.name))) return null;

  const evidence = [
    ...servicing.map(s => s.at),
    ...pages.filter(page => isUpdateActivityName(page.name)).map(page => page.at),
  ].sort((a, b) => a - b);

  const begin = findUpdateBegin(window, p, evidence);
  if (begin === null) return null;

  let bound = window.endMs;
  if (attributionEnd !== null && attributionEnd < bound) bound = attributionEnd;
  for (const t of userBack) {
    if (t <= begin) continue;
    if (t < bound) bound = t;
    break;
  }
  if (bound <= begin) return null;

  let end = begin;
  for (const page of pages) {
    if (page.at > end && page.at < bound) end = page.at;
  }
  const servicedKbs: string[] = [];
  const installedKbs = new Set<string>();
  for (const step of servicing) {
    if (step.at < begin || step.at >= bound) continue;
    if (step.at > end) end = step.at;
    const kb = extractKb(step.pkg);
    if (kb === null) continue;
    if (!servicedKbs.includes(kb)) servicedKbs.push(kb);
    if (reachedInstalled(step)) installedKbs.add(kb);
  }
  const outcome = resolveOutcome(pages, servicing, begin, bound);
  let rebootCount = 0;
  for (const gap of rebootGaps) {
    if (gap.start < begin || gap.start >= bound) continue;
    rebootCount++;
    const restartEnd = gap.end < bound ? gap.end : bound;
    if (restartEnd > end) end = restartEnd;
  }

  return {
    startMs: begin,
    endMs: end,
    boundMs: Number.isFinite(bound) ? bound : null,
    kbs: servicedKbs.filter(kb => installedKbs.has(kb)),
    notInstalledKbs: servicedKbs.filter(kb => !installedKbs.has(kb)),
    outcome,
    rebootCount,
  };
}

/**
 * The OOBE update in one observation window — what the time attribution reports for the window
 * (backend parity, `tests/fixtures/os-update-live/cases.json`). Null without an update.
 */
export function computeOsUpdateInterval(events: readonly LiveEvent[], window: { startMs: number; endMs: number }): OsUpdateInterval | null {
  return intervalFrom(prepare(events), window);
}

// ── the live label ───────────────────────────────────────────────────────────

/** How long "waiting for sign-in" holds back after the update's last evidence: the confirmation visit right after "Installed" must not flicker. */
export const AWAITING_SIGN_IN_SETTLE_MS = 2 * 60 * 1000;

export type OsUpdateLiveState = "updating" | "awaiting_sign_in" | "ended";

export interface OsUpdateLiveFacts {
  /** False while anything shows the update still working: a page visit, a pending restart, a package mid-servicing. */
  concluded: boolean;
  /** The update page asked for its restart and the device has not restarted yet. */
  restarting: boolean;
  interval: OsUpdateInterval;
  /** The newest activity event — the last time the device reported; null without any. */
  lastReportMs: number | null;
}

export interface LiveSession {
  status?: string | null;
  startedAt?: string | null;
  resumedAt?: string | null;
  isPreProvisioned?: boolean | null;
}

/**
 * The update of a running session, or null: terminal status, a pre-provisioned device parked
 * for its user, no update evidence, or the user is back (the update's time is over). The window
 * is the current part — from the WhiteGlove resume, else from the session start, so the update
 * page rows the agent reads back from before it started never begin it earlier than the
 * attribution will.
 */
export function deriveOsUpdateLive(events: readonly LiveEvent[], session: LiveSession): OsUpdateLiveFacts | null {
  if (isTerminalStatus(session.status)) return null;
  if (isParkedAfterTechnicianPart(session, events)) return null;
  const windowStart = parseUtcMs(session.resumedAt) ?? parseUtcMs(session.startedAt);
  if (windowStart === null) return null;

  const p = prepare(events);
  const interval = intervalFrom(p, { startMs: windowStart, endMs: Number.POSITIVE_INFINITY });
  if (interval === null || interval.boundMs !== null) return null;

  const begin = interval.startMs;
  const pages = p.pages.filter(page => page.at >= begin);
  const servicing = p.servicing.filter(step => step.at >= begin);

  let latestVisit = -1;
  let latestRestartPage = -1;
  pages.forEach((page, i) => {
    if (isStartOf(page, V.updatePage)) latestVisit = i;
    else if (isStartOf(page, V.updateRestartPage)) latestRestartPage = i;
  });

  // A visit is open until its stop, the restart page, or a restart of the device.
  const visitOpen = latestVisit >= 0
    && !pages.slice(latestVisit + 1).some(page => isStopOf(page, V.updatePage) || isStartOf(page, V.updateRestartPage))
    && !p.rebootGaps.some(gap => gap.start > pages[latestVisit].at);

  // The restart page asked for a restart that has not happened and that it did not call off.
  const restarting = latestRestartPage >= 0
    && latestRestartPage > latestVisit
    && !p.rebootGaps.some(gap => gap.end > pages[latestRestartPage].at)
    && !pages.slice(latestRestartPage + 1).some(page => isStopOf(page, V.updateRestartPage));

  // A package mid-servicing keeps the update running. One waiting for its restart always does:
  // Windows finishes it after the restart whatever the page reported (the page can stop with
  // "fail" on a timeout while the LCU still installs across two restarts). One still initiating
  // does unless the update gave up — a failed or skipped update can leave it behind.
  const latestStep = new Map<string, string | null>();
  for (const step of servicing) {
    if (step.pkg) latestStep.set(step.pkg.toLowerCase(), step.step);
  }
  const steps = [...latestStep.values()];
  const awaitingRestart = steps.some(step => sameText(step, V.servicingSteps.rebootRequired));
  const initiating = steps.some(step => sameText(step, V.servicingSteps.initiating));

  const closedByStop = latestVisit >= 0
    ? pages.slice(latestVisit + 1).some(page => isStopOf(page, V.updatePage))
    : pages.some(page => isStopOf(page, V.updatePage));

  const gaveUp = interval.outcome === OUTCOME.failed || interval.outcome === OUTCOME.skipped;
  const concluded = !visitOpen
    && !restarting
    && !awaitingRestart
    && (!initiating || gaveUp)
    && (interval.rebootCount >= 1 || closedByStop);

  let lastReportMs: number | null = null;
  for (const e of events) {
    if (BACKFILLING_SOURCES.has(e.source)) continue;
    const at = parseUtcMs(e.timestamp);
    if (at !== null && (lastReportMs === null || at > lastReportMs)) lastReportMs = at;
  }

  return { concluded, restarting, interval, lastReportMs };
}

/**
 * The label at `nowMs`: `updating` while the update works (and for a moment after its last
 * evidence, see AWAITING_SIGN_IN_SETTLE_MS), `awaiting_sign_in` once it finished with a restart,
 * `ended` when it failed or was skipped without one; null when it finished without a restart
 * (the user is still signed in).
 */
export function osUpdateLiveState(facts: OsUpdateLiveFacts, nowMs: number): OsUpdateLiveState | null {
  const { interval } = facts;
  if (!facts.concluded) return "updating";
  if (interval.rebootCount >= 1) {
    return nowMs - interval.endMs >= AWAITING_SIGN_IN_SETTLE_MS ? "awaiting_sign_in" : "updating";
  }
  return interval.outcome === OUTCOME.failed || interval.outcome === OUTCOME.skipped ? "ended" : null;
}

/** "Now" on the device's clock: never before the newest activity event, so a device clock ahead of the browser yields no negative time. */
export function deviceNowMs(facts: OsUpdateLiveFacts, browserNowMs: number): number {
  return facts.lastReportMs === null ? browserNowMs : Math.max(browserNowMs, facts.lastReportMs);
}
