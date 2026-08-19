// App install report PDF builder — the entry point of the lazily loaded PDF
// chunk. This is the ONLY module that imports jsPDF at runtime; the page loads
// it via `await import("@/utils/pdf/appReportPdf")` on button click, so jsPDF
// never enters the route bundle.

import { jsPDF } from "jspdf";
import { pdfColors, pdfPage, contentW } from "./pdfTheme";
import {
  startReport,
  ensureSpace,
  sectionTitle,
  kpiRow,
  calloutBox,
  drawTable,
  finalizeFooters,
  truncateToWidth,
  type KpiCell,
} from "./pdfPrimitives";
import { drawStackedBarChart, drawLineChart } from "./pdfCharts";
import {
  prepareReportModel,
  formatReportDate,
  type AppReportInput,
  type AppReportModel,
  type KpiTone,
} from "./appReportData";

export interface AppReportResult {
  pageCount: number;
  fileName: string;
}

const KPI_TONE_COLORS: Record<KpiTone, string> = {
  default: pdfColors.text,
  success: pdfColors.success,
  danger: pdfColors.danger,
  warning: pdfColors.warning,
};

const BAR_CHART_H = 58;
const LINE_CHART_H = 52;

/** Generate the report and hand it to the browser as a file download. */
export function generateAppReportPdf(input: AppReportInput): AppReportResult {
  const model = prepareReportModel(input);
  const doc = buildAppReportDoc(model, input.generatedAt);
  downloadBlob(doc.output("blob"), model.fileName);
  return { pageCount: doc.getNumberOfPages(), fileName: model.fileName };
}

/** Document assembly, separated from the browser download for testability. */
export function buildAppReportDoc(model: AppReportModel, generatedAt: Date): jsPDF {
  const doc = new jsPDF({ orientation: "portrait", unit: "mm", format: "a4" });
  const ctx = startReport(doc, "App Install Report");

  // Title block: app name (wrapped) + appType badge + meta line.
  doc.setFont("helvetica", "bold");
  doc.setFontSize(16);
  doc.setTextColor(pdfColors.text);
  const nameLines = doc.splitTextToSize(model.appName, contentW - 30) as string[];
  ctx.y += 4;
  doc.text(nameLines, pdfPage.margin, ctx.y + 4);
  if (model.appType) {
    const lastLine = nameLines[nameLines.length - 1] ?? "";
    const badgeX = pdfPage.margin + doc.getTextWidth(lastLine) + 3;
    const badgeY = ctx.y + (nameLines.length - 1) * 6.5;
    doc.setFontSize(7);
    const badgeText = truncateToWidth(doc, model.appType, 24);
    const badgeW = doc.getTextWidth(badgeText) + 4;
    doc.setFillColor(pdfColors.badgeBg);
    doc.roundedRect(badgeX, badgeY, badgeW, 5, 1, 1, "F");
    doc.setTextColor(pdfColors.badgeText);
    doc.text(badgeText, badgeX + 2, badgeY + 3.5);
  }
  ctx.y += nameLines.length * 6.5 + 2;
  doc.setFont("helvetica", "normal");
  doc.setFontSize(8.5);
  doc.setTextColor(pdfColors.subtle);
  doc.text(truncateToWidth(doc, model.metaLine, contentW), pdfPage.margin, ctx.y + 3);
  ctx.y += 8;

  const kpiCells: KpiCell[] = model.kpis.map((kpi) => ({
    label: kpi.label,
    value: kpi.value,
    valueColor: KPI_TONE_COLORS[kpi.tone],
    hint: kpi.hint,
  }));
  kpiRow(ctx, kpiCells, 4);
  ctx.y += 2;

  if (model.detectionLiesText) {
    calloutBox(ctx, `Heads up: ${model.detectionLiesText}`);
  }
  for (const line of model.regressionLines) {
    calloutBox(ctx, line);
  }

  sectionTitle(ctx, "Installs over time (success vs failure)");
  ensureSpace(ctx, BAR_CHART_H + 2);
  drawStackedBarChart(doc, { x: pdfPage.margin, y: ctx.y, w: contentW, h: BAR_CHART_H }, model.timeSeries);
  ctx.y += BAR_CHART_H + 2;

  sectionTitle(ctx, "Avg Install Duration over time");
  ensureSpace(ctx, LINE_CHART_H + 2);
  drawLineChart(doc, { x: pdfPage.margin, y: ctx.y, w: contentW, h: LINE_CHART_H }, model.timeSeries);
  ctx.y += LINE_CHART_H + 2;

  sectionTitle(ctx, "Top Failure Codes");
  if (model.failureCodes.rows.length > 0) {
    drawTable(ctx, {
      columns: [
        { header: "Code", width: 30 },
        { header: "Description", width: 88 },
        { header: "Exit code", width: 44 },
        { header: "Count", width: 18, align: "right" },
      ],
      rows: model.failureCodes.rows,
      moreCount: model.failureCodes.moreCount,
    });
  } else {
    emptySectionNote(ctx, "No failures recorded in this window.");
  }

  sectionTitle(ctx, "Device Model Correlation");
  if (model.deviceModels.rows.length > 0) {
    drawTable(ctx, {
      columns: [
        { header: "Manufacturer", width: 34 },
        { header: "Model", width: 66 },
        { header: "Installs", width: 20, align: "right" },
        { header: "Failed", width: 20, align: "right" },
        { header: "Failure rate", width: 22, align: "right" },
        { header: "Lift vs base", width: 18, align: "right" },
      ],
      rows: model.deviceModels.rows,
      moreCount: model.deviceModels.moreCount,
    });
  } else {
    emptySectionNote(ctx, "Not enough installs per device model to compute correlation.");
  }

  if (model.versions.rows.length > 0) {
    sectionTitle(ctx, "Version Breakdown");
    drawTable(ctx, {
      columns: [
        { header: "Version", width: 60 },
        { header: "Installs", width: 22, align: "right" },
        { header: "Failed", width: 22, align: "right" },
        { header: "Failure rate", width: 26, align: "right" },
        { header: "Median dur.", width: 25, align: "right" },
        { header: "P95 dur.", width: 25, align: "right" },
      ],
      rows: model.versions.rows,
      moreCount: model.versions.moreCount,
    });
  }

  finalizeFooters(doc, `Generated by Autopilot Monitor · ${formatReportDate(generatedAt)}`);
  return doc;
}

function emptySectionNote(ctx: ReturnType<typeof startReport>, text: string) {
  ensureSpace(ctx, 8);
  ctx.doc.setFont("helvetica", "italic");
  ctx.doc.setFontSize(8);
  ctx.doc.setTextColor(pdfColors.faint);
  ctx.doc.text(text, pdfPage.margin + 2, ctx.y + 3);
  ctx.y += 8;
}

// Same blob-anchor pattern as every other export in the app
// (cf. app/admin/components/SessionExportSection.tsx).
function downloadBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = fileName;
  a.click();
  URL.revokeObjectURL(url);
}
