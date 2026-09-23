import { describe, it, expect } from "vitest";
import type { Metric } from "web-vitals";
import { WEB_VITAL_METRIC_NAMES, normalizeRoute, toWebVitalTelemetry } from "../webVitals";

/**
 * Pins the web-vitals → App Insights metric mapping (lib/webVitals.ts): the metric names the
 * monitoring catalog lists, the value units, the property keys and the route normalisation.
 */

function metric(overrides: Partial<Metric> & Pick<Metric, "name">): Metric {
  return {
    value: 0,
    rating: "good",
    delta: 0,
    id: "v5-1",
    entries: [],
    navigationType: "navigate",
    ...overrides,
  };
}

describe("WEB_VITAL_METRIC_NAMES", () => {
  it("names every vital with a stable, unique web_vital_* metric name", () => {
    expect(WEB_VITAL_METRIC_NAMES).toEqual({
      LCP: "web_vital_lcp",
      INP: "web_vital_inp",
      CLS: "web_vital_cls",
      TTFB: "web_vital_ttfb",
      FCP: "web_vital_fcp",
    });
    expect(new Set(Object.values(WEB_VITAL_METRIC_NAMES)).size).toBe(5);
  });
});

describe("toWebVitalTelemetry", () => {
  it("reports timing vitals in whole milliseconds with route, rating and navigation type", () => {
    const telemetry = toWebVitalTelemetry(
      metric({ name: "LCP", value: 2487.6, rating: "needs-improvement", navigationType: "reload" }),
      "/get-started"
    );

    expect(telemetry).toEqual({
      name: "web_vital_lcp",
      value: 2488,
      properties: { route: "/get-started", rating: "needs-improvement", navigationType: "reload" },
    });
  });

  it("rounds each timing vital to whole milliseconds", () => {
    for (const name of ["INP", "TTFB", "FCP"] as const) {
      expect(toWebVitalTelemetry(metric({ name, value: 120.49 }), "/").value).toBe(120);
      expect(toWebVitalTelemetry(metric({ name, value: 120.5 }), "/").value).toBe(121);
    }
  });

  it("keeps CLS as the unitless score with four decimals", () => {
    const telemetry = toWebVitalTelemetry(metric({ name: "CLS", value: 0.123456, rating: "poor" }), "/plans");

    expect(telemetry.name).toBe("web_vital_cls");
    expect(telemetry.value).toBe(0.1235);
    expect(telemetry.properties.rating).toBe("poor");
  });

  it("passes the bfcache navigation type through", () => {
    const telemetry = toWebVitalTelemetry(metric({ name: "TTFB", navigationType: "back-forward-cache" }), "/");

    expect(telemetry.properties.navigationType).toBe("back-forward-cache");
  });
});

describe("normalizeRoute", () => {
  it("keeps the root", () => {
    expect(normalizeRoute("/")).toBe("/");
    expect(normalizeRoute("https://www.example.test/")).toBe("/");
  });

  it("drops the trailing slash the static export links every page with", () => {
    expect(normalizeRoute("/get-started/")).toBe("/get-started");
    expect(normalizeRoute("/settings/tenant/general/")).toBe("/settings/tenant/general");
  });

  it("drops query string and hash (ids travel in the query string)", () => {
    expect(normalizeRoute("/sessions/?id=abc123&tab=events#timeline")).toBe("/sessions");
    expect(normalizeRoute("/dashboard?tenant=t1")).toBe("/dashboard");
  });

  it("accepts the navigation entry's absolute URL", () => {
    expect(normalizeRoute("https://www.example.test/plans/?utm_source=x")).toBe("/plans");
  });

  it("falls back to the root for input that is no URL", () => {
    expect(normalizeRoute("")).toBe("/");
    expect(normalizeRoute("http://[")).toBe("/");
  });
});
