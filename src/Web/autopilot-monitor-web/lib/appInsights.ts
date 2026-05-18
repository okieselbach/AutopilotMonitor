import { ApplicationInsights } from "@microsoft/applicationinsights-web";

let appInsights: ApplicationInsights | null = null;

const telemetryConfig = {
  tenantId: null as string | null,
  isAdmin: false,
  isGlobalAdmin: false,
  theme: "light" as "light" | "dark",
  sidebarState: "full" as "full" | "icons" | "hidden",
};

export function initAppInsights(connectionString: string) {
  if (appInsights || !connectionString || typeof window === "undefined") return;

  appInsights = new ApplicationInsights({
    config: {
      connectionString,
      disableCookiesUsage: true,
      enableAutoRouteTracking: true,
      disableFetchTracking: false,
      disablePageUnloadEvents: ["unload"],
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
  appInsights?.trackEvent({ name }, properties);
}
