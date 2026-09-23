import {
  ApplicationInsights,
  DistributedTracingModes,
  type ICfgSyncConfig,
} from "@microsoft/applicationinsights-web";
import { onCLS, onFCP, onINP, onLCP, onTTFB, type Metric } from "web-vitals";
import { API_BASE_URL, PORTAL_URL, SITE_URL } from "@/utils/config";
import type { TelemetryContext } from "./appInsights";
import { normalizeRoute, toWebVitalTelemetry } from "./webVitals";
import { buildOwnOriginNonApiPatterns, shouldDropDependency } from "./webTelemetryExclusions";

/**
 * The App Insights SDK and the Web Vitals reporter. Reached only through the dynamic import in
 * lib/appInsights.ts, so the SDK (~72 KB) is its own chunk: requested with the main bundle on the
 * portal host, only once the page is idle on the public host (lib/appInsightsTiming.ts).
 */

/** The CfgSync plugin's `identifier` (CfgSyncPlugin.ts) — the key of its extensionConfig entry. */
const CFG_SYNC_PLUGIN_IDENTIFIER = "AppInsightsCfgSyncPlugin";

/**
 * The plugin otherwise fetches https://js.monitor.azure.com/scripts/b/ai.config.1.cfg.json on every
 * page load; that remote config only tunes the throttling of the SDK's own deprecation notices, so
 * blocking it costs nothing and saves a request on the critical path. Everything else keeps the
 * SDK's built-in defaults.
 */
const cfgSyncConfig: ICfgSyncConfig = { blkCdnCfg: true };

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

export function createAppInsights(connectionString: string, context: TelemetryContext): ApplicationInsights {
  const host = apiHost();
  const dependencyExclusions = buildOwnOriginNonApiPatterns([window.location.origin, SITE_URL, PORTAL_URL]);
  const appInsights = new ApplicationInsights({
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
      extensionConfig: { [CFG_SYNC_PLUGIN_IDENTIFIER]: cfgSyncConfig },
      // Own-origin fetches that are not API calls (route payloads, static JSON, the router's HEAD
      // self-requests) are noise in `dependencies` and are not recorded; see webTelemetryExclusions.
      // This catches string fetches; URL-object fetches are dropped by the initializer below.
      excludeRequestFromAutoTrackingPatterns: dependencyExclusions,
    },
  });

  appInsights.addTelemetryInitializer((envelope) => {
    envelope.data = envelope.data ?? {};
    if (context.tenantId) envelope.data["tenantId"] = context.tenantId;
    envelope.data["isAdmin"] = context.isAdmin;
    envelope.data["isGlobalAdmin"] = context.isGlobalAdmin;
    envelope.data["theme"] = context.theme;
    envelope.data["sidebarState"] = context.sidebarState;
  });

  appInsights.loadAppInsights();
  // Returning false drops the item (D-277); undefined keeps it.
  appInsights.addDependencyInitializer((details) =>
    shouldDropDependency(details.item, dependencyExclusions) ? false : undefined
  );
  // The page view of the document itself, on both hosts. The SDK tracks page views only for
  // history changes made after it is loaded (enableAutoRouteTracking), and Next.js writes the
  // initial replaceState in an insertion effect, before any effect can load the SDK — so without
  // this call a full load that navigates nowhere (deep link, reload, the public pages) left no
  // pageViews row and no browserTimings row (measured 2026-09-23: 101 of 2250 anonymous sessions
  // had a page view). The SDK completes the row once performance.timing is ready, so an init
  // after the load event still yields the timings.
  appInsights.trackPageView();
  reportWebVitals(appInsights);
  return appInsights;
}

/**
 * Core Web Vitals as App Insights metrics: one row per metric and page load, reported by
 * web-vitals once the value is final (a bfcache restore counts as a new visit and reports
 * again). Registered after the SDK is up — on the public host that is after idle, which is fine:
 * web-vitals observes with `buffered: true`, so paint, LCP, layout-shift and event entries from
 * before the registration are still delivered, and TTFB reads the navigation entry.
 */
function reportWebVitals(appInsights: ApplicationInsights): void {
  // The metrics describe the document's own navigation, not the SPA route the user may have
  // reached by the time a late metric (LCP on first input, CLS/INP on hide) becomes final.
  const navigation = performance.getEntriesByType("navigation")[0];
  const route = normalizeRoute(navigation?.name || window.location.pathname);

  const report = (metric: Metric) => {
    const telemetry = toWebVitalTelemetry(metric, route);
    appInsights.trackMetric({ name: telemetry.name, average: telemetry.value }, telemetry.properties);
    // CLS, INP and a late LCP become final on the same visibilitychange/pagehide the SDK flushes
    // on (its housekeeping listeners → onunloadFlush → beacon), but that flush only ships what is
    // queued when its listener runs. Flushing here makes the row survive a tab close regardless
    // of listener order.
    if (document.visibilityState === "hidden") appInsights.onunloadFlush();
  };

  onTTFB(report);
  onFCP(report);
  onLCP(report);
  onCLS(report);
  onINP(report);
}
