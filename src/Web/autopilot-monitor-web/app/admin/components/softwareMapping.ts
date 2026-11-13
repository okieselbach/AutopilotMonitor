/**
 * Shared pieces of the Software Mapping tabs: the pure logic (texts that depend on a response,
 * the per-product flag, the export), kept out of the components so it is unit tested, and the
 * phone layout of a table row.
 */
import type { AutoResolveFailedItem, CpeMappingItem } from "@/lib/generated/wire-types.generated";

/**
 * Below `sm` a mapping table hides its header and column widths (`max-sm:hidden` on thead and
 * colgroup) and a row lays its cells out with these classes: they wrap, lose their table padding
 * and fixed widths. Every class carries `max-sm:`, so the desktop table is untouched.
 */
export const PHONE_ROW =
  "max-sm:flex-wrap max-sm:items-center max-sm:gap-x-3 max-sm:gap-y-1 max-sm:p-3 max-sm:*:p-0 max-sm:*:w-auto";

/** A cell that takes a line of its own on a phone and wraps instead of truncating: the name must stay readable. */
export const PHONE_FULL_LINE = "max-sm:basis-full max-sm:whitespace-normal max-sm:break-words";

const AUTO_RESOLVE_REASONS = new Map<string, string>([
  ["no_exact_product", "Not an NVD product name"],
  ["no_cves", "NVD lists no CVE for this product"],
  ["unversioned_cves", "NVD lists CVEs for this product without version information; map it by hand if it should be checked"],
  ["store_package", "Store package: its version is the package's, not the product's. Map it by hand if the versions can be compared"],
  ["nvd_unavailable", "NVD did not answer; try again later"],
  ["already_mapped", "Already mapped"],
  ["ignored", "On the ignore list"],
  ["no_dictionary", "The NVD product dictionary has not been loaded yet (Vulnerability Data > Sync NVD Dictionary loads it)"],
  ["empty_name", "No name"],
]);

/** Why a program got no automatic mapping; a reason code without a text is shown as it is. */
export function autoResolveReasonText(reason: string): string {
  return AUTO_RESOLVE_REASONS.get(reason) ?? reason;
}

/**
 * The programs an auto-map run left unmapped, one line per reason ("{reason text}: Name A, Name B"),
 * the reasons in the order they first came up.
 */
export function failedByReasonLines(failed: readonly AutoResolveFailedItem[]): string[] {
  const programsByReason = new Map<string, string[]>();
  for (const item of failed) {
    const programs = programsByReason.get(item.reason) ?? [];
    if (item.softwareName) programs.push(item.softwareName);
    programsByReason.set(item.reason, programs);
  }
  return [...programsByReason].map(([reason, programs]) => {
    const text = autoResolveReasonText(reason);
    return programs.length > 0 ? `${text}: ${programs.join(", ")}` : text;
  });
}

/**
 * Notice after a saved mapping. The response carries no count when the inventory rows could not
 * be brought in line right away; they follow with the nightly run.
 */
export function mappingSavedNotice(inventoryRowsChanged: number | null | undefined): string {
  if (inventoryRowsChanged == null) return "Mapping saved.";
  return `Mapping saved. ${inventoryRowsChanged} inventory ${inventoryRowsChanged === 1 ? "row" : "rows"} updated.`;
}

/** The product a mapping points to, as the flags endpoint names it ("google:chrome"); null without one. */
export function cpeProductKey(mapping: Pick<CpeMappingItem, "cpeVendor" | "cpeProduct">): string | null {
  return mapping.cpeVendor && mapping.cpeProduct ? `${mapping.cpeVendor}:${mapping.cpeProduct}`.toLowerCase() : null;
}

/** A flag belongs to the product: every mapping that points to it takes the saved state. */
export function withProductFlags(
  mappings: readonly CpeMappingItem[],
  product: string,
  flags: Pick<CpeMappingItem, "selfUpdating" | "selfUpdatingDefault">,
): CpeMappingItem[] {
  return mappings.map((m) =>
    cpeProductKey(m) === product
      ? { ...m, selfUpdating: flags.selfUpdating, selfUpdatingDefault: flags.selfUpdatingDefault }
      : m,
  );
}

/** One mapping as an entry of a mapping list. */
interface MappingListEntry {
  normalizedVendor: string;
  normalizedProduct: string;
  displayNamePatterns: string[];
  publisherPatterns: string[];
  excludePatterns: string[];
  cpeUri: string;
  category: string;
}

/**
 * What the export writes: the operator's own mappings as list entries, and nothing else. It is
 * deliberately not a complete mapping list: the maintained lists also carry editions and the
 * self-updating defaults, which the listing does not hold, so a file that looked like one would
 * drop them when committed in its place.
 */
export function customMappingEntries(mappings: readonly CpeMappingItem[]): MappingListEntry[] {
  return mappings
    .filter((m) => m.source === "custom")
    .map((m) => ({
      normalizedVendor: m.normalizedVendor,
      normalizedProduct: m.normalizedProduct,
      displayNamePatterns: m.displayNamePatterns,
      publisherPatterns: m.publisherPatterns,
      excludePatterns: m.excludePatterns || [],
      cpeUri: m.cpeUri,
      category: m.category,
    }));
}
