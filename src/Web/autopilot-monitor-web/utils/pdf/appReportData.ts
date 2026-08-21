// Pure data preparation for the app install report PDF — no jsPDF import, so the
// page can statically import the helpers here without pulling jsPDF into the
// route chunk (jsPDF enters only via the lazily imported appReportPdf module).

import { getErrorCodeEntry, formatErrorCode } from "@/utils/errorCodeMap";

// Structural mirror of the fields the report uses from the page's module-local
// AnalyticsResponse. A field rename on the page surfaces as a compile error at
// the generateAppReportPdf call site — intentional, cheap coupling.
export interface ReportTimeSeriesPoint {
  bucketStart: string;
  installs: number;
  succeeded: number;
  failed: number;
  failureRate: number;
  avgDurationSeconds: number;
}

export interface AppReportAnalytics {
  appType: string;
  windowDays: number;
  bucket: "day" | "week";
  summary: {
    totalInstalls: number;
    succeeded: number;
    skipped: number;
    unmeasured: number;
    failed: number;
    failureRate: number;
    avgDurationSeconds: number;
    p95DurationSeconds: number;
    trend: "improving" | "worsening" | "stable";
    trendDelta: number | null;
    flakinessScore: number;
  };
  timeSeries: ReportTimeSeriesPoint[];
  versionBreakdown: Array<{
    appVersion: string;
    installs: number;
    failed: number;
    failureRate: number;
    measuredInstalls: number;
    medianDurationSeconds: number;
    p95DurationSeconds: number;
  }>;
  topFailureCodes: Array<{
    code: string;
    exitCode: number | null;
    count: number;
  }>;
  detectionLiesCount: number;
  deviceModelBreakdown: Array<{
    manufacturer: string;
    model: string;
    installs: number;
    failed: number;
    failureRate: number;
    liftVsBaseline: number;
  }>;
  versionRegressions: Array<{
    currentVersion: string;
    previousVersion: string;
    currentMedianSeconds: number;
    previousMedianSeconds: number;
    currentMeasuredCount: number;
    previousMeasuredCount: number;
    lift: number;
  }>;
}

export interface AppReportInput {
  analytics: AppReportAnalytics;
  appName: string;
  days: number;
  scopeLabel: string;
  generatedAt: Date;
}

export type KpiTone = "default" | "success" | "danger" | "warning";

export interface ReportKpi {
  label: string;
  value: string;
  tone: KpiTone;
  hint?: string;
}

export interface ReportTable {
  rows: string[][];
  /** Rows cut off by the per-table cap — rendered as a trailing "+N more" note. */
  moreCount: number;
}

export interface AppReportModel {
  appName: string;
  appType: string;
  metaLine: string;
  fileName: string;
  kpis: ReportKpi[];
  detectionLiesText: string | null;
  regressionLines: string[];
  bucket: "day" | "week";
  timeSeries: ReportTimeSeriesPoint[];
  failureCodes: ReportTable;
  deviceModels: ReportTable;
  versions: ReportTable;
}

const MAX_FAILURE_CODE_ROWS = 10;
const MAX_DEVICE_MODEL_ROWS = 10;
const MAX_VERSION_ROWS = 8;
const MAX_REGRESSION_LINES = 3;
const MAX_SLUG_LENGTH = 60;

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/** Locale-independent "Aug 20, 2026" (local time of the exporting user). */
export function formatReportDate(date: Date): string {
  return `${MONTHS[date.getMonth()]} ${date.getDate()}, ${date.getFullYear()}`;
}

/** `app-report-realmjoin-agent-device-90d-20260818.pdf` */
export function appReportFileName(appName: string, days: number, date: Date): string {
  const slug =
    appName
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "")
      .slice(0, MAX_SLUG_LENGTH)
      .replace(/-+$/, "") || "app";
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, "0");
  const d = String(date.getDate()).padStart(2, "0");
  return `app-report-${slug}-${days}d-${y}${m}${d}.pdf`;
}

/** Human-readable tenant scope for the report title block. */
export function scopeLabel(scope: {
  isAggregatedGlobalView: boolean;
  selectedTenantName?: string;
  selectedTenantId: string;
}): string {
  if (scope.isAggregatedGlobalView) return "All tenants (aggregated)";
  if (scope.selectedTenantName) return `Tenant: ${scope.selectedTenantName}`;
  if (scope.selectedTenantId) return `Tenant: ${scope.selectedTenantId.slice(0, 8)}…`;
  return "Current tenant";
}

// Same formatting rules as the app detail page.
function formatDuration(s: number): string {
  if (!s) return "—";
  if (s < 60) return `${Math.round(s)}s`;
  if (s < 3600) return `${Math.round(s / 60)}m`;
  return `${(s / 3600).toFixed(1)}h`;
}

// PDF variant of the page's trendText: built-in Helvetica (WinAnsi) has no
// arrow glyphs, so improving/worsening render as signed deltas instead.
function trendText(trend: string, delta: number | null): string {
  if (delta == null) return "—";
  if (trend === "improving") return `-${Math.abs(delta).toFixed(1)} pp`;
  if (trend === "worsening") return `+${Math.abs(delta).toFixed(1)} pp`;
  return "stable";
}

function failureRateTone(rate: number): KpiTone {
  if (rate >= 20) return "danger";
  if (rate >= 5) return "warning";
  return "default";
}

/** Build the render-ready report model from the page's analytics response. */
export function prepareReportModel(input: AppReportInput): AppReportModel {
  const { analytics, appName, days, generatedAt } = input;
  const s = analytics.summary;

  const kpis: ReportKpi[] = [
    { label: "Total installs", value: String(s.totalInstalls), tone: "default" },
    {
      label: "Succeeded",
      value: String(s.succeeded),
      tone: "success",
      hint: s.skipped > 0 ? `+${s.skipped} skipped (not applicable)` : undefined,
    },
    { label: "Failed", value: String(s.failed), tone: s.failed > 0 ? "danger" : "default" },
    {
      label: "Failure rate",
      value: `${s.failureRate.toFixed(1)}%`,
      tone: failureRateTone(s.failureRate),
    },
    {
      label: "Avg install time",
      value: formatDuration(s.avgDurationSeconds),
      tone: "default",
      hint:
        s.unmeasured > 0
          ? `final attempt, measured installs only — ${s.unmeasured} without observed start`
          : "final attempt, measured installs only",
    },
    {
      label: "P95 install time",
      value: formatDuration(s.p95DurationSeconds),
      tone: "default",
      hint: "final attempt, measured installs only",
    },
    {
      label: "Trend",
      value: trendText(s.trend, s.trendDelta),
      tone: "default",
      hint: s.trendDelta != null && s.trend !== "stable" ? s.trend : undefined,
    },
    {
      label: "Flakiness",
      value: `${(s.flakinessScore * 100).toFixed(0)}%`,
      tone: "default",
      hint: "% of installs with retries",
    },
  ];

  const failureCodeRows = analytics.topFailureCodes.slice(0, MAX_FAILURE_CODE_ROWS).map((row) => {
    const entry = getErrorCodeEntry(row.code);
    const exitEntry = row.exitCode != null ? getErrorCodeEntry(row.exitCode) : null;
    // Full description on purpose — the PDF table wraps cells, and the exit-code
    // text is exactly what the app owner needs to act on.
    const exitDisplay =
      row.exitCode != null
        ? exitEntry
          ? `${row.exitCode} (${exitEntry.description})`
          : String(row.exitCode)
        : "—";
    return [
      formatErrorCode(row.code),
      entry ? entry.description : "Unknown code",
      exitDisplay,
      String(row.count),
    ];
  });

  const deviceModelRows = [...analytics.deviceModelBreakdown]
    .sort((a, b) => b.liftVsBaseline - a.liftVsBaseline || b.installs - a.installs)
    .slice(0, MAX_DEVICE_MODEL_ROWS)
    .map((row) => [
      row.manufacturer,
      row.model,
      String(row.installs),
      String(row.failed),
      `${row.failureRate.toFixed(1)}%`,
      `${row.liftVsBaseline.toFixed(2)}x`,
    ]);

  const versionRows = analytics.versionBreakdown.slice(0, MAX_VERSION_ROWS).map((row) => [
    row.appVersion,
    String(row.installs),
    String(row.failed),
    `${row.failureRate.toFixed(1)}%`,
    row.measuredInstalls > 0 ? formatDuration(row.medianDurationSeconds) : "—",
    row.measuredInstalls > 0 ? formatDuration(row.p95DurationSeconds) : "—",
  ]);

  const regressionLines = (analytics.versionRegressions ?? [])
    .slice(0, MAX_REGRESSION_LINES)
    .map(
      (reg) =>
        `Duration regression: median install duration rose from ${(reg.previousMedianSeconds / 60).toFixed(1)} to ` +
        `${(reg.currentMedianSeconds / 60).toFixed(1)} min after version ${reg.currentVersion} ` +
        `(${reg.currentMeasuredCount} measured installs vs ${reg.previousMeasuredCount} on ${reg.previousVersion} — lift ${reg.lift}x)`
    );

  const detectionLiesText =
    analytics.detectionLiesCount > 0
      ? `${analytics.detectionLiesCount} install${analytics.detectionLiesCount === 1 ? " was" : "s were"} ` +
        "reported as Succeeded but the detection rule did not find the app afterwards. Check the detection rule."
      : null;

  return {
    appName,
    appType: analytics.appType,
    metaLine:
      `${analytics.windowDays} day window · ${analytics.bucket === "day" ? "daily" : "weekly"} buckets · ` +
      `${input.scopeLabel} · Generated ${formatReportDate(generatedAt)}`,
    fileName: appReportFileName(appName, days, generatedAt),
    kpis,
    detectionLiesText,
    regressionLines,
    bucket: analytics.bucket,
    timeSeries: analytics.timeSeries,
    failureCodes: {
      rows: failureCodeRows,
      moreCount: Math.max(0, analytics.topFailureCodes.length - MAX_FAILURE_CODE_ROWS),
    },
    deviceModels: {
      rows: deviceModelRows,
      moreCount: Math.max(0, analytics.deviceModelBreakdown.length - MAX_DEVICE_MODEL_ROWS),
    },
    versions: {
      rows: versionRows,
      moreCount: Math.max(0, analytics.versionBreakdown.length - MAX_VERSION_ROWS),
    },
  };
}
