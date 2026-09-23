import type { Metric } from "web-vitals";

/**
 * Core Web Vitals → App Insights metrics (customMetrics). Pure mapping; the reporter that
 * feeds it lives in lib/appInsightsRuntime.ts.
 */

/** App Insights metric name (customMetrics.name) per web-vitals metric. */
export const WEB_VITAL_METRIC_NAMES: Record<Metric["name"], string> = {
  LCP: "web_vital_lcp",
  INP: "web_vital_inp",
  CLS: "web_vital_cls",
  TTFB: "web_vital_ttfb",
  FCP: "web_vital_fcp",
};

export interface WebVitalTelemetry {
  name: string;
  /** Whole milliseconds for LCP/INP/TTFB/FCP; the unitless layout-shift score (4 decimals) for CLS. */
  value: number;
  properties: {
    /** Pathname of the document that produced the metric, see normalizeRoute. */
    route: string;
    rating: Metric["rating"];
    navigationType: Metric["navigationType"];
  };
}

/**
 * Route identity of a page load: its pathname without query string, hash or trailing slash
 * (the static export links every page with one). Ids live in the query string in this app
 * and the only dynamic segments are fixed section names, so the pathname is already
 * route-like. Accepts an absolute URL (the navigation entry's name) or a bare pathname.
 */
export function normalizeRoute(urlOrPathname: string): string {
  let pathname: string;
  try {
    pathname = new URL(urlOrPathname, "http://localhost").pathname;
  } catch {
    return "/";
  }
  return pathname.replace(/\/+$/, "") || "/";
}

export function toWebVitalTelemetry(
  metric: Pick<Metric, "name" | "value" | "rating" | "navigationType">,
  route: string
): WebVitalTelemetry {
  const value = metric.name === "CLS" ? Math.round(metric.value * 10000) / 10000 : Math.round(metric.value);
  return {
    name: WEB_VITAL_METRIC_NAMES[metric.name],
    value,
    properties: { route, rating: metric.rating, navigationType: metric.navigationType },
  };
}
