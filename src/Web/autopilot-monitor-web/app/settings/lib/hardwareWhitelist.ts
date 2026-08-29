/**
 * Hardware whitelist CSV helpers.
 *
 * The tenant whitelist is persisted as ONE comma-separated string and split on ',' at
 * enforcement time (TenantConfiguration.GetManufacturerWhitelist). A comma inside a single
 * entry therefore changes the LIST STRUCTURE: "Dell Inc.,*" appended as one "manufacturer"
 * becomes two patterns, the second being the allow-all wildcard. Manufacturer/model strings
 * shown in the rejection insights come from UNAUTHENTICATED distress signals, so every value
 * that enters the list must round-trip as exactly one pattern.
 */

export function parseList(csv: string): string[] {
  return csv.split(",").map((s) => s.trim()).filter(Boolean);
}

export function joinList(items: string[]): string {
  return items.join(",");
}

/**
 * Normalizes a value so it is stored as exactly one whitelist pattern. The delimiter is
 * replaced by the single-character wildcard '?', which still matches the original string
 * literally (and nothing structurally different) instead of splitting the list.
 * Returns "" when nothing usable remains.
 */
export function toWhitelistEntry(value: string): string {
  return value.replace(/,/g, "?").trim();
}

/** Appends `value` as one pattern unless an equal (case-insensitive) entry already exists. */
export function addWhitelistEntry(csv: string, value: string): string {
  const entry = toWhitelistEntry(value);
  if (!entry) return csv;
  const items = parseList(csv);
  if (items.some((i) => i.toLowerCase() === entry.toLowerCase())) return csv;
  return joinList([...items, entry]);
}
