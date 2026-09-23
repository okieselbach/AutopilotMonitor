import type { ApplicationInsights } from "@microsoft/applicationinsights-web";
import { browserIdleEnv, decideInitTiming, runWhenIdle } from "./appInsightsTiming";
import { isOnPortalHost } from "./hostRouting";

let appInsights: ApplicationInsights | null = null;
let initStarted = false;

// Events fired before the SDK is up (module-init code like the MSAL redirect handling and
// the dual app-reg login fallback runs before React mounts AppInsightsInit; on the public
// host the init itself waits until the page is idle) are buffered and replayed on init
// instead of being dropped — those early auth events are exactly the ones incident tracing
// needs. Bounded so a pathological pre-init loop can't grow memory.
const MaxPreInitEvents = 30;
const preInitEvents: Array<{ name: string; properties?: Record<string, string | number | boolean> }> = [];

const telemetryConfig = {
  tenantId: null as string | null,
  isAdmin: false,
  isGlobalAdmin: false,
  theme: "light" as "light" | "dark",
  sidebarState: "full" as "full" | "icons" | "hidden",
};

/** Live context the runtime's telemetry initializer reads at send time. */
export type TelemetryContext = typeof telemetryConfig;

type Runtime = typeof import("./appInsightsRuntime");
let runtime: Promise<Runtime> | null = null;

/** The SDK chunk (lib/appInsightsRuntime.ts): one request, on first use. */
function loadRuntime(): Promise<Runtime> {
  return (runtime ??= import("./appInsightsRuntime"));
}

// Portal host: request the SDK chunk while this module evaluates (part of the main bundle,
// before hydration), so the init at AppInsightsInit's mount does not wait for a round trip —
// the timing the signed-in app has today. The public host requests it only once idle.
if (typeof window !== "undefined" && isOnPortalHost()) {
  void loadRuntime().catch(() => undefined);
}

export function initAppInsights(connectionString: string) {
  if (initStarted || !connectionString || typeof window === "undefined") return;
  initStarted = true;

  const timing = decideInitTiming({ onPortalHost: isOnPortalHost() });
  const start = () => {
    loadRuntime()
      .then((mod) => {
        appInsights = mod.createAppInsights(connectionString, telemetryConfig);
        for (const event of preInitEvents.splice(0)) {
          appInsights.trackEvent({ name: event.name }, event.properties);
        }
      })
      // A chunk that no longer exists (stale bundle after a deploy) or a failing SDK init leaves
      // telemetry off for this page load; it never takes the page down and, being handled here,
      // never triggers ChunkReloadRecovery's reload.
      .catch(() => undefined);
  };

  if (timing === "immediate") {
    start();
  } else {
    runWhenIdle(start, browserIdleEnv());
  }
}

export function setTelemetryContext(
  tenantId: string | null,
  isAdmin: boolean,
  isGlobalAdmin: boolean,
  theme: "light" | "dark",
  sidebarState?: "full" | "icons" | "hidden"
) {
  telemetryConfig.tenantId = tenantId;
  telemetryConfig.isAdmin = isAdmin;
  telemetryConfig.isGlobalAdmin = isGlobalAdmin;
  telemetryConfig.theme = theme;
  if (sidebarState) telemetryConfig.sidebarState = sidebarState;
}

export function setSidebarStateContext(state: "full" | "icons" | "hidden") {
  telemetryConfig.sidebarState = state;
}

export function trackEvent(
  name: string,
  properties?: Record<string, string | number | boolean>
) {
  if (appInsights) {
    appInsights.trackEvent({ name }, properties);
    return;
  }
  // Not initialized yet (or AI disabled for this deployment): buffer browser-side so the
  // event survives until init. SSR/build calls are dropped — there is no page to attribute
  // them to and initAppInsights never runs server-side.
  if (typeof window !== "undefined" && preInitEvents.length < MaxPreInitEvents) {
    preInitEvents.push({ name, properties });
  }
}
