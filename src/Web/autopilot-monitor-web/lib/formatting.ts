/**
 * Canonical formatting helpers for bytes, throughput, durations and percentages.
 *
 * Before this module these were copy-pasted ~10x across pages/components with
 * subtle divergences (decimals, zero-handling, GB-vs-TB ceiling). Centralizing
 * keeps the fleet's byte/duration strings consistent. Callers that want a dash
 * for "no data" pass it via the `zero` argument.
 */

const BYTE_UNITS = ["B", "KB", "MB", "GB", "TB"] as const;

/**
 * Human-readable byte size. 0 decimals for raw bytes, 1 decimal for KB and up
 * (e.g. "0 B", "512 B", "1.5 KB", "2.3 GB"). Non-finite or <= 0 returns `zero`.
 */
export function formatBytes(bytes: number, zero = "0 B"): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return zero;
  const i = Math.min(BYTE_UNITS.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
  const val = bytes / Math.pow(1024, i);
  return `${val.toFixed(i === 0 ? 0 : 1)} ${BYTE_UNITS[i]}`;
}

/** Transfer rate from a bytes-per-second value (e.g. "2.3 MB/s"). <= 0 returns `zero`. */
export function formatThroughput(bytesPerSec: number, zero = "—"): string {
  if (!Number.isFinite(bytesPerSec) || bytesPerSec <= 0) return zero;
  return `${formatBytes(bytesPerSec)}/s`;
}

/**
 * Duration from a SECONDS value: "Xs" (< 1 min), "Xm Ys" (< 1 h), else "Xh Ym".
 * Callers holding milliseconds should pass `ms / 1000`. <= 0 returns `zero`.
 */
export function formatDuration(seconds: number, zero = "0s"): string {
  if (!Number.isFinite(seconds) || seconds <= 0) return zero;
  const s = Math.floor(seconds);
  if (s < 60) return `${s}s`;
  const minutes = Math.floor(s / 60);
  const remSeconds = s % 60;
  if (minutes < 60) return `${minutes}m ${remSeconds}s`;
  const hours = Math.floor(minutes / 60);
  const remMinutes = minutes % 60;
  return `${hours}h ${remMinutes}m`;
}

/**
 * Percentage string from a ratio's numerator/denominator (e.g. 400/1000 -> "40%").
 * Denominator <= 0 returns `zero`. `decimals` controls precision (default 0).
 */
export function formatPercent(numerator: number, denominator: number, decimals = 0, zero = "—"): string {
  if (!Number.isFinite(numerator) || !Number.isFinite(denominator) || denominator <= 0) return zero;
  return `${((numerator / denominator) * 100).toFixed(decimals)}%`;
}

/**
 * Signed HH:MM UTC offset from a minutes value: 120 -> "UTC+02:00", -345 -> "UTC−05:45",
 * 0 -> "UTC+00:00". Quarter-hour zones (Nepal +05:45, Chatham +12:45) render exactly.
 * Non-finite returns `zero`.
 */
export function formatUtcOffset(minutes: number, zero = "—"): string {
  if (!Number.isFinite(minutes)) return zero;
  const sign = minutes < 0 ? "−" : "+";
  const abs = Math.abs(Math.round(minutes));
  const h = Math.floor(abs / 60);
  const m = abs % 60;
  return `UTC${sign}${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}
