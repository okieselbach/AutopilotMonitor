/**
 * The status page's history-retention field: parsing what the person typed and wording the
 * stored value. Pure, so lib/__tests__/retentionInput.test.ts pins both. The value itself lives
 * in IndexedDB meta (pushCore readRetentionDays / writeRetentionDays) and never leaves the device.
 */
import { HISTORY_MAX_ENTRIES, HISTORY_RETENTION_MAX_DAYS } from "@/lib/push/pushCore";

export type RetentionInputResult = { ok: true; days: number } | { ok: false; message: string };

const BLANK_MESSAGE = "Enter a number of days.";
const RANGE_MESSAGE = `Enter a whole number of days between 0 and ${HISTORY_RETENTION_MAX_DAYS}.`;

/** Trims, then accepts only an integer 0–365: digits alone, no sign, no decimals, no unit. */
export function parseRetentionInput(text: string): RetentionInputResult {
  const trimmed = text.trim();
  if (trimmed === "") return { ok: false, message: BLANK_MESSAGE };
  if (!/^[0-9]+$/.test(trimmed)) return { ok: false, message: RANGE_MESSAGE };
  const days = Number(trimmed);
  if (days > HISTORY_RETENTION_MAX_DAYS) return { ok: false, message: RANGE_MESSAGE };
  return { ok: true, days };
}

/** "30 days", "1 day", or for 0 "Until the 200-entry cap" (the entry cap applies either way). */
export function describeRetention(days: number): string {
  if (days === 0) return `Until the ${HISTORY_MAX_ENTRIES}-entry cap`;
  return days === 1 ? "1 day" : `${days} days`;
}
