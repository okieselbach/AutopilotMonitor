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

/** A company-form suffix as Windows reports it after a comma ("VMware, Inc."). */
const COMPANY_SUFFIX = /^(inc|ltd|llc|corp|co|gmbh|ag|sa|s\.a|bv|b\.v|plc|pty|oy|ab|srl|s\.r\.l|limited|incorporated|corporation)\.?$/i;

/**
 * Splits what an admin typed into the editor: commas separate entries ("Dell*, HP*"), except
 * before a company-form suffix, which belongs to the name — "VMware, Inc." is one manufacturer
 * and is kept as one pattern that matches it literally ("VMware? Inc."), not as "VMware" plus
 * "Inc.", which would never match anything.
 */
export function splitTypedWhitelistInput(text: string): string[] {
  const entries: string[] = [];
  for (const part of text.split(",").map((s) => s.trim())) {
    if (!part) continue;
    if (entries.length > 0 && COMPANY_SUFFIX.test(part)) {
      entries[entries.length - 1] = toWhitelistEntry(`${entries[entries.length - 1]}, ${part}`);
    } else {
      entries.push(part);
    }
  }
  return entries;
}

/**
 * The pattern a manufacturer or model from a device report may become, or null. Reports reach
 * the backend unauthenticated, and the matcher has no escape for its wildcards ('*' any run, '?'
 * one character): a reported "*" saved as an entry would admit every device. No real SMBIOS
 * value contains either character, so such a value is never offered as an addition at all.
 */
export function reportedValueEntry(value: string | null | undefined): string | null {
  if (!value || /[*?]/.test(value)) return null;
  return toWhitelistEntry(value) || null;
}

/** Appends `value` as one pattern unless an equal (case-insensitive) entry already exists. */
export function addWhitelistEntry(csv: string, value: string): string {
  const entry = toWhitelistEntry(value);
  if (!entry) return csv;
  const items = parseList(csv);
  if (items.some((i) => i.toLowerCase() === entry.toLowerCase())) return csv;
  return joinList([...items, entry]);
}
