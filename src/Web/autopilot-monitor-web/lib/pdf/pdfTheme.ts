// Print palette + A4 layout constants for generated PDF reports.
//
// Deliberately separate from components/charts/chartTheme.ts: those colors are
// tuned for dark on-screen surfaces (gray-400 axis text, #374151 grid) and look
// washed out on white paper. PDFs always render on a fixed light background,
// independent of the user's on-screen theme.

export const pdfColors = {
  text: "#111827",
  subtle: "#6b7280",
  faint: "#9ca3af",
  border: "#e5e7eb",
  headerBand: "#f3f4f6",
  zebra: "#f9fafb",
  brand: "#16a34a",
  success: "#059669",
  danger: "#dc2626",
  warning: "#b45309",
  warningBg: "#fffbeb",
  warningAccent: "#f59e0b",
  primary: "#2563eb",
  badgeBg: "#dbeafe",
  badgeText: "#1e40af",
} as const;

/** A4 portrait geometry in millimetres. */
export const pdfPage = {
  w: 210,
  h: 297,
  margin: 15,
  /** Height reserved under the top margin for the repeating page header band. */
  headerH: 12,
  /** Height reserved above the bottom margin for the page footer. */
  footerH: 10,
} as const;

/** Usable content width between the side margins. */
export const contentW = pdfPage.w - 2 * pdfPage.margin;

/** First y a content block may start at (below the page header). */
export const contentTop = pdfPage.margin + pdfPage.headerH;

/** Last y a content block may extend to (above the footer). */
export const contentBottom = pdfPage.h - pdfPage.margin - pdfPage.footerH;
