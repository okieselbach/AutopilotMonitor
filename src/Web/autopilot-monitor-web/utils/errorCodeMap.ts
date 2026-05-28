/**
 * Wrapper around the shared error-codes catalog.
 *
 * The catalog lives at `src/Shared/AutopilotMonitor.Shared/Resources/error-codes.json`
 * and is synced into `utils/error-codes.json` at build time (see `scripts/sync-error-codes.js`,
 * wired into the `prebuild` npm script). Backend and web load from the same source.
 *
 * Public API (`ErrorCodeConfidence`, `ErrorCodeEntry`, `getErrorCodeDescription`,
 * `getErrorCodeEntry`, `formatErrorCode`) is unchanged for callers.
 */

import catalogFile from "./error-codes.json";

/**
 * Confidence level for an error code mapping.
 *  - "high"   — Documented by Microsoft (MS Learn, official docs)
 *  - "medium" — Community-confirmed (MVP blogs, Q&A forums, consistent field reports)
 *  - "low"    — Inferred or rarely seen, may not be accurate in all contexts
 */
export type ErrorCodeConfidence = "high" | "medium" | "low";

export interface ErrorCodeEntry {
  description: string;
  confidence: ErrorCodeConfidence;
  source: string;
}

interface CatalogFile {
  schemaVersion: number;
  description?: string;
  entries: Record<string, ErrorCodeEntry>;
}

const typedCatalog = catalogFile as CatalogFile;

/**
 * Mapping of known Windows / MSI / Intune error codes to structured entries.
 * Keys are normalised lowercase hex strings (e.g. "0x80070005") or decimal strings (e.g. "1603").
 */
const errorCodeMap: Record<string, ErrorCodeEntry> = Object.fromEntries(
  Object.entries(typedCatalog.entries).map(([k, v]) => [k.toLowerCase(), v])
);

/**
 * Look up a structured error code entry for a Windows / MSI / Intune error code.
 * Returns null when no mapping is found.
 */
function lookupEntry(code: string | number | null | undefined): ErrorCodeEntry | null {
  if (code == null) return null;
  const raw = String(code).trim();
  if (raw === "") return null;

  // 1) Direct lookup (handles decimal keys like "1603" and already-lowered hex)
  const direct = errorCodeMap[raw.toLowerCase()];
  if (direct) return direct;

  // 2) Hex input normalisation ("0X..." → "0x...")
  if (/^0x/i.test(raw)) {
    const normalised = "0x" + raw.slice(2).toLowerCase().replace(/^0+/, "")
      .padStart(8, "0");
    const found = errorCodeMap[normalised];
    if (found) return found;
  }

  // 3) Signed-decimal HRESULT → unsigned hex  (e.g. -2147024891 → 0x80070005)
  const num = parseInt(raw, 10);
  if (!isNaN(num) && num < 0) {
    const hex = "0x" + (num >>> 0).toString(16).padStart(8, "0");
    const found = errorCodeMap[hex];
    if (found) return found;
  }

  return null;
}

/**
 * Look up a human-readable description for a Windows / MSI / Intune error code.
 *
 * Accepts:
 *  - Decimal strings:       "1603", "0"
 *  - Hex strings:           "0x80070005", "0X80070005"
 *  - Signed-decimal HRESULT: "-2147024891"  (converted to unsigned hex internally)
 *
 * Returns null when no mapping is found.
 */
export function getErrorCodeDescription(code: string | number | null | undefined): string | null {
  return lookupEntry(code)?.description ?? null;
}

/**
 * Look up the full structured entry (description + confidence + source) for an error code.
 * Returns null when no mapping is found.
 */
export function getErrorCodeEntry(code: string | number | null | undefined): ErrorCodeEntry | null {
  return lookupEntry(code);
}

/**
 * Format a raw numeric error code for display.
 * Signed-decimal HRESULTs are converted to hex notation.
 * Decimal exit codes stay as-is.
 */
export function formatErrorCode(code: string | number): string {
  const raw = String(code).trim();
  const num = parseInt(raw, 10);

  // Already hex
  if (/^0x/i.test(raw)) return raw.toUpperCase();

  // Negative signed-decimal → hex
  if (!isNaN(num) && num < 0) {
    return "0x" + (num >>> 0).toString(16).toUpperCase();
  }

  // Positive decimal (exit code) — keep as-is
  return raw;
}
