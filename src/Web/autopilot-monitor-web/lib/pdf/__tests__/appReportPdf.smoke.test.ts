// Smoke test: assemble a full report document under node (jsPDF is DOM-free for
// document assembly; only the download step in generateAppReportPdf needs a browser).
import { describe, it, expect } from "vitest";
import { buildAppReportDoc } from "../appReportPdf";
import { prepareReportModel, type AppReportInput } from "../appReportData";

function makeInput(): AppReportInput {
  const timeSeries = Array.from({ length: 13 }, (_, i) => ({
    bucketStart: new Date(Date.UTC(2026, 4, 25 + i * 7)).toISOString(),
    installs: 10 + i * 15,
    succeeded: 9 + i * 15,
    failed: i % 4 === 0 ? 1 : 0,
    failureRate: i % 4 === 0 ? 5 : 0,
    avgDurationSeconds: 90 - i * 3,
  }));
  return {
    analytics: {
      appType: "MSI",
      windowDays: 90,
      bucket: "week",
      summary: {
        totalInstalls: 912, succeeded: 888, skipped: 20, unmeasured: 3, failed: 1,
        failureRate: 0.1, avgDurationSeconds: 55, p95DurationSeconds: 110,
        trend: "stable", trendDelta: 0, flakinessScore: 0.02,
      },
      timeSeries,
      versionBreakdown: [
        { appVersion: "1.2.0", installs: 500, failed: 1, failureRate: 0.2, measuredInstalls: 480, medianDurationSeconds: 52, p95DurationSeconds: 100 },
      ],
      topFailureCodes: [
        { code: "esp_apps_install_failure", exitCode: 1618, count: 1 },
      ],
      detectionLiesCount: 18,
      deviceModelBreakdown: [
        { manufacturer: "Dell Inc.", model: "Latitude 5440", installs: 200, failed: 1, failureRate: 0.5, liftVsBaseline: 1.1 },
      ],
      versionRegressions: [],
    },
    appName: "RealmJoin Agent (Device)",
    days: 90,
    scopeLabel: "All tenants (aggregated)",
    generatedAt: new Date(2026, 7, 18),
  };
}

describe("buildAppReportDoc", () => {
  it("assembles a multi-section report without throwing", () => {
    const input = makeInput();
    const doc = buildAppReportDoc(prepareReportModel(input), input.generatedAt);
    expect(doc.getNumberOfPages()).toBeGreaterThanOrEqual(1);
    const raw = doc.output();
    expect(raw.startsWith("%PDF")).toBe(true);
  });

  it("handles an app with no data at all", () => {
    const input = makeInput();
    input.analytics = {
      ...input.analytics,
      timeSeries: [],
      versionBreakdown: [],
      topFailureCodes: [],
      detectionLiesCount: 0,
      deviceModelBreakdown: [],
      versionRegressions: [],
      summary: {
        totalInstalls: 0, succeeded: 0, skipped: 0, unmeasured: 0, failed: 0,
        failureRate: 0, avgDurationSeconds: 0, p95DurationSeconds: 0,
        trend: "stable", trendDelta: null, flakinessScore: 0,
      },
    };
    const doc = buildAppReportDoc(prepareReportModel(input), input.generatedAt);
    expect(doc.getNumberOfPages()).toBeGreaterThanOrEqual(1);
  });
});
