import { ApplicationInsights, DistributedTracingModes } from "@microsoft/applicationinsights-web";
import { API_BASE_URL } from "@/utils/config";

let appInsights: ApplicationInsights | null = null;

/**
 * Host of the backend API for the SDK's cross-origin correlation: the portal → API calls are
 * cross-origin, and without an explicit allow-list the SDK attaches no W3C `traceparent` to
 * them, so the portal's dependency rows and the backend's request rows never share an
 * operation_Id. Only the API host — never the identity or blob hosts.
 */
function apiHost(): string | null {
  try {
    return new URL(API_BASE_URL).host;
  } catch {
    return null;
  }
}

// Events fired before initAppInsights (module-init code like the MSAL redirect handling and
// the dual app-reg login fallback runs before React mounts AppInsightsInit) are buffered and
// replayed on init instead of being dropped — those early auth events are exactly the ones
// incident tracing needs. Bounded so a pathological pre-init loop can't grow memory.
const MaxPreInitEvents = 30;
const preInitEvents: Array<{ name: string; properties?: Record<string, string | number | boolean> }> = [];

const telemetryConfig = {
  tenantId: null as string | null,
  isAdmin: false,
  isGlobalAdmin: false,
  theme: "light" as "light" | "dark",
  sidebarState: "full" as "full" | "icons" | "hidden",
};

export function initAppInsights(connectionString: string) {
  if (appInsights || !connectionString || typeof window === "undefined") return;

  const host = apiHost();
  appInsights = new ApplicationInsights({
    config: {
      connectionString,
      disableCookiesUsage: true,
      enableAutoRouteTracking: true,
      disableFetchTracking: false,
      disablePageUnloadEvents: ["unload"],
      // Distributed tracing to the backend: W3C traceparent on API calls (backend side:
      // RequestTelemetryMiddleware reads Activity.Current), scoped to the API host so the
      // preflight-triggering headers never reach login.microsoftonline.com or blob storage.
      enableCorsCorrelation: host !== null,
      correlationHeaderDomains: host ? [host] : undefined,
      distributedTracingMode: DistributedTracingModes.W3C,
    },
  });

  appInsights.addTelemetryInitializer((envelope) => {
    envelope.data = envelope.data ?? {};
    if (telemetryConfig.tenantId) envelope.data["tenantId"] = telemetryConfig.tenantId;
    envelope.data["isAdmin"] = telemetryConfig.isAdmin;
    envelope.data["isGlobalAdmin"] = telemetryConfig.isGlobalAdmin;
    envelope.data["theme"] = telemetryConfig.theme;
    envelope.data["sidebarState"] = telemetryConfig.sidebarState;
  });

  appInsights.loadAppInsights();

  for (const event of preInitEvents.splice(0)) {
    appInsights.trackEvent({ name: event.name }, event.properties);
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
