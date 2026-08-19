// Generic report chrome for jsPDF-generated documents: page header/footer,
// section titles, KPI rows, callout boxes, and a simple fixed-column table.
// App-agnostic on purpose — future report exports (SLA, session report) can
// reuse this module unchanged.
//
// Type-only jsPDF import: the runtime jsPDF instance is created by the caller
// (inside the lazily loaded report builder), so this file adds no bundle weight
// to anything that imports it statically.

import type { jsPDF } from "jspdf";
import { pdfColors, pdfPage, contentW, contentTop, contentBottom } from "./pdfTheme";

export interface PdfContext {
  doc: jsPDF;
  /** Current write cursor (top of the next content block), in mm. */
  y: number;
  /** Report title repeated in the header band of every page. */
  reportTitle: string;
}

export interface TableColumn {
  header: string;
  /** Column width in mm; widths should sum to contentW. */
  width: number;
  align?: "left" | "right";
}

export interface TableSpec {
  columns: TableColumn[];
  rows: string[][];
  /** Trailing italic "+N more …" note when the source data was truncated. */
  moreCount?: number;
  zebra?: boolean;
}

function drawPageHeader(doc: jsPDF, reportTitle: string) {
  const y = pdfPage.margin + 4;
  doc.setFont("helvetica", "bold");
  doc.setFontSize(10);
  doc.setTextColor(pdfColors.brand);
  doc.text("Autopilot Monitor", pdfPage.margin, y);
  doc.setFont("helvetica", "normal");
  doc.setFontSize(9);
  doc.setTextColor(pdfColors.subtle);
  doc.text(reportTitle, pdfPage.w - pdfPage.margin, y, { align: "right" });
  doc.setDrawColor(pdfColors.border);
  doc.setLineWidth(0.3);
  doc.line(pdfPage.margin, y + 3, pdfPage.w - pdfPage.margin, y + 3);
}

/** Start a new report document: draws the first page's header and returns the cursor context. */
export function startReport(doc: jsPDF, reportTitle: string): PdfContext {
  drawPageHeader(doc, reportTitle);
  return { doc, y: contentTop + 2, reportTitle };
}

/** Page-break guard: if the next block of `needed` mm doesn't fit, start a new page. */
export function ensureSpace(ctx: PdfContext, needed: number) {
  if (ctx.y + needed <= contentBottom) return;
  ctx.doc.addPage();
  drawPageHeader(ctx.doc, ctx.reportTitle);
  ctx.y = contentTop + 2;
}

export function sectionTitle(ctx: PdfContext, text: string) {
  ensureSpace(ctx, 14);
  ctx.y += 3;
  ctx.doc.setFont("helvetica", "bold");
  ctx.doc.setFontSize(11);
  ctx.doc.setTextColor(pdfColors.text);
  ctx.doc.text(text, pdfPage.margin, ctx.y + 4);
  ctx.y += 8;
}

/** Truncate text with an ellipsis so it fits maxWidth at the doc's CURRENT font settings. */
export function truncateToWidth(doc: jsPDF, text: string, maxWidth: number): string {
  if (doc.getTextWidth(text) <= maxWidth) return text;
  let t = text;
  while (t.length > 1 && doc.getTextWidth(`${t}…`) > maxWidth) {
    t = t.slice(0, -1);
  }
  return `${t}…`;
}

export interface KpiCell {
  label: string;
  value: string;
  /** Resolved hex color for the value text. */
  valueColor: string;
  hint?: string;
}

const KPI_CELL_H = 17;
const KPI_GAP = 3;

/** One row of bordered KPI cells, `perRow` across the content width. */
export function kpiRow(ctx: PdfContext, kpis: KpiCell[], perRow = 4) {
  const rows = Math.ceil(kpis.length / perRow);
  ensureSpace(ctx, rows * (KPI_CELL_H + KPI_GAP));
  const cellW = (contentW - (perRow - 1) * KPI_GAP) / perRow;
  kpis.forEach((kpi, i) => {
    const col = i % perRow;
    const row = Math.floor(i / perRow);
    const x = pdfPage.margin + col * (cellW + KPI_GAP);
    const y = ctx.y + row * (KPI_CELL_H + KPI_GAP);
    ctx.doc.setDrawColor(pdfColors.border);
    ctx.doc.setLineWidth(0.3);
    ctx.doc.roundedRect(x, y, cellW, KPI_CELL_H, 1, 1, "S");
    ctx.doc.setFont("helvetica", "normal");
    ctx.doc.setFontSize(6.5);
    ctx.doc.setTextColor(pdfColors.subtle);
    ctx.doc.text(truncateToWidth(ctx.doc, kpi.label.toUpperCase(), cellW - 6), x + 3, y + 5);
    ctx.doc.setFont("helvetica", "bold");
    ctx.doc.setFontSize(12);
    ctx.doc.setTextColor(kpi.valueColor);
    ctx.doc.text(truncateToWidth(ctx.doc, kpi.value, cellW - 6), x + 3, y + 11);
    if (kpi.hint) {
      ctx.doc.setFont("helvetica", "normal");
      ctx.doc.setFontSize(6);
      ctx.doc.setTextColor(pdfColors.faint);
      ctx.doc.text(truncateToWidth(ctx.doc, kpi.hint, cellW - 6), x + 3, y + 15);
    }
  });
  ctx.y += rows * (KPI_CELL_H + KPI_GAP);
}

/** Amber warning callout with a left accent bar; wraps the text to the content width. */
export function calloutBox(ctx: PdfContext, text: string) {
  ctx.doc.setFont("helvetica", "normal");
  ctx.doc.setFontSize(8.5);
  const lines = ctx.doc.splitTextToSize(text, contentW - 10) as string[];
  const boxH = lines.length * 4 + 6;
  ensureSpace(ctx, boxH + 3);
  ctx.doc.setFillColor(pdfColors.warningBg);
  ctx.doc.roundedRect(pdfPage.margin, ctx.y, contentW, boxH, 1, 1, "F");
  ctx.doc.setFillColor(pdfColors.warningAccent);
  ctx.doc.rect(pdfPage.margin, ctx.y, 1.2, boxH, "F");
  ctx.doc.setTextColor(pdfColors.warning);
  ctx.doc.text(lines, pdfPage.margin + 5, ctx.y + 5);
  ctx.y += boxH + 3;
}

const TABLE_HEADER_H = 7;
const TABLE_ROW_H = 6;

function drawTableHeader(ctx: PdfContext, columns: TableColumn[]) {
  ctx.doc.setFillColor(pdfColors.headerBand);
  ctx.doc.rect(pdfPage.margin, ctx.y, contentW, TABLE_HEADER_H, "F");
  ctx.doc.setFont("helvetica", "bold");
  ctx.doc.setFontSize(7);
  ctx.doc.setTextColor(pdfColors.subtle);
  let x = pdfPage.margin;
  for (const col of columns) {
    const tx = col.align === "right" ? x + col.width - 2 : x + 2;
    ctx.doc.text(col.header.toUpperCase(), tx, ctx.y + 4.7, {
      align: col.align === "right" ? "right" : "left",
    });
    x += col.width;
  }
  ctx.y += TABLE_HEADER_H;
}

/** Fixed-column table with zebra striping and header repetition after page breaks. */
export function drawTable(ctx: PdfContext, spec: TableSpec) {
  const { columns, rows, moreCount = 0, zebra = true } = spec;
  ensureSpace(ctx, TABLE_HEADER_H + TABLE_ROW_H * 2);
  drawTableHeader(ctx, columns);
  rows.forEach((row, rowIndex) => {
    // Re-draw the header when the row would spill past the footer.
    if (ctx.y + TABLE_ROW_H > contentBottom) {
      ensureSpace(ctx, TABLE_HEADER_H + TABLE_ROW_H);
      drawTableHeader(ctx, columns);
    }
    if (zebra && rowIndex % 2 === 1) {
      ctx.doc.setFillColor(pdfColors.zebra);
      ctx.doc.rect(pdfPage.margin, ctx.y, contentW, TABLE_ROW_H, "F");
    }
    ctx.doc.setFont("helvetica", "normal");
    ctx.doc.setFontSize(8);
    ctx.doc.setTextColor(pdfColors.text);
    let x = pdfPage.margin;
    row.forEach((cell, i) => {
      const col = columns[i];
      if (!col) return;
      const text = truncateToWidth(ctx.doc, cell, col.width - 4);
      const tx = col.align === "right" ? x + col.width - 2 : x + 2;
      ctx.doc.text(text, tx, ctx.y + 4.2, { align: col.align === "right" ? "right" : "left" });
      x += col.width;
    });
    ctx.doc.setDrawColor(pdfColors.border);
    ctx.doc.setLineWidth(0.1);
    ctx.doc.line(pdfPage.margin, ctx.y + TABLE_ROW_H, pdfPage.margin + contentW, ctx.y + TABLE_ROW_H);
    ctx.y += TABLE_ROW_H;
  });
  if (moreCount > 0) {
    ensureSpace(ctx, TABLE_ROW_H);
    ctx.doc.setFont("helvetica", "italic");
    ctx.doc.setFontSize(7.5);
    ctx.doc.setTextColor(pdfColors.faint);
    ctx.doc.text(`+${moreCount} more not shown`, pdfPage.margin + 2, ctx.y + 4);
    ctx.y += TABLE_ROW_H;
  }
  ctx.y += 2;
}

/** Stamp the footer on every page — call once, after all content is placed. */
export function finalizeFooters(doc: jsPDF, generatedLabel: string) {
  const total = doc.getNumberOfPages();
  const y = pdfPage.h - pdfPage.margin;
  for (let p = 1; p <= total; p++) {
    doc.setPage(p);
    doc.setFont("helvetica", "normal");
    doc.setFontSize(7);
    doc.setTextColor(pdfColors.faint);
    doc.text(generatedLabel, pdfPage.margin, y);
    doc.text(`Page ${p} of ${total}`, pdfPage.w - pdfPage.margin, y, { align: "right" });
  }
}
