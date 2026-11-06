/** Compact ("yyyyMMdd", the MCP usage counters) and dashed ("yyyy-MM-dd", growth and tenant activity) UTC day keys. */
const COMPACT_DAY = /^(\d{4})(\d{2})(\d{2})$/;
const DASHED_DAY = /^(\d{4})-(\d{2})-(\d{2})$/;

/**
 * A UTC calendar day key in the viewer's locale, never shifted by the viewer's time zone. Takes both key forms the
 * GA metrics routes send; anything else comes back unchanged.
 */
export function formatDayKey(key: string, options: Intl.DateTimeFormatOptions = { day: "numeric", month: "short" }): string {
  const match = COMPACT_DAY.exec(key) ?? DASHED_DAY.exec(key);
  if (!match) return key;
  return new Date(`${match[1]}-${match[2]}-${match[3]}T00:00:00Z`).toLocaleDateString(undefined, { ...options, timeZone: "UTC" });
}
