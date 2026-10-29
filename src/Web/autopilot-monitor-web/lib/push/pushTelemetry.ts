/**
 * Usage telemetry of the push receiver (D-387): how the app is opened, the pairing steps, what is done in it
 * and on which kind of device, attributed to the pairing scope its device token names. Never sent: anything
 * from the history (titles, texts, facts, scope names), the owner, ids or the token itself. Of the token
 * only the scope segment is read. The decisions are pure functions (lib/__tests__/pushTelemetry.test.ts);
 * the module state at the bottom feeds them from the browser.
 */
import { setPushReceiverContext, trackEvent } from "@/lib/appInsights";
import { PushApiError } from "./pushApi";
import { isStandaloneDisplay, notificationPermission, platformInput, type ReconcileResult } from "./pushClient";
import { detectPlatform, META_KEYS, parseFragment, readMeta, type PlatformInput } from "./pushCore";

type Properties = Record<string, string | number | boolean>;

// --- Scope ----------------------------------------------------------------------------------

export type ReceiverScope = { kind: "platform" } | { kind: "tenant"; tenantId: string };

const TENANT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The scope a device token ("{scope}.{deviceId}.{secret}") was issued for; null for anything else. */
export function receiverScope(token: string | null | undefined): ReceiverScope | null {
  const parts = (token ?? "").split(".");
  if (parts.length < 3) return null;
  if (parts[0] === "platform") return { kind: "platform" };
  return TENANT_ID.test(parts[0]) ? { kind: "tenant", tenantId: parts[0].toLowerCase() } : null;
}

// --- Device facts ---------------------------------------------------------------------------

/** User-Agent Client Hints: Chromium only (Android, desktop Chrome and Edge); Safari and Firefox give none. */
export interface UaHints {
  model?: string;
  platformVersion?: string;
}

export interface DeviceFactsInput extends PlatformInput {
  standalone: boolean;
  permission: NotificationPermission | "unsupported";
  screenWidth: number;
  screenHeight: number;
  pixelRatio: number;
  hints: UaHints | null;
}

const MAX_HINT_CHARS = 64;

export function deviceFacts(input: DeviceFactsInput): Properties {
  const facts: Properties = {
    platform: detectPlatform(input),
    display: input.standalone ? "standalone" : "browser",
    permission: input.permission,
    screen: screenClass(input.screenWidth, input.screenHeight, input.pixelRatio),
  };
  const model = input.hints?.model?.trim();
  if (model) facts.deviceModel = model.slice(0, MAX_HINT_CHARS);
  const osVersion = input.hints?.platformVersion?.trim();
  if (osVersion) facts.osVersion = osVersion.slice(0, MAX_HINT_CHARS);
  return facts;
}

/** "393x852@3": CSS pixels short side first, whatever the rotation, and the pixel ratio — on an iPhone the only hint of its model family. */
export function screenClass(width: number, height: number, pixelRatio: number): string {
  const short = Math.round(Math.min(width, height));
  const long = Math.round(Math.max(width, height));
  return `${short}x${long}@${Math.round(pixelRatio * 100) / 100}`;
}

// --- Opens ----------------------------------------------------------------------------------

export type OpenSource = "notification" | "pair_link" | "direct";

export interface AppOpen {
  launch: "load" | "resume";
  source: OpenSource;
}

/** Coming back after at least this long in the background counts as opening the app again. */
export const RESUME_AFTER_MS = 60_000;
/** A notification tap this close to a return is its reason; the worker's message and the hash change report the same tap twice. */
export const NOTIFICATION_GRACE_MS = 2_000;

export interface OpenTrackerDeps {
  now: () => number;
  setTimer: (callback: () => void, ms: number) => number;
  clearTimer: (handle: number) => void;
  /** An open was detected (load or return): the next server check belongs to it. */
  onDetected: () => void;
  emit: (open: AppOpen) => void;
}

export interface OpenTracker {
  /** The first receiver page of the document reports its load; later pages of the same document do not. */
  load: (source: OpenSource) => void;
  hidden: () => void;
  visible: () => void;
  /** A notification tap reached the open app (worker message or a hash change to an entry). */
  notification: () => void;
}

/**
 * One event per open. A return waits out the grace before it counts as direct, because the tap that
 * brought the app back is reported just after it becomes visible; leaving within the grace still counts it.
 */
export function createOpenTracker(deps: OpenTrackerDeps): OpenTracker {
  let loaded = false;
  let hiddenAt: number | null = null;
  let pendingReturn: number | null = null;
  let lastNotificationAt = Number.NEGATIVE_INFINITY;

  const settlePendingReturn = (emit: boolean) => {
    if (pendingReturn === null) return;
    deps.clearTimer(pendingReturn);
    pendingReturn = null;
    if (emit) deps.emit({ launch: "resume", source: "direct" });
  };

  return {
    load(source) {
      if (loaded) return;
      loaded = true;
      deps.onDetected();
      deps.emit({ launch: "load", source });
    },
    hidden() {
      hiddenAt = deps.now();
      settlePendingReturn(true);
    },
    visible() {
      if (hiddenAt === null) return;
      const away = deps.now() - hiddenAt;
      hiddenAt = null;
      if (away < RESUME_AFTER_MS) return;
      deps.onDetected();
      if (deps.now() - lastNotificationAt <= NOTIFICATION_GRACE_MS) return;
      pendingReturn = deps.setTimer(() => {
        pendingReturn = null;
        deps.emit({ launch: "resume", source: "direct" });
      }, NOTIFICATION_GRACE_MS);
    },
    notification() {
      const now = deps.now();
      if (now - lastNotificationAt <= NOTIFICATION_GRACE_MS) return;
      lastNotificationAt = now;
      settlePendingReturn(false);
      deps.emit({ launch: "resume", source: "notification" });
    },
  };
}

// --- Event properties -----------------------------------------------------------------------

export function deviceCheckProperties(result: ReconcileResult): Properties {
  if (result.state !== "ok") return { state: result.state };
  return {
    state: "ok",
    status: result.device.status,
    subscription: result.subscriptionPresent ? "present" : "missing",
    repair: result.repair,
    repairFailed: result.repairError !== null,
  };
}

/** A failed pairing step reports the API's error code or the HTTP status, never a message. */
export function pairErrorCode(error: unknown): string {
  if (error instanceof PushApiError) return error.code ?? `http-${error.status}`;
  if (error instanceof Error && error.name === "TimeoutError") return "timeout";
  if (error instanceof TypeError) return "network";
  return "other";
}

// --- Browser wiring -------------------------------------------------------------------------

type PushEvent = "push_app_opened" | "push_device_checked" | "push_pair_step" | "push_action";
type PairStep = "shown" | "code_invalid" | "permission" | "subscribe_failed" | "failed" | "waiting" | "confirmed" | "rejected";
type HistoryAction = "expand" | "portal" | "sheet" | "delete" | "filter" | "refresh";
type StatusAction = "retention_saved" | "diagnostics_opened" | "notifications_imported" | "unpaired" | "unpair_failed";

/** The first event of a page load waits at most this long for the Client Hints and the stored scope. */
const READY_WAIT_MS = 300;

interface UaDataLike {
  getHighEntropyValues(hints: string[]): Promise<UaHints>;
}

let hints: UaHints | null = null;
let scopeSetExplicitly = false;
let checkPending = false;
let readiness: Promise<void> | null = null;
let queue: Promise<void> = Promise.resolve();

async function loadHints(): Promise<void> {
  const uaData = (navigator as Navigator & { userAgentData?: UaDataLike }).userAgentData;
  if (!uaData) return;
  try {
    hints = await uaData.getHighEntropyValues(["model", "platformVersion"]);
  } catch {
    // Refused: the facts go without model and version.
  }
}

async function loadStoredScope(): Promise<void> {
  const meta = await readMeta().catch(() => ({}) as Record<string, string>);
  if (!scopeSetExplicitly) applyScope(meta[META_KEYS.deviceToken]);
}

function applyScope(token: string | null | undefined): void {
  const scope = receiverScope(token);
  setPushReceiverContext(scope?.kind ?? null, scope?.kind === "tenant" ? scope.tenantId : null);
}

function ready(): Promise<void> {
  readiness ??= Promise.race([
    Promise.all([loadHints(), loadStoredScope()]).then(() => undefined),
    new Promise<void>((resolve) => window.setTimeout(resolve, READY_WAIT_MS)),
  ]);
  return readiness;
}

/** Events and scope changes run in call order, each after the facts and the stored scope are read. */
function enqueue(step: () => void): void {
  queue = queue.then(ready).then(step).catch(() => undefined);
}

function currentPage(): "history" | "status" | "pair" {
  const path = window.location.pathname.replace(/\/$/, "");
  if (path === "/push/status") return "status";
  if (path === "/push/pair") return "pair";
  return "history";
}

function track(name: PushEvent, properties: Properties): void {
  if (typeof window === "undefined") return;
  enqueue(() =>
    trackEvent(name, {
      ...deviceFacts({
        ...platformInput(),
        standalone: isStandaloneDisplay(),
        permission: notificationPermission(),
        screenWidth: window.screen.width,
        screenHeight: window.screen.height,
        pixelRatio: window.devicePixelRatio || 1,
        hints,
      }),
      ...properties,
    }),
  );
}

const tracker = createOpenTracker({
  now: () => Date.now(),
  setTimer: (callback, ms) => window.setTimeout(callback, ms),
  clearTimer: (handle) => window.clearTimeout(handle),
  onDetected: () => {
    checkPending = true;
  },
  emit: (open) => track("push_app_opened", { page: currentPage(), ...open }),
});

/** Mounted once by the receiver's layout: returns to the app and hash changes to an entry. Returns the cleanup. */
export function startReceiverTelemetry(): () => void {
  void ready();
  const onVisibility = () => (document.visibilityState === "visible" ? tracker.visible() : tracker.hidden());
  const onHash = () => {
    if (parseFragment(window.location.hash)?.kind === "entry") tracker.notification();
  };
  document.addEventListener("visibilitychange", onVisibility);
  window.addEventListener("hashchange", onHash);
  return () => {
    document.removeEventListener("visibilitychange", onVisibility);
    window.removeEventListener("hashchange", onHash);
  };
}

export function reportAppLoad(source: OpenSource): void {
  if (typeof window !== "undefined") tracker.load(source);
}

export function noteNotificationOpen(): void {
  tracker.notification();
}

/** After pairing (the new token) or when the device is gone (null); queued behind the events already tracked. */
export function setReceiverToken(token: string | null): void {
  if (typeof window === "undefined") return;
  enqueue(() => {
    scopeSetExplicitly = true;
    applyScope(token);
  });
}

/** The server check that follows an open, once per open; a device the server no longer knows sheds its scope after the event. */
export function reportDeviceCheck(result: ReconcileResult): void {
  if (!checkPending) return;
  checkPending = false;
  track("push_device_checked", deviceCheckProperties(result));
  if (result.state === "gone") setReceiverToken(null);
}

export function trackPairStep(step: PairStep, properties: Properties = {}): void {
  track("push_pair_step", { step, ...properties });
}

export function trackHistoryAction(action: HistoryAction, properties: Properties = {}): void {
  track("push_action", { area: "history", action, ...properties });
}

export function trackStatusAction(action: StatusAction, properties: Properties = {}): void {
  track("push_action", { area: "status", action, ...properties });
}
