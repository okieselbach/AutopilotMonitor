import { describe, it, expect } from "vitest";
import {
  appReportFileName,
  scopeLabel,
  prepareReportModel,
  formatReportDate,
  type AppReportAnalytics,
  type AppReportInput,
} from "../appReportData";

function makeAnalytics(overrides: Partial<AppReportAnalytics> = {}): AppReportAnalytics {
  return {
    appType: "MSI",
    windowDays: 90,
    bucket: "week",
    summary: {
      totalInstalls: 912,
      succeeded: 888,
      skipped: 20,
      unmeasured: 3,
      failed: 1,
      failureRate: 0.1,
      avgDurationSeconds: 55,
      p95DurationSeconds: 110,
      trend: "stable",
      trendDelta: 0,
      flakinessScore: 0.02,
    },
    timeSeries: [
      { bucketStart: "2026-08-03T00:00:00Z", installs: 10, succeeded: 9, failed: 1, failureRate: 10, avgDurationSeconds: 50 },
    ],
    versionBreakdown: [],
    topFailureCodes: [],
    detectionLiesCount: 0,
    deviceModelBreakdown: [],
    versionRegressions: [],
    ...overrides,
  };
}

function makeInput(overrides: Partial<AppReportAnalytics> = {}): AppReportInput {
  return {
    analytics: makeAnalytics(overrides),
    appName: "RealmJoin Agent (Device)",
    days: 90,
    scopeLabel: "All tenants (aggregated)",
    generatedAt: new Date(2026, 7, 18), // Aug 18, 2026 local
  };
}

describe("appReportFileName", () => {
  it("slugifies the app name with window and date", () => {
    expect(appReportFileName("RealmJoin Agent (Device)", 90, new Date(2026, 7, 18))).toBe(
      "app-report-realmjoin-agent-device-90d-20260818.pdf"
    );
  });

  it("falls back to 'app' when the name has no latin characters", () => {
    expect(appReportFileName("日本語アプリ", 30, new Date(2026, 0, 5))).toBe(
      "app-report-app-30d-20260105.pdf"
    );
  });

  it("caps overlong names without a trailing dash", () => {
    const name = "A".repeat(100) + " suffix";
    const file = appReportFileName(name, 7, new Date(2026, 7, 18));
    const slug = file.replace("app-report-", "").replace("-7d-20260818.pdf", "");
    expect(slug.length).toBeLessThanOrEqual(60);
    expect(slug.endsWith("-")).toBe(false);
  });
});

describe("scopeLabel", () => {
  it("labels the aggregated global view", () => {
    expect(
      scopeLabel({ isAggregatedGlobalView: true, selectedTenantName: undefined, selectedTenantId: "" })
    ).toBe("All tenants (aggregated)");
  });

  it("prefers the friendly tenant name", () => {
    expect(
      scopeLabel({ isAggregatedGlobalView: false, selectedTenantName: "contoso.com", selectedTenantId: "guid" })
    ).toBe("Tenant: contoso.com");
  });

  it("falls back to a shortened tenant id, then to 'Current tenant'", () => {
    expect(
      scopeLabel({
        isAggregatedGlobalView: false,
        selectedTenantName: undefined,
        selectedTenantId: "12345678-abcd-ef00-0000-000000000000",
      })
    ).toBe("Tenant: 12345678…");
    expect(
      scopeLabel({ isAggregatedGlobalView: false, selectedTenantName: undefined, selectedTenantId: "" })
    ).toBe("Current tenant");
  });
});

describe("prepareReportModel", () => {
  it("builds meta line, file name, and 8 KPIs", () => {
    const model = prepareReportModel(makeInput());
    expect(model.metaLine).toBe(
      "90 day window · weekly buckets · All tenants (aggregated) · Generated Aug 18, 2026"
    );
    expect(model.fileName).toBe("app-report-realmjoin-agent-device-90d-20260818.pdf");
    expect(model.kpis).toHaveLength(8);
    expect(model.kpis[0]).toMatchObject({ label: "Total installs", value: "912" });
    expect(model.kpis[1].hint).toBe("+20 skipped (not applicable)");
    expect(model.kpis[3]).toMatchObject({ value: "0.1%", tone: "default" });
    expect(model.kpis[6].value).toBe("stable");
    expect(model.kpis[7].value).toBe("2%");
  });

  it("marks elevated failure rates with warning/danger tones", () => {
    const warn = prepareReportModel(
      makeInput({ summary: { ...makeAnalytics().summary, failureRate: 7.5 } })
    );
    expect(warn.kpis[3].tone).toBe("warning");
    const danger = prepareReportModel(
      makeInput({ summary: { ...makeAnalytics().summary, failureRate: 25 } })
    );
    expect(danger.kpis[3].tone).toBe("danger");
  });

  it("renders trend deltas as signed values (Helvetica has no arrow glyphs)", () => {
    const improving = prepareReportModel(
      makeInput({ summary: { ...makeAnalytics().summary, trend: "improving", trendDelta: -1.23 } })
    );
    expect(improving.kpis[6].value).toBe("-1.2 pp");
    expect(improving.kpis[6].hint).toBe("improving");
    const worsening = prepareReportModel(
      makeInput({ summary: { ...makeAnalytics().summary, trend: "worsening", trendDelta: 2.5 } })
    );
    expect(worsening.kpis[6].value).toBe("+2.5 pp");
  });

  it("truncates failure codes to 10 rows and reports the remainder", () => {
    const codes = Array.from({ length: 12 }, (_, i) => ({
      code: `0x8007000${i}`,
      exitCode: null,
      count: 12 - i,
    }));
    const model = prepareReportModel(makeInput({ topFailureCodes: codes }));
    expect(model.failureCodes.rows).toHaveLength(10);
    expect(model.failureCodes.moreCount).toBe(2);
  });

  it("keeps the full exit-code description untruncated", () => {
    const model = prepareReportModel(
      makeInput({
        topFailureCodes: [{ code: "esp_apps_install_failure", exitCode: 1618, count: 1 }],
      })
    );
    expect(model.failureCodes.rows[0][0]).toBe("esp_apps_install_failure");
    expect(model.failureCodes.rows[0][2]).toBe("1618 (Another installation already in progress)");
  });

  it("enriches known error codes and labels unknown ones", () => {
    const model = prepareReportModel(
      makeInput({
        topFailureCodes: [
          { code: "0x80070005", exitCode: null, count: 3 },
          { code: "0xDEADBEEF", exitCode: null, count: 1 },
        ],
      })
    );
    expect(model.failureCodes.rows[0][1]).not.toBe("Unknown code");
    expect(model.failureCodes.rows[0][1].length).toBeGreaterThan(0);
    expect(model.failureCodes.rows[1][1]).toBe("Unknown code");
  });

  it("sorts device models by lift and truncates to 10", () => {
    const models = Array.from({ length: 12 }, (_, i) => ({
      manufacturer: "Contoso",
      model: `Model ${i}`,
      installs: 10,
      failed: i,
      failureRate: i * 5,
      liftVsBaseline: i * 0.3,
    }));
    const model = prepareReportModel(makeInput({ deviceModelBreakdown: models }));
    expect(model.deviceModels.rows).toHaveLength(10);
    expect(model.deviceModels.moreCount).toBe(2);
    expect(model.deviceModels.rows[0][1]).toBe("Model 11");
    expect(model.deviceModels.rows[0][5]).toBe("3.30x");
  });

  it("shows version durations only when measured", () => {
    const model = prepareReportModel(
      makeInput({
        versionBreakdown: [
          { appVersion: "1.0", installs: 5, failed: 0, failureRate: 0, measuredInstalls: 5, medianDurationSeconds: 90, p95DurationSeconds: 120 },
          { appVersion: "1.1", installs: 3, failed: 1, failureRate: 33.3, measuredInstalls: 0, medianDurationSeconds: 0, p95DurationSeconds: 0 },
        ],
      })
    );
    expect(model.versions.rows[0][4]).toBe("2m");
    expect(model.versions.rows[1][4]).toBe("—");
    expect(model.versions.rows[1][5]).toBe("—");
  });

  it("emits detection-lies text with singular/plural forms", () => {
    expect(prepareReportModel(makeInput({ detectionLiesCount: 0 })).detectionLiesText).toBeNull();
    expect(prepareReportModel(makeInput({ detectionLiesCount: 1 })).detectionLiesText).toContain(
      "1 install was reported as Succeeded"
    );
    expect(prepareReportModel(makeInput({ detectionLiesCount: 18 })).detectionLiesText).toContain(
      "18 installs were reported as Succeeded"
    );
  });

  it("formats regression lines and caps them at 3", () => {
    const reg = {
      currentVersion: "2.0",
      previousVersion: "1.9",
      currentMedianSeconds: 300,
      previousMedianSeconds: 120,
      currentMeasuredCount: 40,
      previousMeasuredCount: 60,
      lift: 2.5,
    };
    const model = prepareReportModel(makeInput({ versionRegressions: [reg, reg, reg, reg] }));
    expect(model.regressionLines).toHaveLength(3);
    expect(model.regressionLines[0]).toBe(
      "Duration regression: median install duration rose from 2.0 to 5.0 min after version 2.0 (40 measured installs vs 60 on 1.9 — lift 2.5x)"
    );
  });

  it("produces a valid model for an app with no data at all", () => {
    const model = prepareReportModel(
      makeInput({
        summary: {
          totalInstalls: 0, succeeded: 0, skipped: 0, unmeasured: 0, failed: 0,
          failureRate: 0, avgDurationSeconds: 0, p95DurationSeconds: 0,
          trend: "stable", trendDelta: null, flakinessScore: 0,
        },
        timeSeries: [],
      })
    );
    expect(model.kpis[0].value).toBe("0");
    expect(model.kpis[4].value).toBe("—");
    expect(model.kpis[6].value).toBe("—");
    expect(model.timeSeries).toEqual([]);
    expect(model.failureCodes.rows).toEqual([]);
    expect(model.failureCodes.moreCount).toBe(0);
  });
});

describe("formatReportDate", () => {
  it("is locale-independent", () => {
    expect(formatReportDate(new Date(2026, 0, 5))).toBe("Jan 5, 2026");
    expect(formatReportDate(new Date(2026, 11, 31))).toBe("Dec 31, 2026");
  });
});
