/**
 * Analytics time windows carried in the URL as `?days=`. The selector presets stay 7/30/90 for
 * everyone; a longer window (Pro keeps up to 365 days) is reached through the URL alone. Pure — the
 * React side is hooks/useWindowDays.ts.
 */

/** Query parameter that carries the window. */
export const WINDOW_DAYS_PARAM = "days";

/** The selector presets, in days. */
export const WINDOW_PRESETS = [7, 30, 90] as const;

/** Selector options over {@link WINDOW_PRESETS}, identical labels on every page. */
export const WINDOW_PRESET_OPTIONS = WINDOW_PRESETS.map((d) => ({ value: d, label: `${d} Days` }));

/** Longest window every edition may query: the Community retention cap. */
export const COMMUNITY_WINDOW_CAP_DAYS = 90;

/**
 * Longest window at all — the backend's route cap, equal to the Pro retention cap
 * (FeatureEntitlementCatalog: Pro RetentionCapDays = 365). Platform roles get it in every scope.
 */
export const PLATFORM_MAX_WINDOW_DAYS = 365;

/** `?days=` as a whole number ≥ 1; anything else ("90abc", "0", "-5", "7.5", blank) is null. */
export function parseWindowDays(raw: string | null | undefined): number | null {
  const trimmed = raw?.trim();
  if (!trimmed || !/^\d+$/.test(trimmed)) return null;
  const n = Number(trimmed);
  return Number.isSafeInteger(n) && n >= 1 ? n : null;
}

/** The window to query: the requested one within the cap, else the page default. */
export function effectiveWindowDays(requested: number | null, defaultDays: number, capDays: number): number {
  return requested == null ? defaultDays : Math.min(requested, capDays);
}

/** The UTC calendar day `days` days before `now` (yyyy-MM-dd) — the start of a date-range window. */
export function utcDateDaysAgo(days: number, now: Date = new Date()): string {
  const d = new Date(now);
  d.setUTCDate(d.getUTCDate() - days);
  return d.toISOString().slice(0, 10);
}

/**
 * Progress-estimate storage key for a window: windows beyond the Community cap keep their own
 * estimate, so a year's compute time never becomes the estimate for a week (and existing keys stay).
 */
export function windowProgressKey(baseKey: string, days: number): string {
  return days > COMMUNITY_WINDOW_CAP_DAYS ? `${baseKey}.long` : baseKey;
}
