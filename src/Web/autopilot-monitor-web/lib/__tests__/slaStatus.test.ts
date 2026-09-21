import { describe, expect, it } from "vitest";
import type { SlaMetricsResponse, SlaSnapshot } from "@/utils/wire-types.generated";
import { buildSlaChecks, formatSlaWindow, summarizeSlaChecks } from "../slaStatus";

function snapshot(overrides: Partial<SlaSnapshot> = {}): SlaSnapshot {
  return {
    period: "last-30-days",
    week: "",
    hasData: true,
    totalCompleted: 41,
    succeeded: 32,
    failed: 9,
    successRate: 78,
    avgDurationMinutes: 40,
    p95DurationMinutes: 25,
    durationViolationCount: 0,
    successRateMet: false,
    durationTargetMet: true,
    ...overrides,
  };
}

function metrics(overrides: Partial<SlaMetricsResponse> = {}): SlaMetricsResponse {
  return {
    targetSuccessRate: 95,
    targetMaxDurationMinutes: 30,
    evaluationPeriod: snapshot(),
    evaluationWindowDays: 30,
    currentWeek: snapshot({ period: "2026-W39", week: "2026-W39", hasData: false, totalCompleted: 0 }),
    weeklyTrend: [],
    violators: [],
    computedAt: "2026-09-21T12:00:00Z",
    fromCache: false,
    computeDurationMs: 1,
    ...overrides,
  } as SlaMetricsResponse;
}

describe("buildSlaChecks", () => {
  it("judges success rate and duration on the evaluation window, not on the (empty) week", () => {
    const checks = buildSlaChecks(metrics());
    expect(checks).toEqual([
      { label: "Success Rate", met: false, hasData: true },
      { label: "Duration", met: true, hasData: true },
    ]);
  });

  it("only lists configured targets", () => {
    const checks = buildSlaChecks(metrics({ targetMaxDurationMinutes: undefined }));
    expect(checks.map((c) => c.label)).toEqual(["Success Rate"]);
  });

  it("adds app installs only when the week produced a snapshot", () => {
    const withTarget = metrics({ targetAppInstallSuccessRate: 98 });
    expect(buildSlaChecks(withTarget).map((c) => c.label)).not.toContain("App Installs");

    const withSnapshot = metrics({
      targetAppInstallSuccessRate: 98,
      appInstallSla: { totalInstalls: 10, succeeded: 10, failed: 0, successRate: 100, targetMet: true, topFailingApps: [] },
    });
    expect(buildSlaChecks(withSnapshot)).toContainEqual({ label: "App Installs", met: true, hasData: true });
  });
});

describe("summarizeSlaChecks", () => {
  it("is breached when a judged target is missed", () => {
    expect(summarizeSlaChecks(buildSlaChecks(metrics()))).toEqual({ state: "breached", metCount: 1, judgedCount: 2 });
  });

  it("is met when every judged target is met", () => {
    const m = metrics({ evaluationPeriod: snapshot({ successRateMet: true }) });
    expect(summarizeSlaChecks(buildSlaChecks(m)).state).toBe("met");
  });

  it("an empty window is no data — never breached, never met", () => {
    const m = metrics({ evaluationPeriod: snapshot({ hasData: false, totalCompleted: 0, successRate: 0, successRateMet: true }) });
    expect(summarizeSlaChecks(buildSlaChecks(m))).toEqual({ state: "noData", metCount: 0, judgedCount: 0 });
  });

  it("leaves a no-data target out of the count while another one is judged", () => {
    const m = metrics({
      evaluationPeriod: snapshot({ hasData: false }),
      targetAppInstallSuccessRate: 98,
      appInstallSla: { totalInstalls: 10, succeeded: 5, failed: 5, successRate: 50, targetMet: false, topFailingApps: [] },
    });
    expect(summarizeSlaChecks(buildSlaChecks(m))).toEqual({ state: "breached", metCount: 0, judgedCount: 1 });
  });
});

describe("formatSlaWindow", () => {
  it("names the window from the length the backend reports", () => {
    expect(formatSlaWindow(30)).toBe("last 30 days");
    expect(formatSlaWindow(14)).toBe("last 14 days");
  });
});
