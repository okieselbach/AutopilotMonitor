import type { GlobalAverages, LocationMetrics } from "@/utils/wire-types.generated";

/**
 * Single source of truth for how a location is colour-coded on the Geographic Performance page.
 *
 * Every mode is consumed twice: the Leaflet map paints markers with `hex`, the Location Performance
 * table paints its badges with `className`. Keeping both on one bucket definition is what guarantees
 * that the map, its legend and the table can never disagree about a threshold. `resolve` must return
 * one of the mode's own `buckets` by identity — the legend is rendered from that same array.
 *
 * Adding a mode = one entry in MAP_COLOR_MODE_BY_ID; the select, the legend and the tests pick it up.
 * Class strings must stay literal (Tailwind scans this file via the app/**\/*.ts content glob).
 */

export type MapColorModeId = "duration" | "success" | "latency" | "do" | "score";

export interface ColorBucket {
  /** Legend text, e.g. "≤ 80% of global". */
  label: string;
  /** Marker fill/stroke and legend swatch. */
  hex: string;
  /** Table badge classes (literal Tailwind). */
  className: string;
}

export interface MapColorMode {
  id: MapColorModeId;
  /** Select option text and legend heading. */
  label: string;
  /** Legend order, best first, "no data" last. */
  buckets: readonly ColorBucket[];
  /** Pure; always returns an element of `buckets`. */
  resolve: (loc: LocationMetrics, global: GlobalAverages) => ColorBucket;
}

// Existing palette families only (green/yellow/orange/red/gray): no new colour families in the web app.
const HEX = {
  green: "#059669", // green-600
  greenLight: "#10B981", // green-500
  yellow: "#F59E0B", // yellow-500
  orange: "#F97316", // orange-500
  red: "#EF4444", // red-500
  neutral: "#4B5563", // gray-600 — a real value in the "middle" band
  noData: "#9CA3AF", // gray-400 — nothing to measure
} as const;

const NO_DATA_CLASS = "bg-gray-100 text-gray-500";

const noData = (label: string): ColorBucket => ({ label, hex: HEX.noData, className: NO_DATA_CLASS });

// ---- Duration vs. global average ------------------------------------------------------------

const DURATION_BUCKETS = [
  { label: "≤ 80% of global avg", hex: HEX.green, className: "bg-green-100 text-green-800" },
  { label: "≤ 100%", hex: HEX.greenLight, className: "bg-green-50 text-green-700" },
  { label: "≤ 120%", hex: HEX.yellow, className: "bg-yellow-50 text-yellow-700" },
  { label: "≤ 150%", hex: HEX.orange, className: "bg-orange-50 text-orange-700" },
  { label: "> 150%", hex: HEX.red, className: "bg-red-100 text-red-800" },
  noData("No global average yet"),
] as const satisfies readonly ColorBucket[];

const duration: MapColorMode = {
  id: "duration",
  label: "Enrollment duration",
  buckets: DURATION_BUCKETS,
  resolve: (loc, global) => {
    if (global.avgDurationMinutes <= 0) return DURATION_BUCKETS[5];
    const ratio = loc.avgDurationMinutes / global.avgDurationMinutes;
    if (ratio <= 0.8) return DURATION_BUCKETS[0];
    if (ratio <= 1.0) return DURATION_BUCKETS[1];
    if (ratio <= 1.2) return DURATION_BUCKETS[2];
    if (ratio <= 1.5) return DURATION_BUCKETS[3];
    return DURATION_BUCKETS[4];
  },
};

// ---- Success rate over finished enrollments ------------------------------------------------

const SUCCESS_BUCKETS = [
  { label: "≥ 90% succeeded", hex: HEX.green, className: "bg-green-100 text-green-800" },
  { label: "≥ 70%", hex: HEX.yellow, className: "bg-yellow-100 text-yellow-800" },
  { label: "< 70%", hex: HEX.red, className: "bg-red-100 text-red-800" },
  noData("No finished enrollments"),
] as const satisfies readonly ColorBucket[];

const success: MapColorMode = {
  id: "success",
  label: "Success rate",
  buckets: SUCCESS_BUCKETS,
  resolve: (loc) => {
    // Rate is over finished enrollments (succeeded + failed) only; a location where everything
    // is still in flight has no rate yet.
    if (loc.succeeded + loc.failed <= 0) return SUCCESS_BUCKETS[3];
    if (loc.successRate >= 90) return SUCCESS_BUCKETS[0];
    if (loc.successRate >= 70) return SUCCESS_BUCKETS[1];
    return SUCCESS_BUCKETS[2];
  },
};

// ---- Agent→backend API latency (absolute) --------------------------------------------------

// Absolute buckets, not relative-to-global: latency encodes physical distance to the backend
// region, and the decision it supports ("open a closer region?") needs absolute thresholds.
const LATENCY_BUCKETS = [
  { label: "< 250 ms", hex: HEX.green, className: "bg-green-100 text-green-800" },
  { label: "< 500 ms", hex: HEX.yellow, className: "bg-yellow-100 text-yellow-800" },
  { label: "< 800 ms", hex: HEX.orange, className: "bg-orange-100 text-orange-700" },
  { label: "≥ 800 ms", hex: HEX.red, className: "bg-red-100 text-red-800" },
  noData("No latency data"),
] as const satisfies readonly ColorBucket[];

const latency: MapColorMode = {
  id: "latency",
  label: "API latency",
  buckets: LATENCY_BUCKETS,
  resolve: (loc) => {
    // Median per location — robust against a single corrupt session average.
    const ms = loc.medianApiLatencyMs;
    if (ms <= 0) return LATENCY_BUCKETS[4];
    if (ms < 250) return LATENCY_BUCKETS[0];
    if (ms < 500) return LATENCY_BUCKETS[1];
    if (ms < 800) return LATENCY_BUCKETS[2];
    return LATENCY_BUCKETS[3];
  },
};

// ---- Delivery Optimization peer efficiency -------------------------------------------------

const DO_BUCKETS = [
  { label: "≥ 50% from peers", hex: HEX.green, className: "bg-green-100 text-green-800" },
  { label: "≥ 10%", hex: HEX.yellow, className: "bg-yellow-100 text-yellow-800" },
  { label: "< 10%", hex: HEX.neutral, className: "bg-gray-100 text-gray-600" },
  noData("No DO data"),
] as const satisfies readonly ColorBucket[];

const doPeer: MapColorMode = {
  id: "do",
  label: "DO peer efficiency",
  buckets: DO_BUCKETS,
  resolve: (loc) => {
    if (loc.doSessionCount <= 0) return DO_BUCKETS[3];
    if (loc.avgDoPercentPeerCaching >= 50) return DO_BUCKETS[0];
    if (loc.avgDoPercentPeerCaching >= 10) return DO_BUCKETS[1];
    return DO_BUCKETS[2];
  },
};

// ---- App-Load-Score (normalized: 100 = global median, lower is better) ----------------------

const SCORE_BUCKETS = [
  { label: "< 80 (fast)", hex: HEX.green, className: "text-green-600" },
  { label: "80–120 (around median)", hex: HEX.neutral, className: "text-gray-700" },
  { label: "> 120 (slow)", hex: HEX.red, className: "text-red-600" },
  noData("No score"),
] as const satisfies readonly ColorBucket[];

const score: MapColorMode = {
  id: "score",
  label: "App-Load-Score",
  buckets: SCORE_BUCKETS,
  resolve: (loc) => {
    if (loc.appLoadScore <= 0) return SCORE_BUCKETS[3];
    if (loc.appLoadScore < 80) return SCORE_BUCKETS[0];
    if (loc.appLoadScore <= 120) return SCORE_BUCKETS[1];
    return SCORE_BUCKETS[2];
  },
};

// ---- Registry ------------------------------------------------------------------------------

export const DEFAULT_MAP_COLOR_MODE: MapColorModeId = "duration";

/** Insertion order = select order. */
export const MAP_COLOR_MODE_BY_ID: Readonly<Record<MapColorModeId, MapColorMode>> = {
  duration,
  success,
  latency,
  do: doPeer,
  score,
};

export const MAP_COLOR_MODES: readonly MapColorMode[] = Object.values(MAP_COLOR_MODE_BY_ID);

export function isMapColorModeId(value: string): value is MapColorModeId {
  return Object.prototype.hasOwnProperty.call(MAP_COLOR_MODE_BY_ID, value);
}

export const MAP_LEGEND_NOTE =
  "Marker size = sessions in range · Blue ring = selected location · Click a bucket to filter";

// ---- Legend filter --------------------------------------------------------------------------

/**
 * Which buckets the map currently shows. Empty = no filter, every marker is drawn. Buckets are
 * compared by identity, so a set built from one mode's buckets never matches another mode's;
 * the page resets the filter on a mode switch rather than relying on that.
 */
export type BucketFilter = ReadonlySet<ColorBucket>;

export const NO_BUCKET_FILTER: BucketFilter = new Set();

/** Toggle one bucket. Removing the last selected bucket returns to "show all" (empty set). */
export function toggleBucketFilter(filter: BucketFilter, bucket: ColorBucket): BucketFilter {
  const next = new Set(filter);
  if (next.has(bucket)) next.delete(bucket);
  else next.add(bucket);
  return next.size === 0 ? NO_BUCKET_FILTER : next;
}

/** Legend dimming and marker visibility both go through here so they cannot disagree. */
export function bucketFilterAllows(filter: BucketFilter, bucket: ColorBucket): boolean {
  return filter.size === 0 || filter.has(bucket);
}

