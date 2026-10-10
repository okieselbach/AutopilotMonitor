// jsPDF renderers for the two report charts, drawn directly from timeSeries
// data with a print-friendly light palette (no DOM capture, theme-independent).

import type { jsPDF } from "jspdf";
import { pdfColors } from "./pdfTheme";
import { niceTicks, linearScale, tickIndices, formatBucketLabel } from "./chartMath";
import type { ReportTimeSeriesPoint } from "./appReportData";

export interface ChartRegion {
  x: number;
  y: number;
  w: number;
  h: number;
}

const Y_LABEL_W = 12;
const X_LABEL_H = 6;
const LEGEND_H = 5;
const MAX_X_LABELS = 10;

interface PlotArea {
  x: number;
  y: number;
  w: number;
  h: number;
}

function plotArea(region: ChartRegion): PlotArea {
  return {
    x: region.x + Y_LABEL_W,
    y: region.y + LEGEND_H,
    w: region.w - Y_LABEL_W,
    h: region.h - LEGEND_H - X_LABEL_H,
  };
}

function drawEmptyPlaceholder(doc: jsPDF, region: ChartRegion) {
  doc.setDrawColor(pdfColors.border);
  doc.setLineWidth(0.3);
  doc.roundedRect(region.x, region.y, region.w, region.h, 1, 1, "S");
  doc.setFont("helvetica", "normal");
  doc.setFontSize(9);
  doc.setTextColor(pdfColors.faint);
  doc.text("No data in this window", region.x + region.w / 2, region.y + region.h / 2, {
    align: "center",
  });
}

/** Gridlines + y tick labels + x bucket labels shared by both charts. */
function drawAxes(
  doc: jsPDF,
  plot: PlotArea,
  yTicks: number[],
  yMax: number,
  yUnit: string,
  points: ReportTimeSeriesPoint[]
) {
  const yScale = linearScale(yMax, plot.h);
  doc.setFont("helvetica", "normal");
  doc.setFontSize(6.5);
  for (const tick of yTicks) {
    const y = plot.y + plot.h - yScale(tick);
    doc.setDrawColor(pdfColors.border);
    doc.setLineWidth(0.1);
    doc.line(plot.x, y, plot.x + plot.w, y);
    doc.setTextColor(pdfColors.subtle);
    doc.text(`${tick}${yUnit}`, plot.x - 1.5, y + 1, { align: "right" });
  }
  // Baseline slightly stronger than the gridlines.
  doc.setDrawColor(pdfColors.subtle);
  doc.setLineWidth(0.2);
  doc.line(plot.x, plot.y + plot.h, plot.x + plot.w, plot.y + plot.h);

  const n = points.length;
  const slotW = plot.w / n;
  doc.setTextColor(pdfColors.subtle);
  for (const i of tickIndices(n, MAX_X_LABELS)) {
    const cx = plot.x + slotW * (i + 0.5);
    doc.text(formatBucketLabel(points[i].bucketStart), cx, plot.y + plot.h + 4, {
      align: "center",
    });
  }
}

function drawLegend(
  doc: jsPDF,
  region: ChartRegion,
  entries: Array<{ label: string; color: string }>
) {
  doc.setFont("helvetica", "normal");
  doc.setFontSize(6.5);
  let x = region.x + region.w;
  // Right-aligned: lay the entries out from the right edge backwards.
  for (const entry of [...entries].reverse()) {
    const labelW = doc.getTextWidth(entry.label);
    x -= labelW;
    doc.setTextColor(pdfColors.subtle);
    doc.text(entry.label, x, region.y + 3);
    x -= 3.5;
    doc.setFillColor(entry.color);
    doc.rect(x, region.y + 0.8, 2.5, 2.5, "F");
    x -= 5;
  }
}

/** "Installs over time": succeeded (green) with failed (red) stacked on top. */
export function drawStackedBarChart(doc: jsPDF, region: ChartRegion, points: ReportTimeSeriesPoint[]) {
  if (points.length === 0) {
    drawEmptyPlaceholder(doc, region);
    return;
  }
  const plot = plotArea(region);
  const { max, ticks } = niceTicks(Math.max(...points.map((p) => p.succeeded + p.failed)));
  drawLegend(doc, region, [
    { label: "Succeeded", color: pdfColors.success },
    { label: "Failed", color: pdfColors.danger },
  ]);
  drawAxes(doc, plot, ticks, max, "", points);
  const yScale = linearScale(max, plot.h);
  const slotW = plot.w / points.length;
  const barW = Math.max(0.4, slotW * 0.7);
  points.forEach((p, i) => {
    const x = plot.x + slotW * i + (slotW - barW) / 2;
    const succeededH = yScale(p.succeeded);
    const failedH = yScale(p.failed);
    if (succeededH > 0) {
      doc.setFillColor(pdfColors.success);
      doc.rect(x, plot.y + plot.h - succeededH, barW, succeededH, "F");
    }
    if (failedH > 0) {
      doc.setFillColor(pdfColors.danger);
      doc.rect(x, plot.y + plot.h - succeededH - failedH, barW, failedH, "F");
    }
  });
}

/** "Avg Install Time over time" (final attempt): blue polyline, dot markers when sparse. */
export function drawLineChart(doc: jsPDF, region: ChartRegion, points: ReportTimeSeriesPoint[]) {
  if (points.length === 0) {
    drawEmptyPlaceholder(doc, region);
    return;
  }
  const plot = plotArea(region);
  const { max, ticks } = niceTicks(Math.max(...points.map((p) => p.avgDurationSeconds)));
  drawLegend(doc, region, [{ label: "Avg install time", color: pdfColors.primary }]);
  drawAxes(doc, plot, ticks, max, "s", points);
  const yScale = linearScale(max, plot.h);
  const slotW = plot.w / points.length;
  const coords = points.map((p, i) => ({
    x: plot.x + slotW * (i + 0.5),
    y: plot.y + plot.h - yScale(p.avgDurationSeconds),
  }));
  doc.setDrawColor(pdfColors.primary);
  doc.setLineWidth(0.5);
  for (let i = 1; i < coords.length; i++) {
    doc.line(coords[i - 1].x, coords[i - 1].y, coords[i].x, coords[i].y);
  }
  if (points.length <= 31) {
    doc.setFillColor(pdfColors.primary);
    for (const c of coords) {
      doc.circle(c.x, c.y, 0.7, "F");
    }
  }
}
