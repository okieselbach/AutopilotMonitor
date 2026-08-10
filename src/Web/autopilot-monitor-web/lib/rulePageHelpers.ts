/**
 * Shared utilities for rule/pattern management pages.
 */

/** Trigger a browser download of `data` serialised as pretty-printed JSON. */
export function downloadAsJson(data: unknown, filename: string) {
  const json = JSON.stringify(data, null, 2);
  const blob = new Blob([json], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

/**
 * Return a shallow copy of `item` with internal / metadata fields removed.
 * Works generically for AnalyzeRule, GatherRule, and ImeLogPattern objects.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function stripInternalFields<T extends { isBuiltIn?: any; isCommunity?: any; createdAt?: any; updatedAt?: any }>(item: T): Omit<T, "isBuiltIn" | "isCommunity" | "createdAt" | "updatedAt"> {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { isBuiltIn, isCommunity, createdAt, updatedAt, ...rest } = item;
  return rest as Omit<T, "isBuiltIn" | "isCommunity" | "createdAt" | "updatedAt">;
}

/**
 * Bump a semver-style version string's minor segment, preserving the segment
 * count and zeroing anything after the minor: "1.0" -> "1.1", "1.9" -> "1.10",
 * "1.0.0" -> "1.1.0", "2.3.7" -> "2.4.0".
 */
export function bumpVersion(v: string): string {
  const parts = (v || "1.0").split(".");
  const major = parts[0] || "1";
  const parsedMinor = parseInt(parts[1] ?? "0", 10);
  const minor = Number.isNaN(parsedMinor) ? 0 : parsedMinor;
  const trailingZeros = parts.slice(2).map(() => "0");
  return [major, `${minor + 1}`, ...trailingZeros].join(".");
}
