/**
 * The Entra tenant region for the Global Admin views: `tenantRegionScope` is Microsoft's raw
 * `tenant_region_scope` code from the tenant's public sign-in metadata. Microsoft documents no list of
 * codes, so the known ones get a label and a map anchor and any other code is shown as Microsoft sent it.
 * Pure: the list, the filter, the statistics line and the map all read a tenant's region through here.
 */

/** The fields of a tenant row the region views read. */
export interface TenantRegionFields {
  tenantRegionScope?: string | null;
  tenantRegionSubScope?: string | null;
}

interface KnownRegion {
  code: string;
  label: string;
  /** Where the map draws the region ([lat, lon]); Worldwide has no place on it. */
  anchor?: readonly [number, number];
}

/** The codes Microsoft is known to send, continents first in the order the filter lists them. */
export const KNOWN_REGIONS: readonly KnownRegion[] = [
  { code: "EU", label: "Europe", anchor: [50, 12] },
  { code: "NA", label: "North America", anchor: [42, -100] },
  { code: "SA", label: "South America", anchor: [-15, -58] },
  { code: "AS", label: "Asia", anchor: [32, 95] },
  { code: "OC", label: "Oceania", anchor: [-25, 135] },
  { code: "AF", label: "Africa", anchor: [5, 20] },
  { code: "WW", label: "Worldwide" },
];

const SUB_SCOPE_LABELS: Readonly<Record<string, string>> = {
  GCC: "US Government Community Cloud",
};

function knownRegion(code: string): KnownRegion | undefined {
  const upper = code.toUpperCase();
  return KNOWN_REGIONS.find((r) => r.code === upper);
}

/** The pill text: Microsoft's codes, e.g. "EU" or "NA · GCC"; null while the tenant has no region. */
export function regionCode(t: TenantRegionFields): string | null {
  if (!t.tenantRegionScope) return null;
  return t.tenantRegionSubScope ? `${t.tenantRegionScope} · ${t.tenantRegionSubScope}` : t.tenantRegionScope;
}

/** The pill's tooltip in plain words, e.g. "Entra tenant region: North America (US Government Community Cloud)". */
export function regionTitle(t: TenantRegionFields): string | null {
  if (!t.tenantRegionScope) return null;
  const label = knownRegion(t.tenantRegionScope)?.label ?? t.tenantRegionScope;
  const sub = t.tenantRegionSubScope;
  return `Entra tenant region: ${label}${sub ? ` (${SUB_SCOPE_LABELS[sub.toUpperCase()] ?? sub})` : ""}`;
}

/** Filter value of a tenant: a continent code, "other" for any other code, "none" without a region yet. */
export function regionFacetValue(t: TenantRegionFields): string {
  if (!t.tenantRegionScope) return "none";
  const region = knownRegion(t.tenantRegionScope);
  return region?.anchor ? region.code : "other";
}

export interface RegionCount {
  /** The region code (a known one upper-cased, any other one as Microsoft sent it). */
  code: string;
  label: string;
  count: number;
  anchor?: readonly [number, number];
}

export interface RegionOverview {
  /** Every region with at least one tenant, the largest first. */
  regions: RegionCount[];
  /** Tenants whose region has not been looked up yet. */
  unresolved: number;
}

/** Tenants per region; the sub-scope counts with its region (a GCC tenant is a North American one). */
export function regionOverview(tenants: readonly TenantRegionFields[]): RegionOverview {
  const counts = new Map<string, number>();
  let unresolved = 0;
  for (const t of tenants) {
    if (!t.tenantRegionScope) {
      unresolved++;
      continue;
    }
    const code = knownRegion(t.tenantRegionScope)?.code ?? t.tenantRegionScope;
    counts.set(code, (counts.get(code) ?? 0) + 1);
  }

  const order = (code: string) => {
    const i = KNOWN_REGIONS.findIndex((r) => r.code === code);
    return i < 0 ? KNOWN_REGIONS.length : i;
  };
  const regions = [...counts.entries()]
    .map(([code, count]): RegionCount => {
      const known = knownRegion(code);
      return { code, label: known?.label ?? code, count, anchor: known?.anchor };
    })
    .sort((a, b) => b.count - a.count || order(a.code) - order(b.code) || a.code.localeCompare(b.code));
  return { regions, unresolved };
}
